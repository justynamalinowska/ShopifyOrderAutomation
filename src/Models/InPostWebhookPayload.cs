namespace ShopifyOrderAutomation.Models;

public class InPostWebhookPayload
{
    public string Status { get; set; } 
    public string ShipmentName { get; set; } 
    public string TrackingNumber { get; set; }
}