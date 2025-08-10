using Asp.Versioning;
using ConfluenceSyncService.Services.Clients;
using ConfluenceSyncService.Services.Maintenance;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace ConfluenceSyncService.Controllers;

[ApiVersionNeutral]
[ApiController]
[Route("maintenance")]
public class MaintenanceController : ControllerBase
{
    private readonly SignatureService _sig;
    private readonly SharePointClient _sp;
    private readonly ILogger<MaintenanceController> _logger;

    public MaintenanceController(SignatureService sig, SharePointClient sp, ILogger<MaintenanceController> logger) // ⬅ add sp
    {
        _sig = sig;
        _sp = sp;
        _logger = logger;
    }

    #region [HttpGet("health")]

    [HttpGet("health")]
    public IActionResult Health()
    {
        _logger.LogDebug("Health check hit at {Utc}", DateTimeOffset.UtcNow);
        return Ok(new { status = "ok", ts = DateTimeOffset.UtcNow });
    }
    #endregion

    #region [HttpPost("actions/ack")]

    // e.g. POST /maintenance/actions/ack?action=TaskDone&resourceId=123&exp=1736035200&sig=...
    [HttpPost("actions/ack")]
    [AllowAnonymous]
    [IgnoreAntiforgeryToken]
    public async Task<IActionResult> Ack(
    [FromQuery] string? action,
    [FromQuery] string? resourceId,
    [FromQuery] long? exp,
    [FromQuery] string? sig,
    CancellationToken ct)
    {
        _logger.LogDebug("Ack received: action={Action}, resourceId={ResourceId}, exp={Exp}", action, resourceId, exp);

        if (string.IsNullOrWhiteSpace(action) || string.IsNullOrWhiteSpace(resourceId) || exp is null || string.IsNullOrWhiteSpace(sig))
            return BadRequest(new { ok = false, error = "missing_params" });

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (exp <= now)
        {
            _logger.LogInformation("Ack rejected: expired for resourceId={ResourceId} (exp={Exp}, now={Now})", resourceId, exp, now);
            return Unauthorized(new { ok = false, error = "expired" });
        }

        bool valid;
        try { valid = _sig.Validate(action!, resourceId!, exp.Value, sig!); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Ack signature validation errored for resourceId={ResourceId}", resourceId);
            return Unauthorized(new { ok = false, error = "invalid_signature" });
        }
        if (!valid)
        {
            _logger.LogInformation("Ack rejected: signature mismatch for resourceId={ResourceId}", resourceId);
            return Unauthorized(new { ok = false, error = "invalid_signature" });
        }

        try
        {
            var updatedId = await _sp.MarkTaskCompleteAsync(resourceId!, ct);
            _logger.LogInformation("Ack success: resourceId={ResourceId}, updatedId={UpdatedId}", resourceId, updatedId);
            return Ok(new { ok = true, resourceId, updatedId, status = "Completed" });
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Ack SharePoint update failed for resourceId={ResourceId}", resourceId);
            return StatusCode(StatusCodes.Status502BadGateway, new { ok = false, error = "sharepoint_update_failed" });
        }
    }
    #endregion

    #region [HttpGet("dev/sign")]

    [HttpGet("dev/sign")]
    [AllowAnonymous]
    public IActionResult DevSign([FromQuery] string resourceId, [FromQuery] string action = "TaskDone", [FromQuery] int ttlMinutes = 10)
    {
        var exp = DateTimeOffset.UtcNow.AddMinutes(ttlMinutes).ToUnixTimeSeconds();
        var sig = _sig.Sign(action, resourceId, exp); // your existing SignatureService

        var baseUrl = $"{Request.Scheme}://{Request.Host}";
        var url = $"{baseUrl}/maintenance/actions/ack?action={Uri.EscapeDataString(action)}&resourceId={Uri.EscapeDataString(resourceId)}&exp={exp}&sig={Uri.EscapeDataString(sig)}";
        return Ok(new { url, exp });
    }
    #endregion

}
