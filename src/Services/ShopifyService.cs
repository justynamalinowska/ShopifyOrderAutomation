using System.Net.Http.Headers;
using System.Text.Json;

namespace ShopifyOrderAutomation.Services
{
    public class ShopifyService : IShopifyService
    {
        private readonly HttpClient _http;
        private readonly ILogger<ShopifyService> _logger;

        public ShopifyService(HttpClient http, IConfiguration config, ILogger<ShopifyService> logger)
        {
            _http = http;
            _logger = logger;

            var token    = config["Shopify:Token"]    ?? throw new ArgumentNullException("Shopify:Token");
            var shopName = config["Shopify:ShopName"] ?? throw new ArgumentNullException("Shopify:ShopName");
            var shopHost = $"{shopName}.myshopify.com";

            _http.BaseAddress = new Uri($"https://{shopHost}/admin/api/2025-07/");
            _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            // 👇 KLUCZOWA ZMIANA: Shopify Admin API wymaga X-Shopify-Access-Token, nie Bearer
            // Usuń ewentualny Authorization i dodaj właściwy nagłówek:
            _http.DefaultRequestHeaders.Authorization = null;
            if (_http.DefaultRequestHeaders.Contains("X-Shopify-Access-Token"))
                _http.DefaultRequestHeaders.Remove("X-Shopify-Access-Token");
            _http.DefaultRequestHeaders.Add("X-Shopify-Access-Token", token);

            _logger.LogInformation("[ShopifyService] Base={Base} TokenLen={Len}", _http.BaseAddress, token.Length);
        }

        // ========== API używane przez kontroler ==========

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

            return await PutFulfillmentOnHold(foId.Value); // ma pre-check supported_actions (brak 422)
        }

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

            // 1) Zwolnij hold, jeśli można
            await ReleaseFoHoldIfNeededAsync(foId.Value);

            // 2) Sprawdź, czy można fulfill
            var supported = await GetSupportedActionsAsync(foId.Value);
            if (!supported.Contains("fulfill"))
            {
                _logger.LogWarning("[Fulfill] Pomijam – brak akcji 'fulfill' (supported=[{A}])", string.Join(",", supported));
                return false;
            }

            // 3) Zrealizuj
            return await MarkOrderAsFulfilled(foId.Value, trackingNumber);
        }

        // ========== Publiczne helpery ==========

        public async Task<long?> GetOrderIdByName(string orderName)
        {
            var name = orderName.StartsWith("#", StringComparison.Ordinal) ? orderName : $"#{orderName}";
            var resp = await _http.GetAsync($"orders.json?name={Uri.EscapeDataString(name)}&status=any");
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("[GetOrderIdByName] HTTP {Status}", (int)resp.StatusCode);
                return null;
            }

            var json = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var orders = doc.RootElement.GetProperty("orders");
            if (orders.GetArrayLength() == 0) return null;

            return orders[0].GetProperty("id").GetInt64();
        }

        public async Task<long?> GetFulfillmentOrderId(long orderId)
        {
            var resp = await _http.GetAsync($"orders/{orderId}/fulfillment_orders.json");
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("[GetFulfillmentOrderId] HTTP {Status}", (int)resp.StatusCode);
                return null;
            }

            var json = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var arr = doc.RootElement.GetProperty("fulfillment_orders");
            if (arr.GetArrayLength() == 0) return null;

            return arr[0].GetProperty("id").GetInt64();
        }

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

        public async Task<bool> MarkOrderAsFulfilled(long fulfillmentOrderId, string trackingNumber)
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

        // ========== Prywatne helpery ==========

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
}
