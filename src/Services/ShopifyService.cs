using System.Net.Http.Headers;
using System.Text.Json;

namespace ShopifyOrderAutomation.Services;

public class ShopifyService : IShopifyService
{
    private readonly HttpClient _http;
    private readonly ILogger<ShopifyService> _logger;

    public ShopifyService(HttpClient http, IConfiguration config, ILogger<ShopifyService> logger)
    {
        _http = http;
        _logger = logger;

        var token = config["Shopify:Token"] ?? throw new ArgumentNullException("Shopify:Token");
        var shopName = config["Shopify:ShopName"] ?? throw new ArgumentNullException("Shopify:ShopName");
        var shopDomain = $"{shopName}.myshopify.com";

        _http.BaseAddress = new Uri($"https://{shopDomain}/admin/api/2025-07/");
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    // ========== PUBLIC API używane przez kontroler ==========

    // 1) ON HOLD po nazwie zamówienia (z # albo bez #)
    public async Task<bool> MarkOrderAsOnHold(string orderName)
    {
        var orderId = await GetOrderIdByName(orderName);
        if (orderId is null)
        {
            _logger.LogWarning("[OnHold] Nie znaleziono order po name={Name}", orderName);
            return false;
        }

        var foId = await GetFulfillmentOrderId(orderId.Value);
        if (foId is null)
        {
            _logger.LogWarning("[OnHold] Brak FO dla orderId={OrderId}", orderId);
            return false;
        }

        return await PutFulfillmentOnHold(foId.Value); // ma pre-check supported_actions
    }

    // 2) FULFILL po nazwie zamówienia (zwalnia hold jeśli trzeba, sprawdza fulfill)
    public async Task<bool> MarkOrderAsFulfilled(string orderName, string trackingNumber)
    {
        var orderId = await GetOrderIdByName(orderName);
        if (orderId is null)
        {
            _logger.LogWarning("[Fulfill] Nie znaleziono order po name={Name}", orderName);
            return false;
        }

        var foId = await GetFulfillmentOrderId(orderId.Value);
        if (foId is null)
        {
            _logger.LogWarning("[Fulfill] Brak FO dla orderId={OrderId}", orderId);
            return false;
        }

        // 1) Najpierw spróbuj zwolnić HOLD (jeśli FO na to pozwala)
        await ReleaseFoHoldIfNeededAsync(foId.Value);

        // 2) Sprawdź, czy 'fulfill' jest dostępny
        var supported = await GetSupportedActionsAsync(foId.Value);
        if (!supported.Contains("fulfill"))
        {
            _logger.LogWarning("[Fulfill] Pomijam – brak akcji 'fulfill' (supported=[{A}])", string.Join(",", supported));
            return false;
        }

        // 3) Realizacja przez /fulfillments.json (po FO id)
        return await FulfillByFoAsync(foId.Value, trackingNumber);
    }

    // ========== Poniżej implementacje i helpery ==========

    // HOLD z pre-checkiem supported_actions (unikamy 422)
    private async Task<bool> PutFulfillmentOnHold(long fulfillmentOrderId)
    {
        var supported = await GetSupportedActionsAsync(fulfillmentOrderId);
        if (!supported.Contains("hold"))
        {
            _logger.LogWarning("[OnHold] FO={Id} nie wspiera akcji HOLD (supported=[{A}])",
                fulfillmentOrderId, string.Join(",", supported));
            return false;
        }

        var resp = await _http.PostAsync(
            $"fulfillment_orders/{fulfillmentOrderId}/hold.json",
            new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));

        var body = await resp.Content.ReadAsStringAsync();
        _logger.LogInformation("[OnHold] POST hold.json -> {Status} {Body}", (int)resp.StatusCode, body);
        return resp.IsSuccessStatusCode;
    }

    // Zwolnij hold jeśli FO ma akcję release_hold
    public async Task ReleaseFoHoldIfNeededAsync(long fulfillmentOrderId)
    {
        var supported = await GetSupportedActionsAsync(fulfillmentOrderId);
        if (!supported.Contains("release_hold"))
        {
            _logger.LogInformation("[ReleaseHold] FO={Id} nie ma akcji release_hold (supported=[{A}])",
                fulfillmentOrderId, string.Join(",", supported));
            return;
        }

        _logger.LogInformation("[ReleaseHold] Zwalniam HOLD dla FO={Id}", fulfillmentOrderId);
        var resp = await _http.PostAsync(
            $"fulfillment_orders/{fulfillmentOrderId}/release_hold.json",
            new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));

        var body = await resp.Content.ReadAsStringAsync();
        _logger.LogInformation("[ReleaseHold] POST release_hold.json -> {Status} {Body}", (int)resp.StatusCode, body);
    }

    // Właściwy call fulfillments.json po FO id
    private async Task<bool> FulfillByFoAsync(long fulfillmentOrderId, string trackingNumber)
    {
        var payload = new
        {
            fulfillment = new
            {
                tracking_number = trackingNumber,
                notify_customer = true,
                line_items_by_fulfillment_order = new[]
                {
                    new { fulfillment_order_id = fulfillmentOrderId }
                }
            }
        };

        var json = JsonSerializer.Serialize(payload);
        var resp = await _http.PostAsync("fulfillments.json",
            new StringContent(json, System.Text.Encoding.UTF8, "application/json"));

        var body = await resp.Content.ReadAsStringAsync();
        _logger.LogInformation("[Fulfill] fulfillments.json -> {Status} {Body}", (int)resp.StatusCode, body);
        return resp.IsSuccessStatusCode;
    }

    // orderId po nazwie (obsługa z #)
    public async Task<long?> GetOrderIdByName(string orderName)
    {
        var name = orderName.StartsWith("#", StringComparison.Ordinal) ? orderName : $"#{orderName}";
        var resp = await _http.GetAsync($"orders.json?name={Uri.EscapeDataString(name)}&status=any");
        if (!resp.IsSuccessStatusCode) return null;

        var json = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var orders = doc.RootElement.GetProperty("orders");
        if (orders.GetArrayLength() == 0) return null;

        return orders[0].GetProperty("id").GetInt64();
    }

    // pierwszy fulfillment_order_id dla orderId
    public async Task<long?> GetFulfillmentOrderId(long orderId)
    {
        var resp = await _http.GetAsync($"orders/{orderId}/fulfillment_orders.json");
        if (!resp.IsSuccessStatusCode) return null;

        var json = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var arr = doc.RootElement.GetProperty("fulfillment_orders");
        if (arr.GetArrayLength() == 0) return null;

        return arr[0].GetProperty("id").GetInt64();
    }

    // wspólny helper: odczyt supported_actions dla FO
    private async Task<HashSet<string>> GetSupportedActionsAsync(long fulfillmentOrderId)
    {
        var resp = await _http.GetAsync($"fulfillment_orders/{fulfillmentOrderId}.json");
        var supported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!resp.IsSuccessStatusCode) return supported;

        var json = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var fo = doc.RootElement.GetProperty("fulfillment_order");

        if (fo.TryGetProperty("supported_actions", out var sa) && sa.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in sa.EnumerateArray())
                if (a.ValueKind == JsonValueKind.String) supported.Add(a.GetString()!);
        }

        return supported;
    }
}
