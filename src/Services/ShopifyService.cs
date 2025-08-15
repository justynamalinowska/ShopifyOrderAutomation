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

            // KLUCZ: Admin API używa nagłówka X-Shopify-Access-Token
            _http.DefaultRequestHeaders.Authorization = null;
            if (_http.DefaultRequestHeaders.Contains("X-Shopify-Access-Token"))
                _http.DefaultRequestHeaders.Remove("X-Shopify-Access-Token");
            _http.DefaultRequestHeaders.Add("X-Shopify-Access-Token", token);

            _logger.LogInformation("[ShopifyService] Base={Base} TokenLen={Len}", _http.BaseAddress, token.Length);
        }

        // ========= API wołane z kontrolera =========

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

            return await PutFulfillmentOnHold(foId.Value);
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

            // 1) zwolnij hold jeśli się da (próbujemy nawet gdy lista akcji pusta)
            await ReleaseFoHoldIfNeededAsync(foId.Value);

            // 2) sprawdź listę akcji
            var supported = await GetSupportedActionsAsync(foId.Value);

            if (supported.Contains("create_fulfillment") || supported.Contains("fulfill"))
            {
                return await MarkOrderAsFulfilled(foId.Value, trackingNumber);
            }

            // 3) Fallback: jeśli Shopify nie zwrócił akcji (pusta lista) – spróbuj mimo to
            if (supported.Count == 0)
            {
                _logger.LogWarning("[Fulfill] supported_actions puste dla FO={Id} – próbuję mimo to (fallback).", foId);
                var ok = await MarkOrderAsFulfilled(foId.Value, trackingNumber);
                if (!ok)
                    _logger.LogWarning("[Fulfill] Fallback nie powiódł się dla FO={Id}", foId);
                return ok;
            }

            _logger.LogWarning("[Fulfill] Pomijam – brak akcji create_fulfillment/fulfill (supported=[{A}])",
                string.Join(",", supported));
            return false;
        }

        // ========= Publiczne helpery (używane też przez kontroler) =========

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
            var body = await resp.Content.ReadAsStringAsync();

            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("[GetFulfillmentOrderId] HTTP {Status} Body={Body}", (int)resp.StatusCode, body);
                return null;
            }

            using var doc = JsonDocument.Parse(body);
            var arr = doc.RootElement.GetProperty("fulfillment_orders");
            if (arr.GetArrayLength() == 0) return null;

            return arr[0].GetProperty("id").GetInt64();
        }

        public async Task ReleaseFoHoldIfNeededAsync(long fulfillmentOrderId)
        {
            var supported = await GetSupportedActionsAsync(fulfillmentOrderId);

            // próbujemy nawet gdy pusto
            if (supported.Count == 0 || supported.Contains("release_hold"))
            {
                _logger.LogInformation("[ReleaseHold] Próba zwolnienia HOLD dla FO={Id} (supported=[{A}])",
                    fulfillmentOrderId, string.Join(",", supported));

                var resp = await _http.PostAsync(
                    $"fulfillment_orders/{fulfillmentOrderId}/release_hold.json",
                    new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));

                var body = await resp.Content.ReadAsStringAsync();
                _logger.LogInformation("[ReleaseHold] POST release_hold.json -> {Status} {Body}", (int)resp.StatusCode, body);
            }
            else
            {
                _logger.LogInformation("[ReleaseHold] FO={Id} nie raportuje release_hold (supported=[{A}]) – pomijam",
                    fulfillmentOrderId, string.Join(",", supported));
            }
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

        // ========= Prywatne helpery =========

        private async Task<bool> PutFulfillmentOnHold(long fulfillmentOrderId)
        {
            var supported = await GetSupportedActionsAsync(fulfillmentOrderId);

            // jeśli Shopify wyraźnie zezwala – spoko
            if (supported.Contains("hold"))
            {
                return await DoHoldPostAsync(fulfillmentOrderId);
            }

            // jeśli lista akcji jest pusta – SPRÓBUJ mimo to (tak działało wcześniej)
            if (supported.Count == 0)
            {
                _logger.LogWarning("[OnHold] supported_actions puste dla FO={Id} – próbuję HOLD mimo to (fallback).", fulfillmentOrderId);
                return await DoHoldPostAsync(fulfillmentOrderId);
            }

            _logger.LogWarning("[OnHold] FO={Id} nie wspiera akcji HOLD (supported=[{A}])",
                fulfillmentOrderId, string.Join(",", supported));
            return false;
        }

        private async Task<bool> DoHoldPostAsync(long fulfillmentOrderId)
        {
            // Zalecany payload (wcześniej bywało, że {} „przechodziło”, ale nie zawsze)
            var payload = new
            {
                fulfillment_hold = new
                {
                    reason = "other",
                    reason_notes = "Auto hold via InPost (shipment_confirmed)"
                }
            };

            var json = JsonSerializer.Serialize(payload);
            var resp = await _http.PostAsync(
                $"fulfillment_orders/{fulfillmentOrderId}/hold.json",
                new StringContent(json, System.Text.Encoding.UTF8, "application/json"));

            var body = await resp.Content.ReadAsStringAsync();
            _logger.LogInformation("[OnHold] POST hold.json -> {Status} {Body}", (int)resp.StatusCode, body);
            return resp.IsSuccessStatusCode;
        }

        private async Task<HashSet<string>> GetSupportedActionsAsync(long fulfillmentOrderId)
        {
            var resp = await _http.GetAsync($"fulfillment_orders/{fulfillmentOrderId}.json");
            var json = await resp.Content.ReadAsStringAsync();

            _logger.LogInformation("[FO] GET fulfillment_orders/{Id}.json -> {Status} Body={Body}",
                fulfillmentOrderId, (int)resp.StatusCode, json);

            var supported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!resp.IsSuccessStatusCode) return supported;

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // standard: { "fulfillment_order": { ..., "supported_actions": [ ... ] } }
            if (root.TryGetProperty("fulfillment_order", out var fo)
                && fo.TryGetProperty("supported_actions", out var sa)
                && sa.ValueKind == JsonValueKind.Array)
            {
                foreach (var a in sa.EnumerateArray())
                    if (a.ValueKind == JsonValueKind.String) supported.Add(a.GetString()!);
            }

            return supported;
        }
    }
}