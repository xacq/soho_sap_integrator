using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace SohoSapIntegrator.Models;

/// <summary>
/// Representa el contenedor principal (envelope) de un nico pedido proveniente de Soho.
/// La API espera una lista de estos objetos.
/// </summary>
public sealed class SohoEnvelope
{
    /// <summary>
    /// Identificador nico del pedido en el sistema de origen (Soho/Zoho).
    /// Es obligatorio y se usa para la idempotencia.
    /// </summary>
    [JsonPropertyName("zohoOrderId")]
    [Required, MinLength(1)]
    public string ZohoOrderId { get; set; } = "";

    /// <summary>
    /// Identificador nico de la instancia o envo especfico del pedido.
    /// Junto con ZohoOrderId, forma una clave nica para la idempotencia.
    /// </summary>
    [JsonPropertyName("instanceId")]
    [Required, MinLength(1)]
    public string InstanceId { get; set; } = "";

    /// <summary>
    /// Objeto que contiene los datos de negocio del pedido.
    /// </summary>
    [JsonPropertyName("businessObject")]
    [Required]
    public SohoBusinessObject BusinessObject { get; set; } = new();
}

/// <summary>
/// Nivel intermedio en la estructura del payload, contiene el objeto de transaccin.
/// </summary>
public sealed class SohoBusinessObject
{
    /// <summary>
    /// Objeto que contiene los detalles de la transaccin de venta.
    /// </summary>
    [JsonPropertyName("Transaction")]
    [Required]
    public SohoTransaction Transaction { get; set; } = new();
}