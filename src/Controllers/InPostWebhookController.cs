using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;
using ShopifyOrderAutomation.Services;
using Microsoft.Extensions.Logging;

namespace ShopifyOrderAutomation.Controllers;

[ApiController]
[Route("api/inpost-webhook")]
public class InPostWebhookController : ControllerBase
{
    private readonly IInPostService _inPostService;
    private readonly IShopifyService _shopifyService;
    private readonly ILogger<InPostWebhookController> _logger;

    public InPostWebhookController(IInPostService inPostService, IShopifyService shopifyService, ILogger<InPostWebhookController> logger)
    {
        _inPostService = inPostService;
        _shopifyService = shopifyService;
        _logger = logger;
    }

    [HttpGet]
    public IActionResult HealthCheck() => Ok("ok");
    
    [HttpPost]
    public async Task<IActionResult> ReceiveWebhook()
    {
        using var reader = new StreamReader(Request.Body);
        var raw = await reader.ReadToEndAsync();
        _logger.LogInformation("InPost webhook RAW: {Raw}", raw);

        try
        {
            var json = JsonNode.Parse(raw)!.AsObject();

            if (!json.TryGetPropertyValue("event", out var evNode) ||
                !json.TryGetPropertyValue("payload", out var payloadNode))
            {
                _logger.LogWarning("Webhook bez wymaganych pól 'event' lub 'payload'");
                return BadRequest();
            }

            var ev = evNode!.GetValue<string>();
            var payload = payloadNode!.AsObject();

            var status   = payload.TryGetPropertyValue("status", out var s) ? s!.GetValue<string>() : null;
            var tracking = payload.TryGetPropertyValue("tracking_number", out var t) ? t!.GetValue<string>() : null;

            _logger.LogInformation("InPost EVENT={Event} STATUS={Status} TRACKING={Tracking}", ev, status, tracking);

            if (ev == "shipment_confirmed") // Etykieta utworzona
            {
                var shipmentId = payload.TryGetPropertyValue("shipment_id", out var sh) ? sh!.GetValue<long>() : 0;
                var orderName = await _inPostService.ResolveOrderNameAsync(shipmentId);

                if (!string.IsNullOrWhiteSpace(orderName))
                {
                    _logger.LogInformation("Marking order {OrderName} as ON HOLD.", orderName);
                    await _shopifyService.MarkOrderAsOnHold(orderName);
                }
                else
                {
                    _logger.LogWarning("Nie udało się ustalić numeru zamówienia dla shipment_id={ShipmentId}", shipmentId);
                }
            }
            else if (ev == "shipment_status_changed" && status == "adopted_at_sorting_center")
            {
                var shipmentId = payload.TryGetPropertyValue("shipment_id", out var sh) ? sh!.GetValue<long>() : 0;
                var orderName = await _inPostService.ResolveOrderNameAsync(shipmentId);

                if (!string.IsNullOrWhiteSpace(orderName))
                {
                    var (isReady, tn) = await _inPostService.IsReadyForFulfillment(tracking);
                    if (isReady)
                    {
                        _logger.LogInformation("Marking order {OrderName} as FULFILLED (tracking={Tracking}).", orderName, tn);
                        await _shopifyService.MarkOrderAsFulfilled(orderName, tn);
                    }
                }
                else
                {
                    _logger.LogWarning("Nie udało się ustalić numeru zamówienia dla shipment_id={ShipmentId}", shipmentId);
                }
            }

            return Ok();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process InPost webhook");
            return Ok(); // 200 aby uniknąć ponownych wysyłek przez InPost
        }
    }
}
