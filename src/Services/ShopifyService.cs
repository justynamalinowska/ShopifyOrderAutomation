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

        // ========= Tworzenie/aktualizacja fulfillmentu =========

        public async Task<bool> MarkOrderAsFulfilled(long fulfillmentOrderId, string trackingNumber)
        {
            // pobierz orderId → potrzebne do sprawdzenia istniejących fulfillmentów
            long orderId;
            {
                var foResp = await _http.GetAsync($"fulfillment_orders/{fulfillmentOrderId}.json");
                var foBody = await foResp.Content.ReadAsStringAsync();
                if (!foResp.IsSuccessStatusCode)
                {
                    _logger.LogWarning("[Fulfill] Nie udało się pobrać FO={Id} (HTTP {Status})", fulfillmentOrderId, (int)foResp.StatusCode);
                    return false;
                }
                using var doc = JsonDocument.Parse(foBody);
                orderId = doc.RootElement.GetProperty("fulfillment_order").GetProperty("order_id").GetInt64();
            }

            // 1) jeśli już istnieje fulfillment z TYM SAMYM trackingiem → kończymy (unikamy 2. maila)
            if (await FulfillmentExistsWithTracking(orderId, trackingNumber))
            {
                _logger.LogInformation("[Fulfill] Fulfillment z tracking={Tracking} już istnieje dla orderId={OrderId} – pomijam.", trackingNumber, orderId);
                return true;
            }

            // 2) jeśli istnieje JAKIKOLWIEK fulfillment → tylko zaktualizuj tracking (i dopiero tu notyfikuj)
            var (exists, existingFulfillmentId, existingTracking) = await GetFirstFulfillmentForOrder(orderId);
            if (exists && existingFulfillmentId.HasValue)
            {
                _logger.LogInformation("[Fulfill] Wykryto istniejący fulfillment={Fid} (tracking={Tn}). Aktualizuję tracking i wysyłam 1 mail.",
                    existingFulfillmentId, existingTracking ?? "(brak)");
                return await UpdateFulfillmentTrackingAsync(existingFulfillmentId.Value, trackingNumber, notify: true);
            }

            // 3) brak fulfillmentów → utwórz nowy (one-shot) z trackingiem i powiadomieniem
            var payload = new
            {
                fulfillment = new
                {
                    tracking_number = trackingNumber,
                    tracking_company = "InPost",
                    notify_customer = true,
                    line_items_by_fulfillment_order = new[]
                    {
                        new { fulfillment_order_id = fulfillmentOrderId }
                    }
                }
            };

            var json = JsonSerializer.Serialize(payload);

            var req = new HttpRequestMessage(HttpMethod.Post, "fulfillments.json")
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
            };
            // Idempotencja po trackingNumber (eliminuje duplikaty przy retrach)
            req.Headers.TryAddWithoutValidation("Idempotency-Key", $"fulfill-{trackingNumber}");

            var resp = await _http.SendAsync(req);
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

        // === DODANE: pre-check czy istnieje fulfillment z tym trackingiem (eliminuje duplikaty/mail #2) ===
        private async Task<bool> FulfillmentExistsWithTracking(long orderId, string trackingNumber)
        {
            var resp = await _http.GetAsync($"orders/{orderId}/fulfillments.json");
            if (!resp.IsSuccessStatusCode) return false;

            var json = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("fulfillments", out var arr) || arr.ValueKind != JsonValueKind.Array)
                return false;

            foreach (var f in arr.EnumerateArray())
            {
                if (f.TryGetProperty("tracking_number", out var tn) &&
                    string.Equals(tn.GetString(), trackingNumber, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        // === DODANE: pobranie pierwszego istniejącego fulfillmentu dla orderu ===
        private async Task<(bool exists, long? fulfillmentId, string? trackingNumber)> GetFirstFulfillmentForOrder(long orderId)
        {
            var resp = await _http.GetAsync($"orders/{orderId}/fulfillments.json");
            if (!resp.IsSuccessStatusCode) return (false, null, null);

            var json = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("fulfillments", out var arr) || arr.ValueKind != JsonValueKind.Array || arr.GetArrayLength() == 0)
                return (false, null, null);

            var f = arr[0];
            long fid = f.GetProperty("id").GetInt64();
            string? tn = f.TryGetProperty("tracking_number", out var tnProp) ? tnProp.GetString() : null;

            return (true, fid, tn);
        }

        // === DODANE: aktualizacja trackingu na istniejącym fulfillmencie ===
        private async Task<bool> UpdateFulfillmentTrackingAsync(long fulfillmentId, string trackingNumber, bool notify)
        {
            var payload = new
            {
                fulfillment = new
                {
                    tracking_number = trackingNumber,
                    tracking_company = "InPost",
                    // bez tracking_urls – Shopify sam zrobi link
                    notify_customer = notify
                }
            };

            var json = JsonSerializer.Serialize(payload);
            var req = new HttpRequestMessage(HttpMethod.Post, $"fulfillments/{fulfillmentId}/update_tracking.json")
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
            };
            // idempotencja na wypadek retry
            req.Headers.TryAddWithoutValidation("Idempotency-Key", $"update-tracking-{fulfillmentId}-{trackingNumber}");

            var resp = await _http.SendAsync(req);
            var body = await resp.Content.ReadAsStringAsync();
            _logger.LogInformation("[UpdateTracking] -> {Status} {Body}", (int)resp.StatusCode, body);
            return resp.IsSuccessStatusCode;
        }
    }
}
