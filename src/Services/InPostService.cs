using System.Net.Http.Headers;
using System.Text.Json;

namespace ShopifyOrderAutomation.Services;

public class InPostService : IInPostService
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _config;

    public InPostService(HttpClient httpClient, IConfiguration config)
    {
        _httpClient = httpClient;
        _config = config;
    }

public async Task<(bool isReady, string trackingNumber, string status)> GetFulfillmentStatus(string referenceNumber)
{
    string token = _config["InPost:Token"];

    var request = new HttpRequestMessage(
        HttpMethod.Get,
        $"https://api-shipx-pl.easypack24.net/v1/tracking?ref={referenceNumber}"
    );

    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
    var response = await _httpClient.SendAsync(request);

    if (!response.IsSuccessStatusCode) return (false, null, null);

    var content = await response.Content.ReadAsStringAsync();
    using var doc = JsonDocument.Parse(content);
    var root = doc.RootElement;

    string status = root.GetProperty("status").GetString();
    string trackingNumber = root.GetProperty("tracking_number").GetString();

    bool isReady = status == "adopted_at_sorting_center";
    return (isReady, trackingNumber, status);
}

}