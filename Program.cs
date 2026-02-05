// Importaciones de libreras y namespaces necesarios.
using Microsoft.AspNetCore.Mvc;
using Microsoft.OpenApi.Models;
using SohoSapIntegrator.Models;
using SohoSapIntegrator.Services;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

// --- INICIO DE CONFIGURACIN DE LA APLICACIN WEB ---

var builder = WebApplication.CreateBuilder(args);

// --- SECCIN: Inyeccin de Dependencias (DI) ---
// Aqu se registran los servicios que la aplicacin utilizar.

// Vincula la seccin "SapDi" del appsettings.json a la clase de opciones SapDiOptions.
// Esto permite inyectar la configuracin de la DI API de SAP de forma segura y tipada.
builder.Services.Configure<SapDiOptions>(builder.Configuration.GetSection("SapDi"));

// Registra OrderMapRepository con un tiempo de vida "Scoped".
// Esto significa que se crear una nueva instancia para cada solicitud HTTP,
// lo cual es ideal para repositorios que usan conexiones a base de datos.
builder.Services.AddScoped<OrderMapRepository>();

// Registra SapDiService con un tiempo de vida "Transient".
// Se crea una nueva instancia cada vez que se solicita. Esto es CRTICO para la DI API de SAP
// porque los objetos COM de SAP no son seguros para usarse en mltiples hilos (threads) a la vez.
// Un tiempo de vida Scoped o Singleton podra causar fallos impredecibles o corrupcin de datos.
builder.Services.AddTransient<ISapDiService, SapDiService>();

// Registra los servicios para generar la documentacin de la API con Swagger/OpenAPI.
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo { Title = "SohoSapIntegrator", Version = "v1" });

    // Define un esquema de seguridad para la API Key.
    // Esto permitir a los usuarios probar el endpoint desde la interfaz de Swagger
    // proporcionando la clave en la cabecera X-API-KEY.
    c.AddSecurityDefinition("ApiKey", new OpenApiSecurityScheme
    {
        In = ParameterLocation.Header,
        Name = "X-API-KEY",
        Type = SecuritySchemeType.ApiKey,
        Description = "API Key para autorizacin"
    });

    // Aplica el requisito de la API Key a todos los endpoints.
    c.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme { Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "ApiKey" } },
            Array.Empty<string>()
        }
    });
});

// Construye la aplicacin con los servicios configurados.
var app = builder.Build();


// --- SECCIN: Configuracin del Pipeline de Middlewares ---
// El pipeline define cmo se procesa cada solicitud HTTP. El orden es importante.

// Redirige automticamente las solicitudes HTTP a HTTPS para mayor seguridad.
app.UseHttpsRedirection();

// Habilita Swagger solo en el entorno de desarrollo para evitar exponer la documentacin en produccin.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// Middleware personalizado para validar la API Key.
// Protege el endpoint de accesos no autorizados.
// La clave se configura en appsettings.json bajo "Soho:ApiKey". Si no existe, la validacin se omite.
app.Use(async (ctx, next) =>
{
    var configuredKey = app.Configuration["Soho:ApiKey"];
    // Si no hay clave configurada, se salta la validacin. til para entornos de prueba locales.
    if (string.IsNullOrWhiteSpace(configuredKey))
    {
        await next();
        return;
    }

    // Comprueba si la cabecera X-API-KEY existe y si su valor coincide con el configurado.
    if (!ctx.Request.Headers.TryGetValue("X-API-KEY", out var provided) ||
        !string.Equals(provided.ToString(), configuredKey, StringComparison.Ordinal))
    {
        // Si la clave no es vlida, se devuelve un error 401 Unauthorized y se detiene el procesamiento.
        ctx.Response.StatusCode = 401;
        await ctx.Response.WriteAsJsonAsync(new { ok = false, code = "UNAUTHORIZED", message = "Falta o es invlida la API key." });
        return;
    }

    // Si la clave es vlida, se pasa la solicitud al siguiente middleware en el pipeline.
    await next();
});


// --- SECCIN: Definicin de Endpoints de la API ---

// Define el endpoint principal que recibe los pedidos de Soho.
// Acepta solicitudes POST en la ruta "/orders".
app.MapPost("/orders", async (
    [FromBody] List<SohoEnvelope> payload, // El cuerpo de la solicitud, deserializado a una lista de objetos.
    OrderMapRepository repo,               // Inyeccin del repositorio para idempotencia.
    ISapDiService sap,                     // Inyeccin del servicio para interactuar con SAP.
    ILoggerFactory loggerFactory,          // Fbrica para crear un logger especfico para este endpoint.
    CancellationToken ct) =>                // Token para manejar cancelaciones de la solicitud.
{
    var log = loggerFactory.CreateLogger("OrdersEndpoint");

    // Validacin bsica: el payload no puede ser nulo o vaco.
    if (payload is null || payload.Count == 0)
        return Results.BadRequest(new { ok = false, code = "BAD_REQUEST", message = "Payload vaco (se espera un arreglo con 1 o ms elementos)." });

    // Lista para almacenar el resultado de cada pedido procesado.
    var results = new List<object>();

    // Procesa cada "envelope" (pedido) que viene en el payload.
    foreach (var env in payload)
    {
        var zohoOrderId = env?.ZohoOrderId?.Trim();
        var instanceId = env?.InstanceId?.Trim();

        // Cada pedido debe tener su ID de Zoho y de la instancia.
        if (string.IsNullOrWhiteSpace(zohoOrderId) || string.IsNullOrWhiteSpace(instanceId))
        {
            results.Add(new { ok = false, code = "VALIDATION", message = "Falta zohoOrderId o instanceId." });
            continue; // Salta al siguiente pedido en el bucle.
        }

        // Calcula un hash SHA256 del contenido del pedido.
        // Esto sirve para detectar si un mismo ID de pedido llega con datos diferentes en solicitudes distintas.
        var payloadJson = JsonSerializer.Serialize(env, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        var payloadHash = Sha256Hex(payloadJson);


        // --- PASO 1: Gestin de Idempotencia ---
        // Se intenta iniciar el procesamiento en la tabla de mapeo.
        var begin = await repo.TryBeginAsync(zohoOrderId, instanceId, payloadHash, ct);

        // Se evala el resultado del intento de inicio.
        if (begin.Code == BeginCode.DuplicateCreated)
        {
            // El pedido ya fue creado exitosamente en una solicitud anterior.
            // Se devuelve el resultado guardado sin procesar nada de nuevo.
            results.Add(new
            {
                ok = true,
                code = "DUPLICATE",
                zohoOrderId,
                instanceId,
                payloadHash = begin.PayloadHash,
                sap = new { docEntry = begin.SapDocEntry, docNum = begin.SapDocNum }
            });
            continue;
        }
        if (begin.Code == BeginCode.InProgress)
        {
            // Hay otra solicitud procesando este mismo pedido en este momento.
            // Se devuelve un error para que el cliente reintente ms tarde.
            results.Add(new
            {
                ok = false,
                code = "IN_PROGRESS",
                zohoOrderId,
                instanceId,
                message = "Orden ya est en procesamiento (idempotencia). Reintenta en unos segundos."
            });
            continue;
        }
        if (begin.Code == BeginCode.ConflictHash)
        {
            // El mismo ID de pedido lleg con un contenido diferente (hash no coincide).
            // Esto es un conflicto y se rechaza.
            results.Add(new
            {
                ok = false,
                code = "CONFLICT_HASH",
                zohoOrderId,
                instanceId,
                message = "Mismo zohoOrderId+instanceId lleg con payload diferente (hash distinto)."
            });
            continue;
        }


        // --- PASO 2: Pre-validacin de datos contra SAP (va SQL) ---
        // Antes de conectar a la DI API, se valida que los datos maestros existan. Es mucho ms rpido.
        var pre = await repo.PreValidateAsync(env, ct);
        if (!pre.Ok)
        {
            // Si la pre-validacin falla, se marca el pedido como FAILED y se reporta el error.
            await SafeMarkFailed(repo, zohoOrderId, instanceId, $"PREVALIDATION: {pre.Message}", log, ct);
            results.Add(new { ok = false, code = "PREVALIDATION", zohoOrderId, instanceId, message = pre.Message });
            continue;
        }


        // --- PASO 3: Creacin del Pedido de Venta en SAP ---
        try
        {
            // Se llama al servicio que encapsula la lgica de la DI API.
            var created = sap.CreateSalesOrder(env);

            // Si la creacin es exitosa, se marca el pedido como CREATED en la tabla de mapeo.
            await repo.MarkCreatedAsync(zohoOrderId, instanceId, created.DocEntry, created.DocNum, ct);

            log.LogInformation("CREATED zohoOrderId={ZohoOrderId} instanceId={InstanceId} DocEntry={DocEntry} DocNum={DocNum}",
                zohoOrderId, instanceId, created.DocEntry, created.DocNum);

            // Se aade el resultado exitoso a la lista de respuestas.
            results.Add(new
            {
                ok = true,
                code = "CREATED",
                zohoOrderId,
                instanceId,
                payloadHash,
                sap = new { docEntry = created.DocEntry, docNum = created.DocNum }
            });
        }
        catch (Exception ex)
        {
            // Si ocurre cualquier excepcin durante la creacin en SAP...
            // 1. Se marca el pedido como FAILED en la base de datos con el mensaje de error.
            await SafeMarkFailed(repo, zohoOrderId, instanceId, ex.ToString(), log, ct);

            // 2. Se registra el error completo para diagnstico.
            log.LogError(ex, "ERROR zohoOrderId={ZohoOrderId} instanceId={InstanceId}", zohoOrderId, instanceId);

            // 3. Se aade un resultado de error a la lista de respuestas.
            results.Add(new
            {
                ok = false,
                code = "ERROR",
                zohoOrderId,
                instanceId,
                message = ex.Message // Solo se expone el mensaje principal al cliente.
            });
        }
    }

    // Se devuelve una respuesta 200 OK con la lista de resultados de cada pedido.
    return Results.Ok(new { ok = true, results });
})
.WithName("CreateSalesOrder") // Nombre para la generacin de OpenAPI.
.WithOpenApi();

// Inicia la aplicacin y la mantiene escuchando solicitudes.
app.Run();


// --- SECCIN: Mtodos de Ayuda (Helpers) ---

/// <summary>
/// Calcula el hash SHA256 de una cadena y lo devuelve como un string hexadecimal.
/// </summary>
static string Sha256Hex(string s)
{
    var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(s));
    var sb = new StringBuilder(bytes.Length * 2);
    foreach (var b in bytes) sb.Append(b.ToString("x2"));
    return sb.ToString();
}

/// <summary>
/// Envuelve la llamada a MarkFailedAsync en un bloque try-catch para evitar
/// que una falla al actualizar el estado a 'FAILED' detenga el procesamiento de otros pedidos.
/// </summary>
static async Task SafeMarkFailed(OrderMapRepository repo, string zohoOrderId, string instanceId, string error, ILogger log, CancellationToken ct)
{
    try
    {
        await repo.MarkFailedAsync(zohoOrderId, instanceId, error, ct);
    }
    catch (Exception ex2)
    {
        // Si incluso marcar el fallo falla, se registra como un error crtico.
        log.LogError(ex2, "CRITICAL: MarkFailedAsync tambin fall para zohoOrderId={ZohoOrderId} instanceId={InstanceId}", zohoOrderId, instanceId);
    }
}