using Microsoft.AspNetCore.Mvc;
using ShopifyOrderAutomation.Models;
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

    [HttpPost]
    public async Task<IActionResult> ReceiveWebhook([FromBody] InPostWebhookPayload payload)
    {
        _logger.LogInformation("Webhook received: {@Status} {@ShipmentName}", payload.Status, payload.ShipmentName);
        
        if (payload == null || string.IsNullOrEmpty(payload.ShipmentName))
            return BadRequest("Invalid payload");

        switch (payload.Status)
        {
            case "confirmed":
                _logger.LogWarning("Status 'created' received. Marking order {ShipmentName} as on hold.", payload.ShipmentName);
                await _shopifyService.MarkOrderAsOnHold(payload.ShipmentName);
                break;
            case "adopted_at_sorting_center":
            {
                _logger.LogInformation("Status 'adopted_at_sorting_center' received. Checking fulfillment readiness...");
                var (isReady, trackingNumber) = await _inPostService.IsReadyForFulfillment(payload.TrackingNumber);
                _logger.LogInformation("IsReady={IsReady}, TrackingNumber={TrackingNumber}", isReady, trackingNumber);
                
                if (isReady)
                {
                    _logger.LogInformation("Marking order {ShipmentName} as fulfilled.", payload.ShipmentName);
                    await _shopifyService.MarkOrderAsFulfilled(payload.ShipmentName, trackingNumber);
                }

                break;
            }
        }

        return Ok();
    }
}
