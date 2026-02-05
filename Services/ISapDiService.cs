using SohoSapIntegrator.Models;

namespace SohoSapIntegrator.Services
{
    /// <summary>
    /// Define el contrato para el servicio que interacta con la DI API de SAP.
    /// Al usar una interfaz, se facilita la prueba y la inversin de dependencias (SOLID).
    /// </summary>
    public interface ISapDiService
    {
        /// <summary>
        /// Crea un Pedido de Venta en SAP basado en el payload de Soho.
        /// </summary>
        /// <param name="env">El objeto que contiene todos los datos del pedido de Soho.</param>
        /// <returns>Una tupla con el DocEntry (clave interna) y DocNum (nmero visible) del pedido creado en SAP.</returns>
        (int DocEntry, int DocNum) CreateSalesOrder(SohoEnvelope env);
    }
}