namespace ShopifyOrderAutomation.Services;

using System.Net.Http.Headers;
using System.Text.Json;

public class ShopifyService : IShopifyService
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _config;
    private readonly string _shopName;

    public ShopifyService(HttpClient httpClient, IConfiguration config)
    {
        _httpClient = httpClient;
        _config = config;
        _shopName = _config["Shopify:ShopName"];
    }

    public async Task MarkOrderAsOnHold(string orderName)
    {
        var orderId = await GetOrderIdByName(orderName);
        if (orderId == null) return;

        var fulfillmentOrderId = await GetFulfillmentOrderId(orderId);
        if (fulfillmentOrderId == null) return;

        var request = new HttpRequestMessage(HttpMethod.Post,
            $"https://{_shopName}.myshopify.com/admin/api/2025-07/fulfillment_orders/{fulfillmentOrderId}/hold.json")
        {
            Content = JsonContent.Create(new
            {
                reason = "other",
                reason_notes = "Wstrzymano realizację przez API"
            })
        };

        AddAuthHeaders(request);
        await _httpClient.SendAsync(request);
    }

    public async Task MarkOrderAsFulfilled(string orderName, string trackingNumber)
    {
        var orderId = await GetOrderIdByName(orderName);
        if (orderId == null) return;

        var orderDetails = await GetOrderDetails(orderId);
        if (orderDetails == null) return;

        var order = orderDetails.RootElement.GetProperty("order");

        var locationId = order.GetProperty("location_id").GetInt64();
        var lineItems = order
            .GetProperty("line_items")
            .EnumerateArray()
            .Select(item => new { id = item.GetProperty("id").GetInt64() })
            .ToArray();

        var fulfillment = new
        {
            fulfillment = new
            {
                location_id = locationId,
                tracking_number = trackingNumber,
                tracking_company = "InPost",
                notify_customer = true,
                line_items = lineItems
            }
        };

        var request = new HttpRequestMessage(HttpMethod.Post,
            $"https://{_shopName}.myshopify.com/admin/api/2025-07/orders/{orderId}/fulfillments.json")
        {
            Content = JsonContent.Create(fulfillment)
        };

        AddAuthHeaders(request);
        await _httpClient.SendAsync(request);
    }

    private async Task<string?> GetOrderIdByName(string orderName)
    {
        var encodedOrderName = Uri.EscapeDataString(orderName);

        var request = new HttpRequestMessage(HttpMethod.Get,
            $"https://{_shopName}.myshopify.com/admin/api/2025-07/orders.json?name={encodedOrderName}&status=any");

        AddAuthHeaders(request);
        var response = await _httpClient.SendAsync(request);
        if (!response.IsSuccessStatusCode) return null;

        var content = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(content);

        var order = json.RootElement.GetProperty("orders").EnumerateArray().FirstOrDefault();
        return order.TryGetProperty("id", out var id) ? id.ToString() : null;
    }

    private async Task<string?> GetFulfillmentOrderId(string orderId)
    {
        var request = new HttpRequestMessage(HttpMethod.Get,
            $"https://{_shopName}.myshopify.com/admin/api/2025-07/orders/{orderId}/fulfillment_orders.json");

        AddAuthHeaders(request);
        var response = await _httpClient.SendAsync(request);
        if (!response.IsSuccessStatusCode) return null;

        var content = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(content);

        var fulfillmentOrder = json.RootElement.GetProperty("fulfillment_orders").EnumerateArray().FirstOrDefault();
        return fulfillmentOrder.TryGetProperty("id", out var id) ? id.ToString() : null;
    }

    private async Task<JsonDocument?> GetOrderDetails(string orderId)
    {
        var request = new HttpRequestMessage(HttpMethod.Get,
            $"https://{_shopName}.myshopify.com/admin/api/2025-07/orders/{orderId}.json?fields=id,line_items,location_id");

        AddAuthHeaders(request);
        var response = await _httpClient.SendAsync(request);
        if (!response.IsSuccessStatusCode) return null;

        var content = await response.Content.ReadAsStringAsync();
        return JsonDocument.Parse(content);
    }

    private void AddAuthHeaders(HttpRequestMessage request)
    {
        request.Headers.Add("X-Shopify-Access-Token", _config["Shopify:Token"]);
    }
}
