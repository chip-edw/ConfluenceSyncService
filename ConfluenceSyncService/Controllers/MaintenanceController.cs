using Asp.Versioning;
using ConfluenceSyncService.Services.Maintenance;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ConfluenceSyncService.Controllers;

[ApiVersionNeutral]
[ApiController]
[Route("maintenance")]
public class MaintenanceController : ControllerBase
{
    private readonly SignatureService _sig;
    private readonly ILogger<MaintenanceController> _logger;

    public MaintenanceController(SignatureService sig, ILogger<MaintenanceController> logger)
    {
        _sig = sig;
        _logger = logger;
    }

    [HttpGet("health")]
    public IActionResult Health()
    {
        _logger.LogDebug("Health check hit at {Utc}", DateTimeOffset.UtcNow);
        return Ok(new { status = "ok", ts = DateTimeOffset.UtcNow });
    }

    // e.g. POST /maintenance/actions/ack?action=TaskDone&resourceId=123&exp=1736035200&sig=...
    [HttpPost("actions/ack")]
    [AllowAnonymous] // one-click link: auth is the signature
    [IgnoreAntiforgeryToken]
    public IActionResult Ack([FromQuery] string action, [FromQuery] string resourceId,
                             [FromQuery] long exp, [FromQuery] string sig)
    {
        _logger.LogDebug("Ack received: action={Action}, resourceId={ResourceId}, exp={Exp}", action, resourceId, exp);

        var valid = _sig.Validate(action, resourceId, exp, sig);
        if (!valid)
        {
            // Log WHY (best-effort) without exposing the HMAC
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (exp <= now)
                _logger.LogWarning("Ack rejected: expired signature for resourceId={ResourceId} (exp={Exp}, now={Now})", resourceId, exp, now);
            else
                _logger.LogWarning("Ack rejected: signature mismatch for resourceId={ResourceId}", resourceId);

            return Unauthorized(new { error = "invalid_or_expired" });
        }

        _logger.LogDebug("Ack accepted for resourceId={ResourceId}", resourceId);

        // NEXT STEP: mark SharePoint row complete and return its ID / status
        return Ok(new { status = "ok", action, resourceId });
    }
}
