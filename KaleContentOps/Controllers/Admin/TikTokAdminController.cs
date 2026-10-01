using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using KaleContentOps.Security;
using KaleContentOps.Services.TikTok;
using KaleContentOps.Data;
using Microsoft.EntityFrameworkCore;

namespace KaleContentOps.Controllers.Admin
{
    [Route("Admin/TikTok")]
    [Authorize(Policy = PermissionPolicyProvider.PolicyPrefix + AuthConstants.Permissions.TikTokAdminView)]
    public class TikTokAdminController : Controller
    {
    private readonly ITikTokShopService _shopService;
    private readonly ITikTokVideoService _videoService;
    private readonly TikTokDetailsSyncService _detailsService;
    private readonly AppDbContext _db;
    private readonly Microsoft.AspNetCore.Hosting.IWebHostEnvironment _env;

    public TikTokAdminController(ITikTokShopService shopService, ITikTokVideoService videoService, TikTokDetailsSyncService detailsService, AppDbContext db, Microsoft.AspNetCore.Hosting.IWebHostEnvironment env)
    {
        _shopService = shopService;
        _videoService = videoService;
        _detailsService = detailsService;
        _db = db;
        _env = env;
    }

        [HttpGet("")]
        public async Task<IActionResult> Index()
        {
            var shops = await _db.TikTokShops.OrderBy(x => x.Id).ToListAsync();
            return View("~/Views/Admin/TikTok/Index.cshtml", shops);
        }

        [HttpPost("SyncAuthorizedShops")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SyncAuthorizedShops(CancellationToken cancellationToken)
        {
            try
            {
                var shops = await _shopService.FetchAndSaveAuthorizedShopsAsync(cancellationToken);
                var count = shops?.Count ?? 0;
                TempData["TikTokSyncMessage"] = $"Authorized shops synchronized: {count}";
                return RedirectToAction("Index");
            }

        // Historical P1 enrichment: process videos whose latest metric exists but is not enriched.
        [HttpPost("SyncVideoDetailsP1")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SyncVideoDetailsP1(int limit = 10, CancellationToken cancellationToken = default)
        {
            if (limit < 1) limit = 1;
            if (limit > 100) limit = 100; // safety hard bound

            var shops = await _db.TikTokShops.ToListAsync(cancellationToken);
            if (shops == null || shops.Count == 0)
            {
                TempData["TikTokSyncError"] = "No authorized TikTok shops found. Sync authorized shops first.";
                return RedirectToAction("Index");
            }

            int shopsProcessed = 0;
            int videosEnriched = 0;
            int errors = 0;
            var perShopErrors = new List<string>();

            foreach (var shop in shops)
            {
                if (string.IsNullOrWhiteSpace(shop.ShopCipher))
                {
                    perShopErrors.Add($"Shop {shop.Id} has empty ShopCipher");
                    errors++;
                    continue;
                }

                try
                {
                    var processed = await _detailsService.RunP1DetailsSyncAsync(shop.ShopCipher, limit: limit, skip: 0, cancellationToken: cancellationToken);
                    shopsProcessed++;
                    videosEnriched += processed;
                }
                catch (Exception ex)
                {
                    errors++;
                    perShopErrors.Add($"Shop {shop.Id} error: {ex.Message}");
                }
            }

            TempData["TikTokSyncResult"] = $"TikTok P1 Details Sync completed. Shops processed: {shopsProcessed}. Videos enriched: {videosEnriched}. Errors: {errors}";
            if (perShopErrors.Count > 0) TempData["TikTokSyncPerShopErrors"] = string.Join("\n", perShopErrors);

            return RedirectToAction("Index");
        }
            catch (Exception ex)
            {
                TempData["TikTokSyncError"] = ex.Message;
                return RedirectToAction("Index");
            }
        }

        [HttpPost("SyncVideos")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SyncVideos(CancellationToken cancellationToken)
        {
            var shops = await _db.TikTokShops.ToListAsync(cancellationToken);
            if (shops == null || shops.Count == 0)
            {
                TempData["TikTokSyncError"] = "No authorized TikTok shops found. Sync authorized shops first.";
                return RedirectToAction("Index");
            }

            int shopsProcessed = 0;
            int videosInserted = 0;
            int errors = 0;
            var perShopErrors = new List<string>();

            foreach (var shop in shops)
            {
                if (string.IsNullOrWhiteSpace(shop.ShopCipher))
                {
                    perShopErrors.Add($"Shop {shop.Id} has empty ShopCipher");
                    errors++;
                    continue;
                }

                try
                {
                    var processed = await _videoService.FetchAndSaveVideoListAsync(shop.ShopCipher, cancellationToken: cancellationToken);
                    shopsProcessed++;
                    videosInserted += processed; // service returns total processed (insert+update)
                }
                catch (Exception ex)
                {
                    errors++;
                    perShopErrors.Add($"Shop {shop.Id} error: {ex.Message}");
                }
            }

            TempData["TikTokSyncResult"] = $"TikTok Video Sync completed. Shops processed: {shopsProcessed}. Videos processed: {videosInserted}. Errors: {errors}";
            if (perShopErrors.Count > 0) TempData["TikTokSyncPerShopErrors"] = string.Join("\n", perShopErrors);

            return RedirectToAction("Index");
        }

        // Manual trigger for per-video Details enrichment. Bounded per shop to stay rate-limit safe:
        // each call goes through the shared TikTokGetWithRetryAsync (Retry-After, exponential backoff
        // with jitter, business code 36009002) and the DetailsSemaphore concurrency limiter.
        [HttpPost("SyncVideoDetails")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SyncVideoDetails(int limit = 20, CancellationToken cancellationToken = default)
        {
            if (limit < 1) limit = 1;
            if (limit > 100) limit = 100; // hard upper bound to prevent accidental bulk runs

            var shops = await _db.TikTokShops.ToListAsync(cancellationToken);
            if (shops == null || shops.Count == 0)
            {
                TempData["TikTokSyncError"] = "No authorized TikTok shops found. Sync authorized shops first.";
                return RedirectToAction("Index");
            }

            var detailsService = _detailsService;

            int shopsProcessed = 0;
            int videosEnriched = 0;
            int errors = 0;
            var perShopErrors = new List<string>();

            foreach (var shop in shops)
            {
                if (string.IsNullOrWhiteSpace(shop.ShopCipher))
                {
                    perShopErrors.Add($"Shop {shop.Id} has empty ShopCipher");
                    errors++;
                    continue;
                }

                try
                {
                    var processed = await detailsService.RunDetailsSyncAsync(shop.ShopCipher, limit: limit, cancellationToken: cancellationToken);
                    shopsProcessed++;
                    videosEnriched += processed;
                }
                catch (Exception ex)
                {
                    errors++;
                    perShopErrors.Add($"Shop {shop.Id} error: {ex.Message}");
                }
            }

            TempData["TikTokSyncResult"] = $"TikTok Details Sync completed. Shops processed: {shopsProcessed}. Videos enriched: {videosEnriched}. Errors: {errors}";
            if (perShopErrors.Count > 0) TempData["TikTokSyncPerShopErrors"] = string.Join("\n", perShopErrors);

            return RedirectToAction("Index");
        }

        // Development-only diagnostic: run the EXISTING details sync pipeline for exactly ONE
        // ContentLog selected by VideoId. Disabled outside Development; no shop credentials are
        // accepted from the request (ShopCipher is resolved from the database).
        [HttpPost("SyncVideoDetailsSingle")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SyncVideoDetailsSingle(long? contentLogId, string? videoId, CancellationToken cancellationToken = default)
        {
            if (!_env.IsDevelopment())
            {
                return NotFound(); // hard-disabled outside Development
            }

            // Accept either ContentLogId (preferred) or videoId for compatibility with existing UI.
            if (!contentLogId.HasValue && string.IsNullOrWhiteSpace(videoId))
            {
                TempData["TikTokSyncError"] = "contentLogId or videoId is required.";
                return RedirectToAction("Index");
            }

            bool synced = false;
            if (contentLogId.HasValue)
            {
                synced = await _detailsService.SyncSingleContentLogAsync(contentLogId.Value, cancellationToken);
            }
            else if (!string.IsNullOrWhiteSpace(videoId))
            {
                synced = await _detailsService.RunSingleVideoSyncAsync(videoId.Trim(), cancellationToken);
            }

            TempData["TikTokSyncResult"] = synced
                ? "Single-video details sync completed."
                : "Single-video details sync produced no persisted metric (see logs).";
            return RedirectToAction("Index");
        }
    }
}
