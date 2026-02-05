using Microsoft.Extensions.Options;
using SohoSapIntegrator.Models;

namespace SohoSapIntegrator.Services;

/// <summary>
/// Clase que contiene las opciones de configuración para conectarse a la DI API de SAP.
/// Los valores se cargan desde la sección "SapDi" del archivo appsettings.json.
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
    public bool UseTrusted { get; set; }
}

/// <summary>
/// Servicio responsable de la comunicación con la DI API de SAP Business One.
/// </summary>
public sealed class SapDiService : ISapDiService
{
    private readonly SapDiOptions _opt;
    private readonly IConfiguration _cfg;

    public SapDiService(IOptions<SapDiOptions> opt, IConfiguration cfg)
    {
        _opt = opt.Value;
        _cfg = cfg;
    }

    public (int DocEntry, int DocNum) CreateSalesOrder(SohoEnvelope env)
    {
        var company = CreateSapCompany();

        try
        {
            company.Server = _opt.Server;
            company.CompanyDB = _opt.CompanyDb;
            company.LicenseServer = _opt.LicenseServer;
            company.DbUserName = _opt.DbUser;
            company.DbPassword = _opt.DbPassword;
            company.UserName = _opt.UserName;
            company.Password = _opt.Password;
            company.UseTrusted = _opt.UseTrusted;
            company.DbServerType = ParseDbServerType((Type)company.GetType(), _opt.DbServerType);

            var rc = (int)company.Connect();
            if (rc != 0)
            {
                company.GetLastError(out int errCode, out string errMsg);
                throw new InvalidOperationException($"Fallo al conectar a la DI API: {errCode} - {errMsg}");
            }

            var defaultCardCode = _cfg["Soho:DefaultCardCode"] ?? "CFMANTA";
            var defaultSlpCode = int.Parse(_cfg["Soho:DefaultSlpCode"] ?? "26");
            var t = env.BusinessObject.Transaction;

            dynamic doc = company.GetBusinessObject(ParseBoObjectType((Type)company.GetType(), "oOrders"));
            doc.CardCode = defaultCardCode;
            doc.NumAtCard = env.ZohoOrderId;
            doc.SalesPersonCode = defaultSlpCode;

            if (DateTime.TryParse(t.Date, out var dt))
                doc.DocDate = dt;

            var first = true;
            foreach (var it in t.SaleItemList)
            {
                if (!first)
                    doc.Lines.Add();
                first = false;

                doc.Lines.ItemCode = it.ProductId;
                doc.Lines.Quantity = (double)it.Quantity;
                doc.Lines.Price = (double)it.Price;
                doc.Lines.DiscountPercent = (double)it.Discount;

                var whs = _cfg["Soho:DefaultWarehouseCode"];
                if (!string.IsNullOrWhiteSpace(whs))
                    doc.Lines.WarehouseCode = whs;
            }

            var addRc = (int)doc.Add();
            if (addRc != 0)
            {
                company.GetLastError(out int addErrCode, out string addErrMsg);
                throw new InvalidOperationException($"Fallo al añadir el pedido (DI Add): {addErrCode} - {addErrMsg}");
            }

            var key = (string)company.GetNewObjectKey();
            if (!int.TryParse(key, out var docEntry))
                throw new InvalidOperationException("No se pudo parsear el GetNewObjectKey() a entero. Key=" + key);

            dynamic doc2 = company.GetBusinessObject(ParseBoObjectType((Type)company.GetType(), "oOrders"));
            if (!(bool)doc2.GetByKey(docEntry))
                throw new InvalidOperationException("Pedido creado pero no se pudo leer con GetByKey(docEntry=" + docEntry + ")");

            return (DocEntry: docEntry, DocNum: (int)doc2.DocNum);
        }
        finally
        {
            try
            {
                if ((bool)company.Connected)
                    company.Disconnect();
            }
            catch
            {
                // No romper el flujo principal por errores al liberar la conexión COM.
            }
        }
    }

    private static dynamic CreateSapCompany()
    {
        var companyType = Type.GetTypeFromProgID("SAPbobsCOM.Company", throwOnError: false);
        if (companyType is null)
        {
            throw new InvalidOperationException(
                "No se encontró SAP DI API (ProgID SAPbobsCOM.Company). " +
                "Instala el cliente DI API de SAP Business One y registra la librería COM en la máquina.");
        }

        return Activator.CreateInstance(companyType)
            ?? throw new InvalidOperationException("No fue posible crear una instancia de SAPbobsCOM.Company.");
    }

    private static object ParseBoObjectType(Type companyType, string enumName)
    {
        var enumType = companyType.Assembly.GetType("SAPbobsCOM.BoObjectTypes")
            ?? throw new InvalidOperationException("No se encontró el tipo SAPbobsCOM.BoObjectTypes en la DI API.");

        return Enum.Parse(enumType, enumName);
    }

    private static object ParseDbServerType(Type companyType, string dbServerType)
    {
        var enumType = companyType.Assembly.GetType("SAPbobsCOM.BoDataServerTypes")
            ?? throw new InvalidOperationException("No se encontró el tipo SAPbobsCOM.BoDataServerTypes en la DI API.");

        var normalized = dbServerType switch
        {
            "dst_MSSQL2014" => "dst_MSSQL2014",
            "dst_MSSQL2012" => "dst_MSSQL2012",
            "dst_MSSQL2017" => "dst_MSSQL2017",
            "dst_MSSQL2019" => "dst_MSSQL2019",
            _ => "dst_MSSQL2016"
        };

        return Enum.Parse(enumType, normalized);
    }
}
