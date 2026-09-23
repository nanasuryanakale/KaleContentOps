using System;
using KaleContentOps.Services;
using KaleContentOps.Services.Targets;
using KaleContentOps.ViewModels;
using Microsoft.AspNetCore.Mvc;

namespace KaleContentOps.Controllers;

/// <summary>
/// Phase 3 API endpoints for Menu Targets (auto-save backend).
/// Thin transport layer only: all business rules (ContentType.Code validation,
/// AUTO_GMV_LIVE rejection, same-day vs new-day versioning, EffectiveFrom/To
/// calculation, transaction, negative-value validation) live in ITargetService.
/// Routes follow the existing lowercase convention (cf. internal/tiktok).
/// </summary>
public class TargetsController : Controller
{
    private readonly ITargetService _targetService;
    private readonly IShopTimeZone _shopTimeZone;

    public TargetsController(ITargetService targetService, IShopTimeZone shopTimeZone)
    {
        _targetService = targetService;
        _shopTimeZone = shopTimeZone;
    }

    // ------------------------------------------------------------------
    // GET targets/current - active configuration for the Menu Targets UI
    // ------------------------------------------------------------------
    [HttpGet("targets/current")]
    public async Task<IActionResult> Current(CancellationToken cancellationToken)
    {
        var current = await _targetService.GetCurrentTargetsAsync(cancellationToken);
        return Ok(current);
    }

    // ------------------------------------------------------------------
    // POST targets/save - auto-save endpoint
    // Antiforgery follows the existing MVC convention (TikTokAdminController
    // uses [ValidateAntiForgeryToken] on POST actions); header-based to keep
    // the JSON body clean.
    // ------------------------------------------------------------------
    [HttpPost("targets/save")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Save([FromBody] TargetSaveDto dto, CancellationToken cancellationToken)
    {
        if (dto is null)
        {
            return BadRequest(new { error = "Payload tidak valid." });
        }

        // Server date only (Phase 2 mechanism: shop reporting timezone, Asia/Jakarta).
        // The browser never gets a say in what "today" is.
        var request = new TargetSaveRequest
        {
            ContentTypeId = dto.ContentTypeId,
            TargetUpload = dto.TargetUpload,
            TargetViews = dto.TargetViews,
            Today = _shopTimeZone.Today()
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
                CreatedNewVersion = result.CreatedNewVersion
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
