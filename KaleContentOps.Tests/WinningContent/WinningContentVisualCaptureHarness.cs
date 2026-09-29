using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using KaleContentOps.Data;
using KaleContentOps.Models;
using KaleContentOps.Security;
using KaleContentOps.Tests;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace KaleContentOps.Tests.WinningContent;

/// <summary>
/// TEMPORARY visual-verification harness (Phase E): seeds a realistic dataset through
/// the real MVC pipeline and captures the rendered /WinningContent page into
/// VisualCheck/WinningContent.html for headless-browser screenshot verification.
/// SKIPPED by default (enable with WC_VISUAL=1) so CI never depends on it.
/// </summary>
public class WinningContentVisualCaptureHarness
{
    private const string NonKk = "NON_KK";
    private const string Kk = "KK";
    private const string AutoGmv = "AUTO_GMV_LIVE";
    private const string Ai = "AI_PRODUCE";
    private const string Self = "SELF_PRODUCE";

    [Fact]
    public async Task Capture()
    {
        if (Environment.GetEnvironmentVariable("WC_VISUAL") != "1")
        {
            return; // skipped by default
        }

        var factory = new AuthTestFactory();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            foreach (var (code, name) in new[] { (Kk, "Keranjang Kuning"), (NonKk, "Non-KK"), (AutoGmv, "Auto GMV Live") })
            {
                if (!db.ContentTypes.Any(c => c.Code == code))
                {
                    db.ContentTypes.Add(new ContentType { Code = code, Name = name });
                }
            }
            foreach (var (code, name) in new[] { (Ai, "AI Produce"), (Self, "Self Produce") })
            {
                if (!db.ProductionMethods.Any(p => p.Code == code))
                {
                    db.ProductionMethods.Add(new ProductionMethod { Code = code, Name = name });
                }
            }
            db.SaveChanges();

            var typeByCode = db.ContentTypes.ToDictionary(c => c.Code, c => c.Id);
            var ai = db.ProductionMethods.Single(p => p.Code == Ai).Id;
            var self = db.ProductionMethods.Single(p => p.Code == Self).Id;

            var random = new Random(20260929);
            var likes = new Func<long>(() => (long)(random.NextDouble() * 400 + 60));
            var comments = new Func<long>(() => (long)(random.NextDouble() * 30));
            var shares = new Func<long>(() => (long)(random.NextDouble() * 20));

            var male = new Func<decimal>(() => Math.Round(0.35m + (decimal)random.NextDouble() * 0.25m, 3));
            var female = new Func<decimal>(() => Math.Round(0.9m - male(), 3));
            var age1 = new Func<decimal>(() => Math.Round(0.28m + (decimal)random.NextDouble() * 0.15m, 3));
            var age2 = new Func<decimal>(() => Math.Round(0.25m - age1() + 0.15m + (decimal)random.NextDouble() * 0.1m, 3));

            var demoJson = new Func<string>(() =>
                $"{{\"male\":{male():0.###},\"female\":{female():0.###},\"ages\":{{\"18-24\":{age1():0.###},\"25-34\":{age2():0.###}}}}}");

            void Log(string title, string code, int? methodId, DateTime postTime, long views, long? reach, bool archived = false, bool withDemo = true, bool withReach = true)
            {
                var log = new ContentLog
                {
                    VideoId = $"vc-{Guid.NewGuid():N}"[..22],
                    Title = title,
                    Username = "kaleofficial",
                    VideoUrl = $"https://www.tiktok.com/@kaleofficial/video/{random.Next(100000000, 999999999)}",
                    VideoPostTime = postTime,
                    ContentTypeId = typeByCode[code],
                    ProductionMethodId = code == AutoGmv ? null : methodId,
                    IsArchived = archived
                };
                db.ContentLogs.Add(log);
                db.ContentMetrics.Add(new ContentMetric
                {
                    ContentLog = log,
                    Views = views,
                    Reach = withReach ? reach : null,
                    Likes = likes(),
                    Comments = comments(),
                    Shares = shares(),
                    DemographicsJson = withDemo ? demoJson() : null,
                    CapturedAt = postTime.AddDays(1)
                });
            }

            // ---- Selected period: 2026-09-07 .. 2026-09-27 (mockup-like window) ----
            var start = new DateTime(2026, 9, 7, 6, 0, 0);
            var titles = new[]
            {
                "Q&A ukuran KALE gedean loh bacot", "Kenapa harus KALE dibanding brand lain",
                "GRWM ke acara kondangan pakai KALE", "5 tips foto produk pakai KALE",
                "Teaser drop baru bulan depan", "Live shopping recap minggu ini",
                "Uji coba pertama AI Produce: 100% dibikin Google Flow", "Cerita di balik koleksi Dorian Corduroy",
                "Cara rawat kain corduroy biar awet", "Flash sale hari ini - Denim fest",
                "Barclay Hoodie - warna baru", "Weston Sweater, cocok buat cuaca sekarang",
                "Cardigan favorit bulan ini, cek keranjang kuning", "Kaos ganteng meleah sin i",
                "kaos katu yang nyaman dipakai sehariari", "Short pants simpel yang bikin outfit makin kece"
            };

            // 10 NON_KK (2 AI, 8 Self), 4 KK (Self), 1 AUTO_GMV_LIVE (archived, no method).
            var plan = new (string Code, int? Method, bool Archived, long Views, long Reach)[]
            {
                (NonKk, ai,   false, 14449, 7400), (NonKk, self, false, 12701, 6500),
                (NonKk, self, false, 12428, 6300), (NonKk, self, false, 12212, 6100),
                (NonKk, self, false,  3641, 1900), (NonKk, self, false,  1978,  950),
                (NonKk, self, false,  2673, 1400), (NonKk, self, false,  4738, 2400),
                (Kk,    self, false,  6817, 3400), (Kk,    self, false,  4741, 2300),
                (Kk,    self, false,  3554, 1800), (Kk,    self, false,  3290, 1700),
                (AutoGmv, null, true, 640, 320)
            };

            for (var i = 0; i < plan.Length; i++)
            {
                var (code, method, archived, views, reach) = plan[i];
                Log(titles[i % titles.Length] + (i >= titles.Length ? $" #{i}" : ""),
                    code, method, start.AddDays(i % 20).AddHours(i % 7), views, reach, archived);
            }

            // One below-median AI row (mockup: AI Produce 1 of 1 below median).
            var aiBelow = new ContentLog
            {
                VideoId = $"vc-{Guid.NewGuid():N}"[..22],
                Title = "Uji coba pertama AI Produce: 100% dibikin Google Flow",
                Username = "kaleofficial",
                VideoPostTime = start.AddDays(2),
                ContentTypeId = typeByCode[NonKk],
                ProductionMethodId = ai
            };
            db.ContentLogs.Add(aiBelow);
            db.ContentMetrics.Add(new ContentMetric
            {
                ContentLog = aiBelow, Views = 1978, Reach = 950,
                Likes = 120, Comments = 18, Shares = 9,
                DemographicsJson = demoJson(), CapturedAt = start.AddDays(3)
            });

            // ---- Previous window [start - 30d, start): audience comparison baseline ----
            var prevTitles = new[] { "Behind the scene produksi", "Styling basic tee", "Haul koleksi lama", "Tips mix and match" };
            for (var i = 0; i < 12; i++)
            {
                var log = new ContentLog
                {
                    VideoId = $"vc-{Guid.NewGuid():N}"[..22],
                    Title = prevTitles[i % prevTitles.Length] + $" #{i}",
                    Username = "kaleofficial",
                    VideoPostTime = start.AddDays(-29 + i * 2),
                    ContentTypeId = typeByCode[i % 3 == 2 ? Kk : NonKk],
                    ProductionMethodId = i % 5 == 0 ? ai : self
                };
                db.ContentLogs.Add(log);
                db.ContentMetrics.Add(new ContentMetric
                {
                    ContentLog = log,
                    Views = random.Next(3, 12) * 100,
                    Reach = null,
                    Likes = likes(), Comments = comments(), Shares = shares(),
                    DemographicsJson = demoJson(),
                    CapturedAt = start.AddDays(-28 + i * 2)
                });
            }

            db.SaveChanges();
        }

        var username = "vc.capture";
        await factory.CreateRoleUserAsync(username, AuthConstants.Roles.Viewer);
        var client = await factory.SignInAsync(username);

        var html = await client.GetStringAsync("/WinningContent?startDate=2026-09-07&endDate=2026-09-27");

        var dir = Path.Combine(Directory.GetCurrentDirectory(), "..", "..", "..", "..", "VisualCheck");
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, "WinningContent.html"), html);
        await File.WriteAllTextAsync(Path.Combine(dir, "wc-visual-credentials.txt"),
            $"user={username}\npassword=Passw0rd!Long\n");

        Assert.True(File.Exists(Path.Combine(dir, "WinningContent.html")));
    }
}
