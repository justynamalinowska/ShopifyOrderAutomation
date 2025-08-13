using System.Net.Http.Headers;
using System.Text.Json;

namespace ShopifyOrderAutomation.Services;

public class InPostService : IInPostService
{
    private readonly HttpClient _httpClient;
    private readonly string _token;
    private readonly ILogger<InPostService> _logger;
    private readonly IConfiguration _config;

    // produkcyjny ShipX
    private const string BaseUrl = "https://api-shipx-pl.easypack24.net";

    public InPostService(HttpClient httpClient, IConfiguration config, ILogger<InPostService> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
        _config = config;
        
        _token = _config["InPost:Token"] ?? throw new ArgumentNullException("InPost:Token");
        
        _logger.LogInformation("[InPost] Token prefix: {Prefix}", _token.Substring(0, 20));

        // ustawiamy bazowy adres i nagłówki
        _httpClient.BaseAddress = new Uri(BaseUrl);
        _httpClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", _token);
        _httpClient.DefaultRequestHeaders.Accept.Clear();
        _httpClient.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public async Task<(bool isReady, string shipmentName)> IsReadyForFulfillment(string trackingNumber)
    {
        _logger.LogInformation("[InPost] Sprawdzanie statusu przesyłki {TrackingNumber}", trackingNumber);

        var response = await _httpClient.GetAsync($"/v1/tracking/{trackingNumber}");
        var responseBody = await response.Content.ReadAsStringAsync();

        _logger.LogInformation("[InPost] Status Code: {StatusCode}", (int)response.StatusCode);
        _logger.LogDebug("[InPost] Response Body: {Body}", responseBody);

        if (!response.IsSuccessStatusCode)
            return (false, null);

        using var doc = JsonDocument.Parse(responseBody);
        var root = doc.RootElement;

        if (!root.TryGetProperty("status", out var statusProp) ||
            !root.TryGetProperty("tracking_number", out var trackingProp))
        {
            _logger.LogWarning("[InPost] Brak wymaganych pól w odpowiedzi API");
            return (false, null);
        }

        string status = statusProp.GetString();
        string shipmentName = trackingProp.GetString();

        bool isReady = true; // TEST: zawsze gotowe
        // bool isReady = status == "adopted_at_sorting_center"; // PRODUKCJA: tylko po przyjęciu w sortowni

        return (isReady, shipmentName);
    }
    
    public async Task<string?> ResolveOrderNameAsync(long shipmentId)
    {
        var cleanToken = _token.Trim();

        var url = $"{BaseUrl}/v1/shipments/{shipmentId}";
        _logger.LogInformation("[InPost] Pobieranie szczegółów shipment_id={ShipmentId}", shipmentId);
        _logger.LogInformation("[InPost] Request URL: {Url}", url);
        _logger.LogInformation("[InPost] Token prefix: {Prefix}...", cleanToken.Substring(0, Math.Min(15, cleanToken.Length)));

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", cleanToken);
        request.Headers.Accept.Clear();
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.AcceptCharset.Add(new StringWithQualityHeaderValue("utf-8"));

        var response = await _httpClient.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        _logger.LogInformation("[InPost] GET {Url} -> {StatusCode}", url, (int)response.StatusCode);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("[InPost] Błąd pobierania shipmentu {ShipmentId}: kod={StatusCode}, body={Body}", shipmentId, (int)response.StatusCode, body);
            return null;
        }

        using var doc = JsonDocument.Parse(body);

        if (doc.RootElement.TryGetProperty("reference", out var refProp))
        {
            var orderName = refProp.GetString()?.TrimStart('#');
            _logger.LogInformation("[InPost] Odczytany reference={Reference}", orderName);
            return orderName;
        }

        _logger.LogWarning("[InPost] Shipment {ShipmentId} nie zawiera pola 'reference'", shipmentId);
        return null;
    }
}
