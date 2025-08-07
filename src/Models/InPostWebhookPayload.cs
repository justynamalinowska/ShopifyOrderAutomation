namespace ShopifyOrderAutomation.Models;
using System.Text.Json.Serialization;

public class InPostWebhookPayload
{
    [JsonPropertyName("status")]
    public string Status { get; set; }

    [JsonPropertyName("shipment_name")]
    public string ShipmentName { get; set; }

    [JsonPropertyName("tracking_number")]
    public string? TrackingNumber { get; set; }
}