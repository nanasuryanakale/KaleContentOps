        [Fact]
        public async Task Debug_P1_Candidates_Analysis()
        {
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;

            using var db = new AppDbContext(options);

            // Create shop
            db.TikTokCredentials.Add(new TikTokCredential { AppKey = "K" });
            var shop = new TikTokShop { ShopCipher = "SHOP-DEBUG" };
            db.TikTokShops.Add(shop);
            await db.SaveChangesAsync();

            // Create 10 ContentLogs with metrics (like the failing test does)
            for (int i = 0; i < 10; i++) await SeedShopLogWithMetricAsync(db, shop.ShopCipher, $"VID-{i}");

            // Now analyze the state
            var shopId = shop.Id;
            var query = db.ContentLogs.AsNoTracking().Where(cl => cl.TikTokShopId == shopId && !string.IsNullOrEmpty(cl.VideoId));

            // P0: no metrics
            var p0Count = await query.Where(cl => !db.ContentMetrics.Any(m => m.ContentLogId == cl.Id)).CountAsync();
            Console.WriteLine($"P0 (no metrics): {p0Count}");

            // latestPerLogRestricted
            var latestPerLogRestricted = db.ContentMetrics
                .Where(m => db.ContentLogs.Where(cl => cl.TikTokShopId == shopId).Select(cl => cl.Id).Contains(m.ContentLogId))
                .GroupBy(m => m.ContentLogId)
                .Select(g => new { ContentLogId = g.Key, CapturedAt = g.Max(x => x.CapturedAt) });
            Console.WriteLine($"latestPerLogRestricted count: {latestPerLogRestricted.Count()}");

            // latestMetrics (P1: not enriched)
            var latestMetrics = from lm in latestPerLogRestricted
                                join m in db.ContentMetrics on new { lm.ContentLogId, lm.CapturedAt } equals new { m.ContentLogId, m.CapturedAt }
                                where (m.Likes.HasValue || m.Comments.HasValue || m.Shares.HasValue || m.NewFollowers.HasValue || m.Reach.HasValue || m.AverageWatch.HasValue || m.FullWatchRate.HasValue || m.DemographicsJson != null || m.NewFollowerDemographicsJson != null) == false
                                select new { lm.ContentLogId, m.CapturedAt };
            Console.WriteLine($"latestMetrics (P1) count: {latestMetrics.Count()}");

            // Full query
            var q = from cl in query
                    join lm in latestMetrics on cl.Id equals lm.ContentLogId
                    orderby cl.VideoPostTime descending, cl.Id descending
                    select new { Cl = cl, CapturedAt = (DateTime?)lm.CapturedAt };
            var page = await q.Skip(0).Take(10).ToListAsync();
            Console.WriteLine($"Page count: {page.Count}");
            foreach (var item in page)
            {
                Console.WriteLine($"  VideoId={item.Cl.VideoId}, HasMetric={db.ContentMetrics.Any(m => m.ContentLogId == item.Cl.Id)}");
            }

            var candidates = page.Select(x => (ContentLog)x.Cl).ToList();
            Console.WriteLine($"CandidatesSelected: {candidates.Count}");
        }
    }
}
