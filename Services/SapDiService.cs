using Microsoft.Extensions.Options;
using SAPbobsCOM;
using SohoSapIntegrator.Models;

namespace SohoSapIntegrator.Services;

/// <summary>
/// Clase que contiene las opciones de configuracin para conectarse a la DI API de SAP.
/// Los valores se cargan desde la seccin "SapDi" del archivo appsettings.json.
/// </summary>
public sealed class SapDiOptions
{
    public string Server { get; set; } = "";
    public string DbServerType { get; set; } = "dst_MSSQL2016";
    public string CompanyDb { get; set; } = "";
    public string DbUser { get; set; } = "";
    public string DbPassword { get; set; } = "";
    public string UserName { get; set; } = "";
    public string Password { get; set; } = "";
    public string LicenseServer { get; set; } = "";
    public bool UseTrusted { get; set; } = false;
}

/// <summary>
/// Servicio responsable de la comunicacin con la DI API de SAP Business One.
/// Su funcin principal es crear un Pedido de Venta en SAP a partir de los datos recibidos.
/// </summary>
public sealed class SapDiService : ISapDiService
{
    private readonly SapDiOptions _opt;
    private readonly IConfiguration _cfg;

    /// <summary>
    /// Constructor que inyecta las dependencias necesarias.
    /// </summary>
    /// <param name="opt">Opciones de configuracin para la conexin a SAP DI API.</param>
    /// <param name="cfg">Configuracin general de la aplicacin para acceder a otros valores.</param>
    public SapDiService(IOptions<SapDiOptions> opt, IConfiguration cfg)
    {
        _opt = opt.Value;
        _cfg = cfg;
    }

    /// <summary>
    /// Crea un Pedido de Venta en SAP basado en el payload de Soho.
    /// </summary>
    /// <param name="env">El objeto que contiene todos los datos del pedido de Soho.</param>
    /// <returns>Una tupla con el DocEntry y DocNum del pedido creado en SAP.</returns>
    /// <exception cref="InvalidOperationException">Se lanza si la conexin o la creacin del documento fallan.</exception>
    public (int DocEntry, int DocNum) CreateSalesOrder(SohoEnvelope env)
    {
        // El objeto Company es el punto de entrada principal para todas las operaciones de la DI API.
        var company = new Company();

        try
        {
            // --- PASO 1: Configurar y establecer la conexin con SAP ---
            company.Server = _opt.Server;
            company.CompanyDB = _opt.CompanyDb;
            company.LicenseServer = _opt.LicenseServer;

            company.DbUserName = _opt.DbUser;
            company.DbPassword = _opt.DbPassword;

            company.UserName = _opt.UserName;
            company.Password = _opt.Password;

            company.UseTrusted = _opt.UseTrusted;

            company.DbServerType = ParseDbServerType(_opt.DbServerType);

            // Intenta conectar a la base de datos de la compaa.
            var rc = company.Connect();
            if (rc != 0)
            {
                // Si la conexin falla, obtiene el error de SAP y lanza una excepcin.
                company.GetLastError(out var errCode, out var errMsg);
                throw new InvalidOperationException($"Fallo al conectar a la DI API: {errCode} - {errMsg}");
            }


            // --- PASO 2: Mapeo de datos de Soho a un objeto de Pedido de Venta de SAP ---

            // Obtiene valores por defecto desde la configuracin (appsettings.json).
            // Estos se usan si el payload no provee informacin especfica o si la regla de negocio as lo define.
            var defaultCardCode = _cfg["Soho:DefaultCardCode"] ?? "CFMANTA";
            var defaultSlpCode = int.Parse(_cfg["Soho:DefaultSlpCode"] ?? "26");

            var t = env.BusinessObject.Transaction;

            // Se obtiene un objeto de documento vaco (Pedido de Venta) de SAP.
            Documents doc = (Documents)company.GetBusinessObject(BoObjectTypes.oOrders);

            // ---- Mapeo de Cabecera ----
            
            // CardCode (Cliente): Se usa un cliente genrico por defecto.
            // La lgica para buscar un cliente por cdula/RUC fue descartada segn anlisis previo.
            doc.CardCode = defaultCardCode;

            // NumAtCard (N Ref. Cliente/Proveedor): Se usa para guardar el ID del pedido de Zoho.
            // Esto es crucial para la trazabilidad y la idempotencia externa.
            doc.NumAtCard = env.ZohoOrderId;

            // SalesPersonCode (Vendedor): Se usa un cdigo de vendedor por defecto.
            doc.SalesPersonCode = defaultSlpCode;

            // Fechas del documento.
            if (DateTime.TryParse(t.Date, out var dt))
                doc.DocDate = dt; // Fecha de contabilizacin
            
            // ---- Mapeo de Lneas ----
            bool first = true;
            foreach (var it in t.SaleItemList)
            {
                if (!first) doc.Lines.Add(); // Se aade una nueva lnea a partir de la segunda.
                first = false;

                doc.Lines.ItemCode = it.ProductId; // Cdigo del artculo.
                doc.Lines.Quantity = (double)it.Quantity; // Cantidad.

                // Precio: Se asume que Soho enva el precio final con impuestos/descuentos ya aplicados.
                // No se usan listas de precios de SAP.
                doc.Lines.Price = (double)it.Price;

                // Descuento a nivel de lnea (en porcentaje).
                doc.Lines.DiscountPercent = (double)it.Discount;

                // Almacn: Se asigna un almacn por defecto a cada lnea.
                var whs = _cfg["Soho:DefaultWarehouseCode"];
                if (!string.IsNullOrWhiteSpace(whs))
                    doc.Lines.WarehouseCode = whs;
            }


            // --- PASO 3: Crear el documento en SAP y obtener su identificador ---

            // Intenta aadir el documento a la base de datos.
            var addRc = doc.Add();
            if (addRc != 0)
            {
                // Si falla, obtiene el mensaje de error de SAP y lanza una excepcin.
                company.GetLastError(out var addErrCode, out var addErrMsg);
                throw new InvalidOperationException($"Fallo al aadir el pedido (DI Add): {addErrCode} - {addErrMsg}");
            }

            // Despus de aadir, SAP nos da el DocEntry del nuevo documento.
            var key = company.GetNewObjectKey();
            if (!int.TryParse(key, out var docEntry))
                throw new InvalidOperationException("No se pudo parsear el GetNewObjectKey() a entero. Key=" + key);

            // El DocNum no se devuelve directamente, as que hay que leer el documento recin creado para obtenerlo.
            Documents doc2 = (Documents)company.GetBusinessObject(BoObjectTypes.oOrders);
            if (!doc2.GetByKey(docEntry))
                throw new InvalidOperationException("Pedido creado pero no se pudo leer con GetByKey(docEntry=" + docEntry + ")");

            // Devuelve los identificadores del nuevo pedido.
            return (DocEntry: docEntry, DocNum: doc2.DocNum);
        }
        finally
        {
            // --- PASO 4: Desconexin ---
            // Es VITAL desconectarse de la DI API para liberar la licencia y los recursos.
            // El bloque 'finally' asegura que esto ocurra incluso si hay una excepcin.
            if (company.Connected)
                company.Disconnect();
        }
    }

    /// <summary>
    /// Convierte el tipo de servidor de base de datos de string (desde appsettings.json)
    /// al tipo de enumeracin que espera la DI API.
    /// </summary>
    private static BoDataServerTypes ParseDbServerType(string s) =>
        s switch
        {
            "dst_MSSQL2016" => BoDataServerTypes.dst_MSSQL2016,
            "dst_MSSQL2014" => BoDataServerTypes.dst_MSSQL2014,
            "dst_MSSQL2012" => BoDataServerTypes.dst_MSSQL2012,
            "dst_MSSQL2019" => BoDataServerTypes.dst_MSSQL2019,
            "dst_MSSQL2017" => BoDataServerTypes.dst_MSSQL2017,
            _ => BoDataServerTypes.dst_MSSQL2016 // Valor por defecto.
        };
}