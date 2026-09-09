using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using KaleContentOps.Services.TikTok;
using KaleContentOps.Data;
using Microsoft.EntityFrameworkCore;

namespace KaleContentOps.Controllers.Admin
{
    [Route("Admin/TikTok")]
    public class TikTokAdminController : Controller
    {
        private readonly ITikTokShopService _shopService;
        private readonly ITikTokVideoService _videoService;
        private readonly AppDbContext _db;

        public TikTokAdminController(ITikTokShopService shopService, ITikTokVideoService videoService, AppDbContext db)
        {
            _shopService = shopService;
            _videoService = videoService;
            _db = db;
        }

        [HttpGet("")]
        public async Task<IActionResult> Index()
        {
            var shops = await _db.TikTokShops.OrderBy(x => x.Id).ToListAsync();
            return View(shops);
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
            int videosUpdated = 0; // we count processed as inserted or updated; service returns total processed
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
    }
}
