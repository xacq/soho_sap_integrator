# SohoSapIntegrator

API integradora para recibir pedidos desde Soho/Zoho y crearlos en SAP Business One de manera segura, trazable e idempotente.

## Punto de entrada (entry point)

El punto de entrada de la aplicación es **`Program.cs`**.

Desde ese archivo se inicializa todo el host de ASP.NET Core:
- configuración (`builder.Configuration`),
- inyección de dependencias (`builder.Services`),
- middlewares (`app.Use...`),
- y definición de endpoints (`app.MapPost`, `app.MapGet`).

Para ejecutar localmente:

```bash
dotnet run --project SohoSapIntegrator.csproj
```

Una vez levantada, la API expone principalmente:
- `POST /orders`
- `GET /orders/{zohoOrderId}/{instanceId}/status`


## Troubleshooting rápido

### Error `CS0246: SAPbobsCOM` / no levanta Swagger
Si Visual Studio muestra errores de `SAPbobsCOM` o `BoDataServerTypes`, **no es por el modelo `SohoPayload.cs`**.
El problema es la dependencia de SAP DI API en la máquina donde compilas/ejecutas.

- Verifica instalación de **SAP Business One DI API** (32/64 bits acorde a tu proceso).
- Verifica registro COM de `SAPbobsCOM.Company`.
- Si DI API no está instalada, el servicio no podrá crear pedidos en SAP.

> Nota: la API puede iniciar y mostrar Swagger, pero al crear pedidos (`POST /orders`) fallará hasta que DI API esté instalada y registrada.

## Estado del sistema (actual)

### ✅ Implementado
- `POST /orders` para creación de pedidos en SAP.
- Idempotencia por `zohoOrderId + instanceId` con persistencia en `Z_SOHO_OrderMap`.
- Pre-validación rápida de maestros SAP (`OCRD`, `OSLP`, `OWHS`, `OITM`) por SQL directo.
- Seguridad por API key (`X-API-KEY`).
- `GET /orders/{zohoOrderId}/{instanceId}/status` para observabilidad funcional.

### ⏳ Pendiente
- Callback saliente desacoplado para cierre/confirmación hacia endpoint externo.
- Mejoras de hardening (reconciliación SAP vs SQL, expiración de `PROCESSING`, validación avanzada de payload).

## Endpoints

### 1) Crear pedidos
`POST /orders`

Body esperado: arreglo JSON de `SohoEnvelope`.

Respuesta:
- `200 OK` con resultado por cada pedido (`CREATED`, `DUPLICATE`, `IN_PROGRESS`, `CONFLICT_HASH`, `ERROR`, `PREVALIDATION`, etc.).

### 2) Consultar estado
`GET /orders/{zohoOrderId}/{instanceId}/status`

Respuesta:
- `200 OK`:
```json
{
  "ok": true,
  "code": "STATUS",
  "zohoOrderId": "...",
  "instanceId": "...",
  "status": "CREATED",
  "sap": { "docEntry": 123, "docNum": 456 },
  "errorMessage": null,
  "updatedAt": "2026-01-01T12:34:56"
}
```
- `404 NOT_FOUND` si no existe el registro.
- `400 BAD_REQUEST` si faltan parámetros.

## Tabla de idempotencia

`dbo.Z_SOHO_OrderMap` (mínimo esperado):
- `ZohoOrderId` (PK)
- `InstanceId` (PK)
- `PayloadHash`
- `Status` (`PROCESSING`, `CREATED`, `FAILED`)
- `SapDocEntry`, `SapDocNum`
- `ErrorMessage`
- `ProcessingAt`, `CreatedAt`, `UpdatedAt`

## Configuración

Revisar `appsettings.json`:
- `ConnectionStrings:SqlServer`
- `Soho:ApiKey`, `Soho:DefaultCardCode`, `Soho:DefaultSlpCode`, `Soho:DefaultWarehouseCode`
- `SapDi:*` (servidor, compañía, credenciales, license server)

## Documentación interna

- `Explicacion_Proyecto.md`: explicación funcional y técnica detallada.
- `ANALISIS_TECNICO_REPOSITORIO.md`: contraste del estado real del repositorio, brechas y recomendaciones.
