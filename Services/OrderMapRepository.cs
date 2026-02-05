using Microsoft.Data.SqlClient;
using SohoSapIntegrator.Models;
using System.Data;

namespace SohoSapIntegrator.Services;

/// <summary>
/// Enumera los posibles resultados de la operacin de inicio de idempotencia (TryBeginAsync).
/// </summary>
public enum BeginCode
{
    Started,          // El procesamiento ha comenzado con xito.
    DuplicateCreated, // El pedido ya haba sido creado previamente.
    InProgress,       // El pedido est siendo procesado por otra solicitud concurrente.
    ConflictHash      // El mismo ID de pedido lleg con un contenido diferente.
}

/// <summary>
/// Representa el resultado de la operacin de inicio de idempotencia.
/// </summary>
public sealed record BeginResult(
    BeginCode Code,
    string? PayloadHash,
    int? SapDocEntry,
    int? SapDocNum
);

/// <summary>
/// Representa el resultado de la operacin de pre-validacin.
/// </summary>
public sealed record PreValidationResult(bool Ok, string Message);

/// <summary>
/// Repositorio que gestiona la tabla 'Z_SOHO_OrderMap' para la idempotencia y el seguimiento de pedidos.
/// Esta clase es fundamental para evitar la duplicacin de pedidos en SAP.
/// </summary>
public sealed class OrderMapRepository
{
    private readonly IConfiguration _cfg;
    private readonly ILogger<OrderMapRepository> _log;

    public OrderMapRepository(IConfiguration cfg, ILogger<OrderMapRepository> log)
    {
        _cfg = cfg;
        _log = log;
    }

    /// <summary>
    /// Crea y devuelve una nueva conexin a la base de datos SQL Server.
    /// </summary>
    private SqlConnection CreateConn()
        => new SqlConnection(_cfg.GetConnectionString("SqlServer"));

    /// <summary>
    /// Mtodo central de idempotencia. Intenta iniciar el procesamiento de un pedido de forma atmica y segura.
    /// </summary>
    /// <returns>Un objeto BeginResult que indica el estado del inicio del procesamiento.</returns>
    public async Task<BeginResult> TryBeginAsync(string zohoOrderId, string instanceId, string payloadHash, CancellationToken ct)
    {
        await using var conn = CreateConn();
        await conn.OpenAsync(ct);

        // Inicia una transaccin SQL. Esto asegura que la secuencia de lectura-escritura
        // sea atmica y evita condiciones de carrera (race conditions).
        await using var tx = await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);

        // --- PASO 1: Comprobar si el pedido ya existe en la tabla de mapeo ---
        // Se usa WITH (UPDLOCK, HOLDLOCK) para bloquear la fila (o el rango donde debera estar)
        // durante toda la transaccin. Esto obliga a cualquier otra solicitud concurrente para el mismo
        // pedido a esperar a que esta transaccin termine, serializando el acceso y evitando duplicados.
        var selectCmd = new SqlCommand(@"
SELECT TOP 1 Status, PayloadHash, SapDocEntry, SapDocNum
FROM dbo.Z_SOHO_OrderMap WITH (UPDLOCK, HOLDLOCK)
WHERE ZohoOrderId=@zoho AND InstanceId=@inst;
", conn, (SqlTransaction)tx);

        selectCmd.Parameters.AddWithValue("@zoho", zohoOrderId);
        selectCmd.Parameters.AddWithValue("@inst", instanceId);

        string? status = null;
        string? existingHash = null;
        int? sapDocEntry = null;
        int? sapDocNum = null;

        await using (var r = await selectCmd.ExecuteReaderAsync(ct))
        {
            if (await r.ReadAsync(ct))
            {
                status = r["Status"] as string;
                existingHash = r["PayloadHash"] as string;
                sapDocEntry = r["SapDocEntry"] == DBNull.Value ? null : (int?)Convert.ToInt32(r["SapDocEntry"]);
                sapDocNum = r["SapDocNum"] == DBNull.Value ? null : (int?)Convert.ToInt32(r["SapDocNum"]);
            }
        }

        // --- PASO 2: Decidir qu hacer segn el estado encontrado ---
        if (status is not null) // El registro ya existe.
        {
            // Conflicto de Hash: El ID es el mismo, pero el contenido del payload es diferente.
            if (!string.IsNullOrWhiteSpace(existingHash) && !string.Equals(existingHash, payloadHash, StringComparison.OrdinalIgnoreCase))
            {
                await tx.CommitAsync(ct);
                return new BeginResult(BeginCode.ConflictHash, existingHash, sapDocEntry, sapDocNum);
            }

            // Duplicado: El pedido ya fue creado con xito.
            if (string.Equals(status, "CREATED", StringComparison.OrdinalIgnoreCase))
            {
                await tx.CommitAsync(ct);
                return new BeginResult(BeginCode.DuplicateCreated, existingHash ?? payloadHash, sapDocEntry, sapDocNum);
            }

            // En Progreso: Otra solicitud est procesando este pedido ahora mismo.
            if (string.Equals(status, "PROCESSING", StringComparison.OrdinalIgnoreCase))
            {
                await tx.CommitAsync(ct);
                return new BeginResult(BeginCode.InProgress, existingHash ?? payloadHash, sapDocEntry, sapDocNum);
            }

            // Fallido: El pedido fall en un intento anterior. Se permite el reintento.
            // Se actualiza el estado de nuevo a 'PROCESSING'.
            var upd = new SqlCommand(@"
UPDATE dbo.Z_SOHO_OrderMap
SET Status='PROCESSING',
    PayloadHash=@hash,
    ProcessingAt=SYSDATETIME(),
    UpdatedAt=SYSDATETIME(),
    ErrorMessage=NULL
WHERE ZohoOrderId=@zoho AND InstanceId=@inst;
", conn, (SqlTransaction)tx);

            upd.Parameters.AddWithValue("@hash", payloadHash);
            upd.Parameters.AddWithValue("@zoho", zohoOrderId);
            upd.Parameters.AddWithValue("@inst", instanceId);

            await upd.ExecuteNonQueryAsync(ct);
            await tx.CommitAsync(ct);

            return new BeginResult(BeginCode.Started, payloadHash, null, null);
        }

        // --- PASO 3: Si no existe, insertar un nuevo registro en estado 'PROCESSING' ---
        var ins = new SqlCommand(@"
INSERT INTO dbo.Z_SOHO_OrderMap (ZohoOrderId, InstanceId, PayloadHash, Status, ProcessingAt, CreatedAt, UpdatedAt)
VALUES (@zoho, @inst, @hash, 'PROCESSING', SYSDATETIME(), SYSDATETIME(), SYSDATETIME());
", conn, (SqlTransaction)tx);

        ins.Parameters.AddWithValue("@zoho", zohoOrderId);
        ins.Parameters.AddWithValue("@inst", instanceId);
        ins.Parameters.AddWithValue("@hash", payloadHash);

        await ins.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct);

        return new BeginResult(BeginCode.Started, payloadHash, null, null);
    }

    /// <summary>
    /// Actualiza el estado de un pedido a 'CREATED' y guarda los identificadores del documento de SAP.
    /// </summary>
    public async Task MarkCreatedAsync(string zohoOrderId, string instanceId, int docEntry, int docNum, CancellationToken ct)
    {
        await using var conn = CreateConn();
        await conn.OpenAsync(ct);

        var cmd = new SqlCommand(@"
UPDATE dbo.Z_SOHO_OrderMap
SET Status='CREATED',
    SapDocEntry=@de,
    SapDocNum=@dn,
    UpdatedAt=SYSDATETIME()
WHERE ZohoOrderId=@zoho AND InstanceId=@inst;
", conn);

        cmd.Parameters.AddWithValue("@de", docEntry);
        cmd.Parameters.AddWithValue("@dn", docNum);
        cmd.Parameters.AddWithValue("@zoho", zohoOrderId);
        cmd.Parameters.AddWithValue("@inst", instanceId);

        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Actualiza el estado de un pedido a 'FAILED' y guarda el mensaje de error.
    /// </summary>
    public async Task MarkFailedAsync(string zohoOrderId, string instanceId, string error, CancellationToken ct)
    {
        await using var conn = CreateConn();
        await conn.OpenAsync(ct);

        var cmd = new SqlCommand(@"
UPDATE dbo.Z_SOHO_OrderMap
SET Status='FAILED',
    ErrorMessage=@err,
    UpdatedAt=SYSDATETIME()
WHERE ZohoOrderId=@zoho AND InstanceId=@inst;
", conn);

        cmd.Parameters.Add("@err", SqlDbType.NVarChar).Value = error;
        cmd.Parameters.AddWithValue("@zoho", zohoOrderId);
        cmd.Parameters.AddWithValue("@inst", instanceId);

        await cmd.ExecuteNonQueryAsync(ct);
    }

    // --- SECCIN: Pre-validacin de Datos ---
    
    /// <summary>
    /// Realiza validaciones rpidas contra la base de datos de SAP usando SQL directo,
    /// antes de incurrir en el costo de una conexin a la DI API.
    /// </summary>
    /// <returns>Un resultado que indica si la validacin fue exitosa o el mensaje de error si fall.</returns>
    public async Task<PreValidationResult> PreValidateAsync(SohoEnvelope env, CancellationToken ct)
    {
        // Carga de configuraciones por defecto.
        var defaultCardCode = _cfg["Soho:DefaultCardCode"] ?? "";
        var defaultSlpCodeStr = _cfg["Soho:DefaultSlpCode"] ?? "";
        var defaultWhsCode = _cfg["Soho:DefaultWarehouseCode"] ?? "";

        if (string.IsNullOrWhiteSpace(defaultCardCode))
            return new(false, "Soho:DefaultCardCode no est configurado.");

        if (!int.TryParse(defaultSlpCodeStr, out var defaultSlpCode))
            return new(false, "Soho:DefaultSlpCode es invlido o no est configurado.");

        if (string.IsNullOrWhiteSpace(defaultWhsCode))
            return new(false, "Soho:DefaultWarehouseCode no est configurado.");

        // Extrae la lista de cdigos de producto nicos del pedido.
        var items = env.BusinessObject.Transaction.SaleItemList
            .Select(x => x.ProductId?.Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (items.Count == 0)
            return new(false, "La lista de artculos (SaleItemList) est vaca o no contiene ProductId.");

        await using var conn = CreateConn();
        await conn.OpenAsync(ct);

        // 1) Validar que el CardCode por defecto exista.
        if (!await ExistsAsync(conn, "SELECT 1 FROM OCRD WHERE CardCode=@p", new("@p", defaultCardCode), ct))
            return new(false, $"El CardCode por defecto '{defaultCardCode}' no existe en la tabla OCRD.");

        // 2) Validar que el SlpCode (vendedor) por defecto exista.
        if (!await ExistsAsync(conn, "SELECT 1 FROM OSLP WHERE SlpCode=@p", new("@p", defaultSlpCode), ct))
            return new(false, $"El SlpCode por defecto '{defaultSlpCode}' no existe en la tabla OSLP.");

        // 3) Validar que el Warehouse (almacn) por defecto exista.
        if (!await ExistsAsync(conn, "SELECT 1 FROM OWHS WHERE WhsCode=@p", new("@p", defaultWhsCode), ct))
            return new(false, $"El Warehouse por defecto '{defaultWhsCode}' no existe en la tabla OWHS.");

        // 4) Validar que TODOS los artculos del pedido existan en el maestro de artculos (OITM).
        // Se construye una consulta parametrizada para evitar inyeccin SQL.
        var paramNames = items.Select((_, i) => "@i" + i).ToArray();
        var sql = $"SELECT ItemCode FROM OITM WHERE ItemCode IN ({string.Join(",", paramNames)})";

        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var cmd = new SqlCommand(sql, conn))
        {
            for (int i = 0; i < items.Count; i++)
                cmd.Parameters.AddWithValue(paramNames[i], items[i]!);

            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                found.Add(r.GetString(0));
        }

        // Compara la lista de artculos del pedido con los encontrados en la base de datos.
        var missing = items.Where(x => !found.Contains(x!)).ToList();
        if (missing.Count > 0)
            return new(false, $"Los siguientes ItemCode no existen en OITM: {string.Join(", ", missing.Take(20))}" + (missing.Count > 20 ? "..." : ""));

        // Si todas las validaciones pasan, se devuelve un resultado exitoso.
        return new(true, "OK");
    }

    /// <summary>
    /// Mtodo de ayuda para verificar la existencia de un registro basado en una consulta SQL.
    /// </summary>
    private static async Task<bool> ExistsAsync(SqlConnection conn, string sql, SqlParameter p, CancellationToken ct)
    {
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add(p);
        var o = await cmd.ExecuteScalarAsync(ct);
        return o is not null;
    }
}