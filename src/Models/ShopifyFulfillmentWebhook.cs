namespace ShopifyOrderAutomation.Models;

public class ShopifyFulfillmentWebhook
{
    public long OrderId { get; set; }
    public string OrderName { get; set; } // np. "#12345"
}