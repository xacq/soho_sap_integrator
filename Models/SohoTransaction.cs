using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace SohoSapIntegrator.Models;

/// <summary>
/// Representa los detalles de la transaccin de venta.
/// Contiene la informacin de cabecera y las lneas del pedido.
/// </summary>
public sealed class SohoTransaction
{
    /// <summary>
    /// Fecha de la transaccin. Se espera en un formato que DateTime.TryParse pueda interpretar.
    /// </summary>
    [JsonPropertyName("date")]
    [Required, MinLength(1)]
    public string Date { get; set; } = "";

    /// <summary>
    /// Cdigo del almacn (opcional). En la lgica actual, se usa un almacn por defecto.
    /// </summary>
    [JsonPropertyName("warehouseCode")]
    public string? WarehouseCode { get; set; }

    /// <summary>
    /// ID del vendedor (opcional). En la lgica actual, se usa un vendedor por defecto.
    /// </summary>
    [JsonPropertyName("sellerId")]
    public int? SellerId { get; set; }

    [JsonPropertyName("warehouseId")]
    public int? WarehouseId { get; set; }

    /// <summary>
    /// Objeto que contiene informacin bsica del cliente.
    /// </summary>
    [JsonPropertyName("Customer")]
    [Required]
    public SohoCustomer Customer { get; set; } = new();

    /// <summary>
    /// Lista de los artculos que componen el pedido. Debe contener al menos un artculo.
    /// </summary>
    [JsonPropertyName("SaleItemList")]
    [Required, MinLength(1)]
    public List<SohoSaleItem> SaleItemList { get; set; } = new();

    // Las siguientes propiedades (ExtraExpense, Subtotal, etc.) se reciben en el payload
    // pero no se utilizan directamente en la lgica de creacin de pedidos en SAP,
    // ya que SAP recalcula los totales basado en sus propias reglas.

    [JsonPropertyName("ExtraExpense")]
    public decimal ExtraExpense { get; set; }

    [JsonPropertyName("Subtotal")]
    public decimal Subtotal { get; set; }

    [JsonPropertyName("IVA")]
    public decimal IVA { get; set; }

    [JsonPropertyName("Total")]
    public decimal Total { get; set; }
}

/// <summary>
/// Representa la informacin del cliente en el payload de Soho.
/// En la lgica actual, esta informacin no se usa directamente ya que se asigna un cliente por defecto.
/// </summary>
public sealed class SohoCustomer
{
    [JsonPropertyName("CustomerId")]
    public string? CustomerId { get; set; }

    [JsonPropertyName("Name")]
    public string? Name { get; set; }

    [JsonPropertyName("Phone")]
    public string? Phone { get; set; }
}

/// <summary>
/// Representa una lnea de artculo en el pedido de venta.
/// </summary>
public sealed class SohoSaleItem
{
    /// <summary>
    /// Cdigo del producto (ItemCode en SAP). Es obligatorio.
    /// </summary>
    [JsonPropertyName("ProductId")]
    [Required, MinLength(1)]
    public string ProductId { get; set; } = "";

    /// <summary>
    /// Cantidad del producto. Debe ser mayor que cero.
    /// </summary>
    [JsonPropertyName("Quantity")]
    [Range(0.000001, double.MaxValue)]
    public decimal Quantity { get; set; }

    /// <summary>
    /// Precio unitario del producto. La lgica actual asume que este es el precio final.
    /// </summary>
    [JsonPropertyName("Price")]
    public decimal Price { get; set; }

    /// <summary>
    /// Porcentaje de descuento para la lnea (0-100).
    /// </summary>
    [JsonPropertyName("Discount")]
    [Range(0, 100)]
    public decimal Discount { get; set; }

    /// <summary>
    /// Total de la lnea. No se usa directamente en la lgica de creacin de SAP.
    /// </summary>
    [JsonPropertyName("Total")]
    public decimal Total { get; set; }
}