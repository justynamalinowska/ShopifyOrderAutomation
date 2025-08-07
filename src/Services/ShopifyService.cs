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
        Console.WriteLine($"[OnHold] Szukam zamówienia: {orderName}");

        var orderId = await GetOrderIdByName(orderName);
        if (orderId == null)
        {
            Console.WriteLine("[OnHold] Nie znaleziono zamówienia.");
            return;
        }

        Console.WriteLine($"[OnHold] Znaleziono orderId: {orderId}");

        var fulfillmentOrderId = await GetFulfillmentOrderId(orderId);
        if (fulfillmentOrderId == null)
        {
            Console.WriteLine("[OnHold] Nie znaleziono fulfillmentOrderId.");
            return;
        }

        Console.WriteLine($"[OnHold] Znaleziono fulfillmentOrderId: {fulfillmentOrderId}");
        
        var payload = new
        {
            fulfillment_hold = new  
            {
                reason = "other",
                reason_notes = "Paczka czeka na zeskanowanie w punkcie"
            }
        };


        var jsonString = JsonSerializer.Serialize(payload);
        var content = new StringContent(jsonString);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        var request = new HttpRequestMessage(HttpMethod.Post,
            $"https://{_shopName}.myshopify.com/admin/api/2025-07/fulfillment_orders/{fulfillmentOrderId}/hold.json")
        {
            Content = content
        };

        AddAuthHeaders(request);
        var response = await _httpClient.SendAsync(request);

        var responseBody = await response.Content.ReadAsStringAsync();
        Console.WriteLine($"[OnHold] Odpowiedź: {(int)response.StatusCode} {response.ReasonPhrase}");
        Console.WriteLine($"[OnHold] Body: {responseBody}");
    }

    public async Task MarkOrderAsFulfilled(string orderName, string trackingNumber)
    {
        Console.WriteLine($"[Fulfill] Szukam zamówienia: {orderName}");

        var orderId = await GetOrderIdByName(orderName);
        if (orderId == null)
        {
            Console.WriteLine("[Fulfill] Nie znaleziono zamówienia.");
            return;
        }

        Console.WriteLine($"[Fulfill] Znaleziono orderId: {orderId}");

        var orderDetails = await GetOrderDetails(orderId);
        if (orderDetails == null)
        {
            Console.WriteLine("[Fulfill] Nie udało się pobrać szczegółów zamówienia.");
            return;
        }

        var order = orderDetails.RootElement.GetProperty("order");

        const long locationId = 102918979916; //long.Parse(_config["Shopify:DefaultLocationId"]);
        Console.WriteLine($"[Fulfill] locationId: {locationId}");

        var lineItems = order
            .GetProperty("line_items")
            .EnumerateArray()
            .Select(item => new { id = item.GetProperty("id").GetInt64() })
            .ToArray();

        Console.WriteLine($"[Fulfill] Liczba pozycji w zamówieniu: {lineItems.Length}");

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
        var response = await _httpClient.SendAsync(request);

        Console.WriteLine($"[Fulfill] Odpowiedź z fulfill: {(int)response.StatusCode} {response.ReasonPhrase}");
    }

    private async Task<string?> GetOrderIdByName(string orderName)
    {
        var encodedOrderName = Uri.EscapeDataString(orderName);

        Console.WriteLine($"[GetOrderId] Szukam orderName={encodedOrderName}");

        var request = new HttpRequestMessage(HttpMethod.Get,
            $"https://{_shopName}.myshopify.com/admin/api/2025-07/orders.json?name={encodedOrderName}&status=any");

        AddAuthHeaders(request);
        var response = await _httpClient.SendAsync(request);

        if (!response.IsSuccessStatusCode)
        {
            Console.WriteLine($"[GetOrderId] Błąd API: {(int)response.StatusCode} {response.ReasonPhrase}");
            return null;
        }

        var content = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(content);

        var order = json.RootElement.GetProperty("orders").EnumerateArray().FirstOrDefault();
        var found = order.TryGetProperty("id", out var id) ? id.ToString() : null;

        Console.WriteLine($"[GetOrderId] orderId={found}");
        return found;
    }

    private async Task<string?> GetFulfillmentOrderId(string orderId)
    {
        Console.WriteLine($"[GetFulfillmentId] Szukam fulfillmentOrder dla orderId={orderId}");

        var request = new HttpRequestMessage(HttpMethod.Get,
            $"https://{_shopName}.myshopify.com/admin/api/2025-07/orders/{orderId}/fulfillment_orders.json");

        AddAuthHeaders(request);
        var response = await _httpClient.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            Console.WriteLine($"[GetFulfillmentId] Błąd API: {(int)response.StatusCode} {response.ReasonPhrase}");
            return null;
        }

        var content = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(content);

        var ordersArray = json.RootElement.GetProperty("fulfillment_orders").EnumerateArray();

        var fulfillmentOrder = ordersArray.FirstOrDefault();

        if (fulfillmentOrder.ValueKind == JsonValueKind.Undefined || fulfillmentOrder.ValueKind == JsonValueKind.Null)
        {
            Console.WriteLine("[GetFulfillmentId] fulfillment_orders[] puste – brak realizacji.");
            return null;
        }

        if (!fulfillmentOrder.TryGetProperty("id", out var id))
        {
            Console.WriteLine("[GetFulfillmentId] Brak pola 'id' w fulfillment_order.");
            return null;
        }

        var found = id.ToString();
        Console.WriteLine($"[GetFulfillmentId] fulfillmentOrderId={found}");
        return found;
    }

    private async Task<JsonDocument?> GetOrderDetails(string orderId)
    {
        Console.WriteLine($"[GetOrderDetails] Pobieram szczegóły zamówienia {orderId}");

        var request = new HttpRequestMessage(HttpMethod.Get,
            $"https://{_shopName}.myshopify.com/admin/api/2025-07/orders/{orderId}.json?fields=id,line_items,location_id");

        AddAuthHeaders(request);
        var response = await _httpClient.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            Console.WriteLine($"[GetOrderDetails] Błąd API: {(int)response.StatusCode} {response.ReasonPhrase}");
            return null;
        }

        var content = await response.Content.ReadAsStringAsync();
        Console.WriteLine($"[GetOrderDetails] Sukces.");
        return JsonDocument.Parse(content);
    }

    private void AddAuthHeaders(HttpRequestMessage request)
    {
        request.Headers.Add("X-Shopify-Access-Token", _config["Shopify:Token"]);
    }
}
