# Explicación del Proyecto: SohoSapIntegrator

## 1. Resumen General

**Objetivo:** El proyecto `SohoSapIntegrator` es un servicio de API web diseñado para recibir pedidos de venta desde un sistema externo (llamado "Soho" o "Zoho") y crearlos de forma segura y fiable en SAP Business One.

**Características Principales:**
- **Idempotencia:** Garantiza que un mismo pedido no se pueda crear más de una vez, incluso si se recibe la misma solicitud múltiples veces.
- **Validación Rápida:** Verifica que los datos maestros (como artículos, cliente, etc.) existan en SAP *antes* de intentar crear el pedido, optimizando el rendimiento y dando retroalimentación inmediata.
- **Manejo de Errores y Trazabilidad:** Registra cada intento de creación en una base de datos, guardando el estado (Creado, Fallido, En Progreso) y los mensajes de error.
- **Seguridad:** Protege el punto de acceso a la API mediante una clave (API Key).

## 2. Flujo de Procesamiento de un Pedido

Este es el recorrido que hace un pedido desde que llega a la API hasta que se crea en SAP.

### Paso 0: Configuración e Inicio (Archivo: `Program.cs`)

- Al iniciar la aplicación, se configuran todos los servicios necesarios.
    - `SapDiService`: El servicio que habla con SAP. Se configura como **Transient**, lo que significa que se crea una nueva instancia para cada pedido. Esto es **crítico** para la estabilidad de la DI API de SAP, que no maneja bien múltiples operaciones simultáneas con el mismo objeto de conexión.
    - `OrderMapRepository`: El servicio que habla con la base de datos SQL para la idempotencia y el seguimiento.
    - **Swagger**: Se configura una interfaz de usuario para poder ver y probar la API en el entorno de desarrollo.

### Paso 1: Recepción de la Solicitud (Archivo: `Program.cs`, Endpoint: `POST /orders`)

1.  Una solicitud HTTP POST llega a la URL `/orders` de la API. El cuerpo de la solicitud (payload) es un arreglo JSON que contiene uno o más pedidos.
2.  **Autenticación:** Un middleware (código que se ejecuta antes del endpoint) intercepta la solicitud.
    - Busca una cabecera llamada `X-API-KEY`.
    - Compara el valor de esa cabecera con el valor configurado en `appsettings.json` (`Soho:ApiKey`).
    - Si la clave no es válida, rechaza la solicitud con un error `401 Unauthorized`. Si es válida, continúa.
3.  El `endpoint` recibe los datos y los deserializa en una lista de objetos `SohoEnvelope`.
4.  El código empieza a procesar cada `SohoEnvelope` de la lista, uno por uno en un bucle.

### Paso 2: Idempotencia - "No duplicar" (Archivo: `Services/OrderMapRepository.cs`, Función: `TryBeginAsync`)

Para cada pedido, antes de hacer nada, se debe asegurar que no se está procesando ya o que no se haya creado antes.

1.  **Cálculo de Hash:** Se calcula un "hash" (una firma digital única, SHA256) a partir del contenido del pedido (`payloadJson`). Esto sirve para detectar si un pedido con el mismo ID llega con datos diferentes.
2.  **Inicio de Transacción SQL:** Se conecta a la base de datos SQL Server y se inicia una transacción con un nivel de aislamiento especial. Se utiliza `WITH (UPDLOCK, HOLDLOCK)` en la consulta.
    - **¿Por qué es importante?** Este comando bloquea la fila en la tabla `Z_SOHO_OrderMap` que corresponde al `ZohoOrderId` y `InstanceId` del pedido. Si dos solicitudes para el mismo pedido llegan exactamente al mismo tiempo, la segunda tendrá que esperar a que la primera termine su transacción. Esto **evita condiciones de carrera** y es el corazón de la garantía de no duplicidad.
3.  **Verificación de Estado:**
    - **Si el pedido ya existe en la tabla:**
        - **CONFLICT_HASH:** El pedido existe, pero el `payloadHash` nuevo es diferente al guardado. Se devuelve un error. Esto indica un problema, ya que el mismo ID de pedido no debería tener datos distintos.
        - **DUPLICATE_CREATED:** El estado es `CREATED`. Significa que el pedido ya fue creado con éxito en SAP. Se devuelve una respuesta de éxito inmediato sin volver a procesar nada.
        - **IN_PROGRESS:** El estado es `PROCESSING`. Significa que otra solicitud está trabajando en este pedido ahora mismo. Se devuelve un error para que el sistema de origen reintente más tarde.
        - **FAILED:** El estado es `FAILED`. Significa que un intento anterior falló. El sistema permite un reintento, actualizando el estado de nuevo a `PROCESSING`.
    - **Si el pedido no existe en la tabla:**
        - Se inserta una nueva fila en `Z_SOHO_OrderMap` con el estado `PROCESSING`.
        - Se devuelve el estado `Started`, indicando que se puede proceder con la creación.

### Paso 3: Pre-Validación Rápida (Archivo: `Services/OrderMapRepository.cs`, Función: `PreValidateAsync`)

Si `TryBeginAsync` da luz verde (`Started`), se realiza una validación rápida. Es mucho más eficiente hacer esto con consultas SQL directas que conectar a la DI API de SAP.

1.  **Verificar Cliente:** Comprueba si el cliente por defecto (configurado en `Soho:DefaultCardCode`) existe en la tabla `OCRD` de SAP.
2.  **Verificar Vendedor:** Comprueba si el vendedor por defecto (`Soho:DefaultSlpCode`) existe en `OSLP`.
3.  **Verificar Almacén:** Comprueba si el almacén por defecto (`Soho:DefaultWarehouseCode`) existe en `OWHS`.
4.  **Verificar Artículos:** Extrae todos los `ProductId` de las líneas del pedido y comprueba que **todos** existan en la tabla `OITM` (Maestro de Artículos).

Si alguna de estas validaciones falla, el proceso se detiene aquí. Se llama a `MarkFailedAsync` para registrar el error en la base de datos y se devuelve una respuesta de error al cliente.

### Paso 4: Creación del Pedido en SAP (Archivo: `Services/SapDiService.cs`, Función: `CreateSalesOrder`)

Si la pre-validación es exitosa, se procede a la creación real en SAP.

1.  **Conexión a la DI API:**
    - Se crea un objeto `SAPbobsCOM.Company`.
    - Se le asignan todas las propiedades de conexión leídas desde la sección `SapDi` del `appsettings.json` (servidor, base de datos, usuario, contraseña, etc.).
    - Se invoca al método `company.Connect()`. Si falla, se lanza una excepción que será capturada en el `endpoint`.
2.  **Mapeo de Datos:**
    - Se crea un objeto de Pedido de Venta (`Documents oOrders`).
    - **Cabecera:**
        - `CardCode`: Se asigna el cliente por defecto (`Soho:DefaultCardCode`).
        - `NumAtCard`: **Campo clave**. Se guarda el `ZohoOrderId` aquí para tener una referencia directa al pedido de origen dentro de SAP.
        - `SalesPersonCode`: Se asigna el vendedor por defecto.
        - `DocDate`: Se asigna la fecha del pedido.
    - **Líneas:**
        - Se recorre cada artículo (`SohoSaleItem`) del payload.
        - `ItemCode`: Se mapea desde `ProductId`.
        - `Quantity`: Se mapea desde `Quantity`.
        - `Price`: Se mapea desde `Price`. Se asume que este es el precio final.
        - `DiscountPercent`: Se mapea desde `Discount`.
        - `WarehouseCode`: Se asigna el almacén por defecto a cada línea.
3.  **Creación y Obtención de ID:**
    - Se llama al método `doc.Add()`. Si SAP devuelve un error, se lanza una excepción con el mensaje de error de SAP.
    - Si `Add()` tiene éxito, se usa `company.GetNewObjectKey()` para obtener el `DocEntry` (la clave interna numérica del nuevo pedido).
    - Inmediatamente después, se hace una lectura (`GetByKey`) de ese mismo pedido para obtener el `DocNum` (el número de pedido visible para el usuario).
4.  **Desconexión:** En un bloque `finally`, se llama siempre a `company.Disconnect()`. Esto es **fundamental** para liberar la licencia de la DI API que se usó durante la conexión.

### Paso 5: Actualización Final del Estado (Archivo: `Program.cs` / `Services/OrderMapRepository.cs`)

1.  **Si la creación en SAP fue exitosa (`CreateSalesOrder` no lanzó excepción):**
    - Se llama a `MarkCreatedAsync`, que actualiza la fila en `Z_SOHO_OrderMap` a `Status='CREATED'` y guarda el `SapDocEntry` y `SapDocNum` devueltos por SAP.
    - Se devuelve una respuesta de éxito (`200 OK`) al cliente, incluyendo los IDs de SAP.
2.  **Si la creación en SAP falló (hubo una excepción):**
    - El bloque `catch` en `Program.cs` captura la excepción.
    - Se llama a `SafeMarkFailed` (que a su vez llama a `MarkFailedAsync`) para actualizar la fila a `Status='FAILED'` y guardar el mensaje de error en la base de datos.
    - Se devuelve una respuesta de error al cliente con el mensaje de la excepción.

## 3. Estructura de la Base de Datos de Idempotencia

Se espera una tabla con una estructura similar a esta en una base de datos SQL Server.

**Tabla: `Z_SOHO_OrderMap`**
- `ZohoOrderId` (varchar, PK): ID del pedido en Soho.
- `InstanceId` (varchar, PK): ID de la instancia de envío.
- `Status` (varchar): `PROCESSING`, `CREATED`, `FAILED`.
- `PayloadHash` (varchar): El hash SHA256 del contenido del pedido.
- `SapDocEntry` (int, nullable): El DocEntry del pedido creado en SAP.
- `SapDocNum` (int, nullable): El DocNum del pedido creado en SAP.
- `ErrorMessage` (nvarchar, nullable): El mensaje de error si el estado es `FAILED`.
- `ProcessingAt` (datetime): Marca de tiempo de cuándo se empezó a procesar.
- `CreatedAt` (datetime): Marca de tiempo de cuándo se creó el registro.
- `UpdatedAt` (datetime): Marca de tiempo de la última actualización.

## 4. Endpoint de Observabilidad de Estado

Además del flujo de creación (`POST /orders`), el sistema expone un endpoint de consulta de estado:

- **GET** `/orders/{zohoOrderId}/{instanceId}/status`

### Respuestas esperadas
- **200 OK**: Cuando existe registro en `Z_SOHO_OrderMap` para la llave `zohoOrderId + instanceId`.
  - Incluye: `status`, `sap.docEntry`, `sap.docNum`, `errorMessage`, `updatedAt`.
- **404 Not Found**: Cuando no existe registro para la combinación solicitada.
- **400 Bad Request**: Cuando faltan parámetros o llegan vacíos.

Este endpoint permite trazabilidad operativa sin consultar directamente la base de datos.

## 5. Estado actual del alcance (implementado vs pendiente)

### Implementado
- Recepción y procesamiento de pedidos con idempotencia.
- Persistencia del estado en `Z_SOHO_OrderMap` (`PROCESSING`, `CREATED`, `FAILED`).
- Consulta de estado por `zohoOrderId + instanceId`.

### Pendiente
- Notificación saliente desacoplada (callback/outbox) hacia un endpoint externo de cierre de pedido.
- Hardening operativo adicional (reconciliación SAP/SQL, control de `PROCESSING` stale y validación más estricta de payload).
