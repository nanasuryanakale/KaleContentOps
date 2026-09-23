using System;
using KaleContentOps.Security;
using KaleContentOps.Services;
using KaleContentOps.Services.Targets;
using KaleContentOps.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace KaleContentOps.Controllers;

/// <summary>
/// Phase 3 API endpoints for Menu Targets (auto-save backend) + Phase 4b
/// (Effective Date, scheduled targets, history, ChangedBy audit).
/// Thin transport layer only: all business rules (ContentType.Code validation,
/// AUTO_GMV_LIVE rejection, same-date revise vs new-version insert, EffectiveFrom/To
/// calculation, transaction, negative-value validation) live in ITargetService.
/// Routes follow the existing lowercase convention (cf. internal/tiktok).
/// </summary>
public class TargetsController : Controller
{
    private readonly ITargetService _targetService;
    private readonly IShopTimeZone _shopTimeZone;
    private readonly ICurrentUser _currentUser;

    public TargetsController(ITargetService targetService, IShopTimeZone shopTimeZone, ICurrentUser currentUser)
    {
        _targetService = targetService;
        _shopTimeZone = shopTimeZone;
        _currentUser = currentUser;
    }

    // ------------------------------------------------------------------
    // GET /Targets - Menu Targets page view
    // ------------------------------------------------------------------
    [HttpGet]
    [Authorize(Policy = PermissionPolicyProvider.PolicyPrefix + AuthConstants.Permissions.TargetView)]
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        return View();
    }

    // ------------------------------------------------------------------
    // GET targets/current - target effective today for the Menu Targets UI
    // ------------------------------------------------------------------
    [HttpGet("targets/current")]
    [Authorize(Policy = PermissionPolicyProvider.PolicyPrefix + AuthConstants.Permissions.TargetView)]
    public async Task<IActionResult> Current(CancellationToken cancellationToken)
    {
        var current = await _targetService.GetCurrentTargetsAsync(cancellationToken);
        return Ok(current);
    }

    // ------------------------------------------------------------------
    // GET targets/scheduled - future (not yet active) target versions
    // ------------------------------------------------------------------
    [HttpGet("targets/scheduled")]
    [Authorize(Policy = PermissionPolicyProvider.PolicyPrefix + AuthConstants.Permissions.TargetView)]
    public async Task<IActionResult> Scheduled(CancellationToken cancellationToken)
    {
        var scheduled = await _targetService.GetScheduledTargetsAsync(cancellationToken);
        return Ok(scheduled);
    }

    // ------------------------------------------------------------------
    // GET targets/history - persisted version chain for one content type,
    // Effective Date descending. Requires Target.History.View (backend gate).
    // ------------------------------------------------------------------
    [HttpGet("targets/history")]
    [Authorize(Policy = PermissionPolicyProvider.PolicyPrefix + AuthConstants.Permissions.TargetHistoryView)]
    [Authorize(Policy = PermissionPolicyProvider.PolicyPrefix + AuthConstants.Permissions.TargetView)]
    public async Task<IActionResult> History(int contentTypeId, CancellationToken cancellationToken)
    {
        var history = await _targetService.GetTargetHistoryAsync(contentTypeId, maxRows: 50, cancellationToken);
        return Ok(history);
    }

    // ------------------------------------------------------------------
    // GET targets/actual - rolling 7-day actuals for Menu Targets UI
    // Query parameter: contentTypeId (required)
    // ------------------------------------------------------------------
    [HttpGet("targets/actual")]
    [Authorize(Policy = PermissionPolicyProvider.PolicyPrefix + AuthConstants.Permissions.TargetView)]
    public async Task<IActionResult> Actual(int contentTypeId, CancellationToken cancellationToken)
    {
        var endDate = _shopTimeZone.Today();
        var actual = await _targetService.GetActualAsync(contentTypeId, endDate, days: 7, cancellationToken);

        if (actual is null)
        {
            return NotFound(new { error = "Content type tidak ditemukan." });
        }

        return Ok(actual);
    }

    // ------------------------------------------------------------------
    // POST targets/save - auto-save endpoint
    // Antiforgery follows the existing MVC convention (TikTokAdminController
    // uses [ValidateAntiForgeryToken] on POST actions); header-based to keep
    // the JSON body clean.
    // ------------------------------------------------------------------
    [HttpPost("targets/save")]
    [Authorize(Policy = PermissionPolicyProvider.PolicyPrefix + AuthConstants.Permissions.TargetEdit)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Save([FromBody] TargetSaveDto dto, CancellationToken cancellationToken)
    {
        if (dto is null)
        {
            return BadRequest(new { error = "Payload tidak valid." });
        }

        // Server date only (Phase 2 mechanism: shop reporting timezone, Asia/Jakarta).
        // The browser never gets a say in what "today" is. EffectiveDate ("Berlaku Mulai")
        // is user input, but the versioning decision always compares it against server today.
        var request = new TargetSaveRequest
        {
            ContentTypeId = dto.ContentTypeId,
            TargetUpload = dto.TargetUpload,
            TargetViews = dto.TargetViews,
            Today = _shopTimeZone.Today(),
            EffectiveDate = dto.EffectiveDate,
            // Audit: stable Identity user id resolved server-side from the authenticated
            // principal (ICurrentUser) - never taken from the request body.
            ChangedByUserId = _currentUser.UserId
        };

        try
        {
            var result = await _targetService.SaveTargetAsync(request, cancellationToken);

            if (!result.Success)
            {
                return MapBusinessError(result);
            }

            var saved = result.Target!;
            return Ok(new TargetSaveResponseDto
            {
                Success = true,
                ContentTypeId = saved.ContentTypeId,
                ContentTypeCode = result.ContentTypeCode ?? string.Empty,
                TargetUpload = saved.TargetUpload,
                TargetViews = saved.TargetViews,
                EffectiveFrom = saved.EffectiveFrom,
                EffectiveTo = saved.EffectiveTo,
                CreatedNewVersion = result.CreatedNewVersion,
                IsScheduled = result.IsScheduled
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Infrastructure/store failures must surface as server errors,
            // never disguised as business validation errors.
            return Problem(
                title: "Terjadi kesalahan saat menyimpan target.",
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    /// <summary>
    /// Maps TargetService validation error codes to HTTP responses.
    /// 404 for unknown content type, 422 for business rule violations.
    /// </summary>
    private IActionResult MapBusinessError(TargetSaveResult result)
    {
        if (result.ErrorCode == TargetSaveErrorCodes.ContentTypeNotFound)
        {
            return NotFound(new { error = result.ErrorMessage, code = result.ErrorCode });
        }

        // CONTENT_TYPE_NOT_TARGETABLE, TARGET_UPLOAD_NEGATIVE, TARGET_VIEWS_NEGATIVE,
        // ACTIVE_VERSION_ANOMALY (data anomaly surfaced to the client as unprocessable).
        return UnprocessableEntity(new { error = result.ErrorMessage, code = result.ErrorCode });
    }
}
