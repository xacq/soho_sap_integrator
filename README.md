# SohoSapIntegrator

Una API web robusta y segura que integra pedidos de venta desde sistemas externos (Soho/Zoho) hacia SAP Business One, garantizando **idempotencia**, **validación rápida** y **trazabilidad completa**.

## 🎯 Descripción

**SohoSapIntegrator** es un servicio que actúa como intermediario entre un sistema de gestión de pedidos (Soho/Zoho) y SAP Business One. Su propósito es recibir pedidos de venta de forma segura, validarlos eficientemente y crear los registros correspondientes en SAP, garantizando que cada pedido se procese exactamente una vez, incluso ante reintentos.

### Características Principales

- ✅ **Idempotencia Garantizada**: Evita la creación de pedidos duplicados mediante control de transacciones SQL y hashes de contenido
- ✅ **Validación Rápida**: Pre-valida datos maestros (clientes, artículos, almacenes) con consultas SQL antes de conectar a SAP
- ✅ **Trazabilidad Completa**: Registra cada intento de procesamiento con estado y mensajes de error detallados
- ✅ **Seguridad**: Protección de endpoints mediante API Key en cabecera HTTP
- ✅ **Documentación Interactiva**: Swagger/OpenAPI integrado para desarrollo y pruebas
- ✅ **Manejo Robusto de Errores**: Transacciones seguras con rollback automático ante fallos

---

## 🏗️ Arquitectura

### Stack Tecnológico

- **Framework**: ASP.NET Core 8.0 (Minimal APIs)
- **Base de Datos**: SQL Server (para idempotencia y seguimiento)
- **Integración SAP**: DI API de SAP Business One (COM)
- **ORM/Acceso Datos**: ADO.NET (Microsoft.Data.SqlClient)
- **Autenticación**: API Key en cabecera HTTP
- **Documentación API**: Swagger/OpenAPI (Swashbuckle)

### Componentes Principales

```
SohoSapIntegrator/
├── Program.cs                      # Punto de entrada, configuración y endpoints
├── Models/
│   ├── SohoEnvelope.cs            # Modelo de datos de entrada desde Soho
│   └── SohoTransaction.cs         # Detalles de líneas de pedido
├── Services/
│   ├── ISapDiService.cs           # Interfaz para operaciones en SAP
│   ├── SapDiService.cs            # Implementación de creación de pedidos en SAP
│   └── OrderMapRepository.cs      # Gestión de idempotencia y base de datos
├── Data/                          # Scripts SQL para inicialización
├── appsettings.json              # Configuración del proyecto
└── SohoSapIntegrator.http        # Archivo de pruebas HTTP (REST Client)
```

---

## 📋 Flujo de Procesamiento

```
┌─────────────────────────────────────────────────────────────┐
│ 1. RECEPCIÓN: POST /orders (con X-API-KEY header)          │
└────────────────┬────────────────────────────────────────────┘
                 │
┌────────────────▼────────────────────────────────────────────┐
│ 2. AUTENTICACIÓN: Validación de API Key                    │
└────────────────┬────────────────────────────────────────────┘
                 │
┌────────────────▼────────────────────────────────────────────┐
│ 3. IDEMPOTENCIA: TryBeginAsync                             │
│   - Calcula hash SHA256 del pedido                          │
│   - Bloquea fila en BD (UPDLOCK, HOLDLOCK)                 │
│   - Detecta: duplicados, conflictos, procesamiento actual   │
└────────────────┬────────────────────────────────────────────┘
                 │
┌────────────────▼────────────────────────────────────────────┐
│ 4. PRE-VALIDACIÓN: PreValidateAsync (SQL directo)          │
│   - Verifica cliente existe (OCRD)                          │
│   - Verifica vendedor existe (OSLP)                         │
│   - Verifica almacén existe (OWHS)                          │
│   - Verifica todos los artículos existen (OITM)            │
└────────────────┬────────────────────────────────────────────┘
                 │
┌────────────────▼────────────────────────────────────────────┐
│ 5. CREACIÓN EN SAP: CreateSalesOrder                        │
│   - Conecta a DI API de SAP                                 │
│   - Mapea datos: cabecera y líneas                          │
│   - Obtiene DocEntry y DocNum                               │
│   - Desconecta (liberación de licencia)                     │
└────────────────┬────────────────────────────────────────────┘
                 │
┌────────────────▼────────────────────────────────────────────┐
│ 6. CONFIRMACIÓN: MarkCreatedAsync o MarkFailedAsync        │
│   - Actualiza estado en Z_SOHO_OrderMap                     │
│   - Guarda IDs y errores                                    │
│   - Responde al cliente                                     │
└─────────────────────────────────────────────────────────────┘
```

---

## 🚀 Instalación y Configuración

### Requisitos Previos

- **.NET 8.0 SDK** o superior
- **SQL Server** (SQL Server 2016 o superior)
- **SAP Business One** con DI API disponible
- **Visual Studio 2022** o Visual Studio Code (opcional)

### Pasos de Instalación

#### 1. Clonar el Repositorio

```bash
git clone <repository-url>
cd SohoSapIntegrator
```

#### 2. Configurar la Base de Datos SQL

Crear la tabla de idempotencia:

```sql
CREATE TABLE Z_SOHO_OrderMap (
    ZohoOrderId VARCHAR(50) NOT NULL,
    InstanceId VARCHAR(50) NOT NULL,
    Status VARCHAR(20) NOT NULL,
    PayloadHash VARCHAR(64) NOT NULL,
    SapDocEntry INT NULL,
    SapDocNum INT NULL,
    ErrorMessage NVARCHAR(MAX) NULL,
    ProcessingAt DATETIME NOT NULL,
    CreatedAt DATETIME NOT NULL,
    UpdatedAt DATETIME NOT NULL,
    PRIMARY KEY (ZohoOrderId, InstanceId)
);
```

#### 3. Configurar appsettings.json

Actualizar el archivo `appsettings.json` con tus credenciales:

```json
{
    "ConnectionStrings": {
        "SqlServer": "Server=YOUR_SQL_SERVER;Database=YOUR_DB;User Id=YOUR_USER;Password=YOUR_PASSWORD;TrustServerCertificate=True;Encrypt=False;"
    },
    "Soho": {
        "ApiKey": "TU_CLAVE_API_SECRETA",
        "DefaultCardCode": "CODIGO_CLIENTE",
        "DefaultSlpCode": 1,
        "DefaultWarehouseCode": "01"
    },
    "SapDi": {
        "Server": "IP_O_HOSTNAME_SAP_DB",
        "DbServerType": "dst_MSSQL2016",
        "CompanyDb": "NOMBRE_BD_SAP",
        "DbUser": "USUARIO_BD_SAP",
        "DbPassword": "PASSWORD_BD_SAP",
        "UserName": "USUARIO_SAP",
        "Password": "PASSWORD_SAP",
        "LicenseServer": "IP_SERVIDOR_LICENCIAS:30000",
        "UseTrusted": false
    }
}
```

#### 4. Agregar Referencia COM a SAPbobsCOM

En Visual Studio:
1. Click derecho en el proyecto → **Add Reference**
2. Buscar y agregar **SAPbobsCOM**

Si no aparece, puede ser necesario instalar el SAP SDK en el servidor.

#### 5. Restaurar Dependencias y Compilar

```bash
dotnet restore
dotnet build
```

#### 6. Ejecutar la Aplicación

```bash
dotnet run
```

La aplicación estará disponible en: `https://localhost:5001`

Swagger estará disponible en: `https://localhost:5001/swagger`

---

## 📡 Uso de la API

### Endpoint Principal: POST /orders

**URL**: `POST https://localhost:5001/orders`

**Headers Requeridos**:
```
X-API-KEY: TU_CLAVE_API
Content-Type: application/json
```

**Cuerpo de la Solicitud** (Ejemplo):

```json
[
    {
        "ZohoOrderId": "ZOHO-123456",
        "InstanceId": "instance-001",
        "OrderDate": "2024-02-05T10:30:00Z",
        "Items": [
            {
                "ProductId": "ART-001",
                "Quantity": 5,
                "Price": 100.00,
                "Discount": 10
            },
            {
                "ProductId": "ART-002",
                "Quantity": 3,
                "Price": 50.00,
                "Discount": 0
            }
        ]
    }
]
```

**Respuesta Exitosa** (200 OK):

```json
{
    "ok": true,
    "code": "CREATED",
    "message": "Pedido creado exitosamente en SAP",
    "sapDocEntry": 1234,
    "sapDocNum": 2024001
}
```

**Respuestas de Error**:

| Status | Código | Descripción |
|--------|--------|-------------|
| 401 | UNAUTHORIZED | API Key inválida o ausente |
| 400 | DUPLICATE_CREATED | Pedido ya fue creado en SAP |
| 409 | IN_PROGRESS | Pedido está siendo procesado en otro hilo |
| 400 | CONFLICT_HASH | Datos del pedido conflictúan con registro anterior |
| 400 | VALIDATION_FAILED | Falló validación de datos maestros |
| 500 | SAP_ERROR | Error al crear pedido en SAP |

### Ejemplo con cURL

```bash
curl -X POST https://localhost:5001/orders \
  -H "X-API-KEY: TU_CLAVE_API" \
  -H "Content-Type: application/json" \
  -d @pedido.json
```

### Pruebas con Swagger UI

1. Acceder a `https://localhost:5001/swagger`
2. Hacer clic en "Authorize"
3. Ingresar la API Key
4. Expandir el endpoint `/orders`
5. Hacer clic en "Try it out"
6. Ingresar JSON de ejemplo y ejecutar

---

## 🔐 Seguridad

### API Key

- **Ubicación**: Cabecera HTTP `X-API-KEY`
- **Configuración**: Campo `Soho:ApiKey` en `appsettings.json`
- **Validación**: Middleware personalizado en `Program.cs`

### Base de Datos

- **Idempotencia**: Transacciones SQL con `UPDLOCK` y `HOLDLOCK` para evitar condiciones de carrera
- **Hash SHA256**: Detecta cambios en el contenido del pedido

### Conexión a SAP

- **Desconexión**: Siempre se ejecuta en bloque `finally` para liberar licencias
- **Instances Transient**: Cada solicitud obtiene una nueva instancia de `SapDiService`

### Recomendaciones

- ✅ Usar HTTPS en producción
- ✅ Cambiar la API Key por una cadena fuerte y única
- ✅ Mantener `appsettings.json` fuera del control de versiones
- ✅ Usar `appsettings.Production.json` con credenciales seguras
- ✅ Implementar rate limiting en producción

---

## 🛠️ Desarrollo

### Estructura de Archivos de Configuración

```
appsettings.json              # Configuración general (DEFAULT)
appsettings.Development.json  # Configuración para desarrollo (override)
appsettings.Production.json   # Configuración para producción (override)
```

### Variables de Entorno Alternativas

Si prefieres usar variables de entorno:

```bash
# Linux/Mac
export ConnectionStrings__SqlServer="..."
export Soho__ApiKey="..."
export SapDi__Server="..."

# PowerShell
$env:ConnectionStrings__SqlServer="..."
$env:Soho__ApiKey="..."
$env:SapDi__Server="..."
```

### Pruebas Manuales

Usar el archivo `SohoSapIntegrator.http` (REST Client) en VS Code:

```http
### Test de creación de pedido
POST https://localhost:5001/orders HTTP/1.1
X-API-KEY: CAMBIA_ESTA_LLAVE
Content-Type: application/json

[
    {
        "ZohoOrderId": "TEST-001",
        "InstanceId": "test-001",
        "OrderDate": "2024-02-05T10:30:00Z",
        "Items": [
            {
                "ProductId": "ART-001",
                "Quantity": 1,
                "Price": 100,
                "Discount": 0
            }
        ]
    }
]
```

---

## 📊 Base de Datos - Tabla Z_SOHO_OrderMap

| Columna | Tipo | Descripción |
|---------|------|-------------|
| **ZohoOrderId** | VARCHAR(50) | ID del pedido en Soho (PK) |
| **InstanceId** | VARCHAR(50) | ID de la instancia de envío (PK) |
| **Status** | VARCHAR(20) | `PROCESSING`, `CREATED`, `FAILED` |
| **PayloadHash** | VARCHAR(64) | Hash SHA256 del contenido |
| **SapDocEntry** | INT | DocEntry del pedido en SAP |
| **SapDocNum** | INT | DocNum (número visible) en SAP |
| **ErrorMessage** | NVARCHAR(MAX) | Mensaje de error si falló |
| **ProcessingAt** | DATETIME | Cuándo comenzó el procesamiento |
| **CreatedAt** | DATETIME | Cuándo se creó el registro |
| **UpdatedAt** | DATETIME | Última actualización |

---

## 🔄 Campos de Configuración

### Sección: ConnectionStrings

```json
"ConnectionStrings": {
    "SqlServer": "Server=YOUR_SQL_SERVER;Database=YOUR_DB;User Id=YOUR_USER;Password=YOUR_PASSWORD;TrustServerCertificate=True;Encrypt=False;"
}
```

### Sección: Soho

```json
"Soho": {
    "ApiKey": "Clave secreta para validación de requests",
    "DefaultCardCode": "Código de cliente por defecto en SAP",
    "DefaultSlpCode": "Código de vendedor/sales person",
    "DefaultWarehouseCode": "Código de almacén por defecto"
}
```

### Sección: SapDi

```json
"SapDi": {
    "Server": "IP o hostname del servidor de BD de SAP",
    "DbServerType": "Tipo de BD (dst_MSSQL2016, dst_MSSQL2019, etc.)",
    "CompanyDb": "Nombre de la BD de la compañía",
    "DbUser": "Usuario de BD para acceso a SAP",
    "DbPassword": "Contraseña de BD",
    "UserName": "Usuario de SAP",
    "Password": "Contraseña de usuario SAP",
    "LicenseServer": "IP:Puerto del servidor de licencias",
    "UseTrusted": "true si usa Windows Auth, false si usa usuario/contraseña"
}
```

---

## 🐛 Solución de Problemas

### Error: "Cannot Create COM Object"
- **Causa**: SAPbobsCOM no está instalado o no es accesible
- **Solución**: Instalar SAP SDK en el servidor, reiniciar Visual Studio

### Error: "Invalid License"
- **Causa**: Servidor de licencias inaccesible o sin licencias disponibles
- **Solución**: Verificar conectividad al servidor de licencias (puerto 30000)

### Error: "DUPLICATE_CREATED"
- **Causa**: El pedido ya fue procesado anteriormente
- **Solución**: Verificar DB Z_SOHO_OrderMap, puede ser reintentable si Status es `FAILED`

### Error: "IN_PROGRESS"
- **Causa**: Otra solicitud está procesando el mismo pedido
- **Solución**: Esperar a que se complete o revisar status en BD

### Error: "VALIDATION_FAILED"
- **Causa**: Cliente, vendedor, almacén o artículos no existen en SAP
- **Solución**: Crear los registros maestros en SAP o cambiar configuración

---

## 📝 Logs y Monitoreo

Los logs se escriben en la consola y en el archivo de configuración de logging:

```json
"Logging": {
    "LogLevel": {
        "Default": "Information",
        "Microsoft.AspNetCore": "Warning"
    }
}
```

Cambiar a `Debug` para más verbosidad en desarrollo:

```json
"LogLevel": {
    "Default": "Debug"
}
```

---

## 📄 Licencia

Este proyecto está bajo licencia propietaria. Contactar al propietario para más información.

---

## 📞 Soporte

Para reportar problemas o solicitar información:
- **Email**: info@greenpc.dev
- **Documentación**: Ver archivo `Explicacion_Proyecto.md`
- **Problemas**: Registrar en el repositorio/ticketing system

---

## 🔗 Referencias

- [ASP.NET Core Documentation](https://docs.microsoft.com/aspnet/core)
- [SAP Business One DI API](https://help.sap.com/viewer/product/SAP%20BUSINESS%20ONE/9.2/en-US)
- [Microsoft.Data.SqlClient](https://github.com/dotnet/SqlClient)
- [Swagger/OpenAPI](https://swagger.io/)

---

**Última actualización**: Febrero 2024  
**Versión**: 1.0.0
