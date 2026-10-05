using KaleContentOps.Data;
using KaleContentOps.Models;
using KaleContentOps.Services;
using KaleContentOps.ViewModels;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace KaleContentOps.Controllers;

public class ContentLogController : Controller
{
    // Date formats accepted for date filtering: the dd/MM/yyyy value rendered by the
    // Tanggal column, the legacy dd-MM-yyyy input, and the ISO value posted by
    // <input type="date">.
    private static readonly string[] DateSearchFormats =
        { "dd-MM-yyyy", "dd/MM/yyyy", "d-M-yyyy", "d/M/yyyy", "yyyy-MM-dd" };

    private readonly AppDbContext _db;
    private readonly IShopTimeZone _shopTimeZone;

    // shopTimeZone stays optional so existing single-argument construction (unit tests)
    // keeps compiling; DI supplies the configured shop timezone (Asia/Jakarta) at runtime.
    public ContentLogController(AppDbContext db, IShopTimeZone? shopTimeZone = null)
    {
        _db = db;
        _shopTimeZone = shopTimeZone ?? new ShopTimeZone(Options.Create(new ShopTimeZoneOptions()));
    }

    public async Task<IActionResult> Index(
        string? search,
        string? contentType,
        string? dateFrom = null,
        string? dateTo = null,
        int page = 1,
        int pageSize = 20)
    {
        if (page < 1) page = 1;
        if (pageSize < 1) pageSize = 20;

        dateFrom = string.IsNullOrWhiteSpace(dateFrom) ? null : dateFrom.Trim();
        dateTo = string.IsNullOrWhiteSpace(dateTo) ? null : dateTo.Trim();

        IQueryable<ContentLog> query = _db.ContentLogs.AsNoTracking();

        // Date range filter on VideoPostTime, evaluated server-side as a half-open window:
        //   startInclusive <= VideoPostTime < endExclusive.
        // Boundaries are shop-local (Asia/Jakarta, GMT+7) midnights because VideoPostTime is
        // stored as a naive shop-local timestamp, so the selected day keeps its full 24h span.
        var filterFrom = ParseFilterDate(dateFrom);
        var filterTo = ParseFilterDate(dateTo);

        if (filterFrom.HasValue)
        {
            var startInclusive = _shopTimeZone.ToDateTime(filterFrom.Value);
            query = query.Where(x => x.VideoPostTime != null && x.VideoPostTime >= startInclusive);
        }

        if (filterTo.HasValue)
        {
            var endExclusive = _shopTimeZone.ToDateTime(filterTo.Value.AddDays(1));
            query = query.Where(x => x.VideoPostTime != null && x.VideoPostTime < endExclusive);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            search = search.Trim();

            // Exact-date search on VideoPostTime (server-side, half-open range so the whole
            // selected Jakarta day stays included). Accepts the dd/MM/yyyy value shown in the
            // Tanggal column as well as the legacy dd-MM-yyyy input.
            if (DateOnly.TryParseExact(search, DateSearchFormats, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var parsedDate))
            {
                var start = _shopTimeZone.ToDateTime(parsedDate);
                var end = start.AddDays(1);
                query = query.Where(x => x.VideoPostTime != null && x.VideoPostTime >= start && x.VideoPostTime < end);
            }
            else
            {
                // attempt numeric parse for views (support commas like 12,543)
                long? parsedViews = null;
                var digitsOnly = search.Replace(",", string.Empty);
                if (long.TryParse(digitsOnly, out var v))
                {
                    parsedViews = v;
                }

                // build predicate that checks multiple table fields (Title, VideoId, Username,
                // CreatorNickname, ContentType code/name, ProductionMethod name, PIC name,
                // latest metric Views). Case-insensitive Contains, translated to SQL.
                query = query.Where(x =>
                    (x.Title != null && x.Title.Contains(search))
                    || x.VideoId.Contains(search)
                    || (x.Username != null && x.Username.Contains(search))
                    || (x.VideoUrl != null && x.VideoUrl.Contains(search))
                    || (x.CreatorNickname != null && x.CreatorNickname.Contains(search))
                    || (x.ContentType != null && (x.ContentType.Code != null && x.ContentType.Code.Contains(search) || x.ContentType.Name != null && x.ContentType.Name.Contains(search)))
                    || (x.ProductionMethod != null && x.ProductionMethod.Name != null && x.ProductionMethod.Name.Contains(search))
                    || (x.Pic != null && x.Pic.Name != null && x.Pic.Name.Contains(search))
                    || (parsedViews.HasValue && x.Metrics.OrderByDescending(m => m.CapturedAt).Select(m => (long?)m.Views).FirstOrDefault() == parsedViews.Value)
                );
            }
        }

        if (!string.IsNullOrWhiteSpace(contentType))
        {
            // filter by content type code (KK, NON_KK, AUTO_GMV_LIVE)
            query = query.Where(x => x.ContentType != null && x.ContentType.Code == contentType);
        }

        // compute total count for given filters
        var totalItems = await query.CountAsync();

        var totalPages = (int)Math.Ceiling(totalItems / (double)pageSize);
        if (page > totalPages && totalPages > 0) page = totalPages;

        // Efficient projection: only select fields required by the UI plus the latest metric per content log
        var pageQuery = query
            .OrderByDescending(x => x.VideoPostTime)
            .ThenByDescending(x => x.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize);

        var items = await pageQuery
            .Select(x => new KaleContentOps.ViewModels.ContentLogListItem
            {
                Id = x.Id,
                VideoPostTime = x.VideoPostTime,
                ContentTypeId = x.ContentTypeId,
                ContentTypeCode = x.ContentType != null ? x.ContentType.Code : null,
                ContentTypeName = x.ContentType != null ? x.ContentType.Name : null,
                ProductionMethodId = x.ProductionMethodId,
                ProductionMethodName = x.ProductionMethod != null ? x.ProductionMethod.Name : null,
                PicId = x.PicId,
                Title = x.Title,
                VideoUrl = x.VideoUrl,
                // correlated subquery for latest metric per content log -> project metric scalars
                Views = x.Metrics.OrderByDescending(m => m.CapturedAt).Select(m => (long?)m.Views).FirstOrDefault(),
                Reach = x.Metrics.OrderByDescending(m => m.CapturedAt).Select(m => (long?)m.Reach).FirstOrDefault(),
                AverageWatch = x.Metrics.OrderByDescending(m => m.CapturedAt).Select(m => (decimal?)m.AverageWatch).FirstOrDefault(),
                FullWatchRate = x.Metrics.OrderByDescending(m => m.CapturedAt).Select(m => (decimal?)m.FullWatchRate).FirstOrDefault(),
                Likes = x.Metrics.OrderByDescending(m => m.CapturedAt).Select(m => (long?)m.Likes).FirstOrDefault(),
                Comments = x.Metrics.OrderByDescending(m => m.CapturedAt).Select(m => (long?)m.Comments).FirstOrDefault(),
                Shares = x.Metrics.OrderByDescending(m => m.CapturedAt).Select(m => (long?)m.Shares).FirstOrDefault(),
                NewFollowers = x.Metrics.OrderByDescending(m => m.CapturedAt).Select(m => (long?)m.NewFollowers).FirstOrDefault(),
                // Latest-metric demographics JSON, parsed into display fields after materialization below
                DemographicsJson = x.Metrics.OrderByDescending(m => m.CapturedAt).Select(m => (string?)m.DemographicsJson).FirstOrDefault(),
                // Commerce/attribute metrics (verified from actual TikTok response)
                GmvAmount = x.Metrics.OrderByDescending(m => m.CapturedAt).Select(m => (decimal?)m.GmvAmount).FirstOrDefault(),
                GmvCurrency = x.Metrics.OrderByDescending(m => m.CapturedAt).Select(m => (string?)m.GmvCurrency).FirstOrDefault(),
                ItemsSold = x.Metrics.OrderByDescending(m => m.CapturedAt).Select(m => (long?)m.ItemsSold).FirstOrDefault(),
                SkuOrders = x.Metrics.OrderByDescending(m => m.CapturedAt).Select(m => (long?)m.SkuOrders).FirstOrDefault(),
                AvgCustomers = x.Metrics.OrderByDescending(m => m.CapturedAt).Select(m => (decimal?)m.AvgCustomers).FirstOrDefault(),
                ClickThroughRate = x.Metrics.OrderByDescending(m => m.CapturedAt).Select(m => (decimal?)m.ClickThroughRate).FirstOrDefault(),
                HashtagsJson = x.Metrics.OrderByDescending(m => m.CapturedAt).Select(m => (string?)m.HashtagsJson).FirstOrDefault(),
                ProductsJson = x.Metrics.OrderByDescending(m => m.CapturedAt).Select(m => (string?)m.ProductsJson).FirstOrDefault(),
                LatestMetricCapturedAt = x.Metrics.OrderByDescending(m => m.CapturedAt).Select(m => (DateTime?)m.CapturedAt).FirstOrDefault()
            })
            .AsNoTracking()
            .ToListAsync();

        // Parse the latest metric's DemographicsJson into display fields (0..1 viewer shares).
        // Done after materialization to keep the query translatable to SQL (no client-eval in projection).
        foreach (var item in items)
        {
            if (!string.IsNullOrWhiteSpace(item.DemographicsJson))
            {
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(item.DemographicsJson);
                    var root = doc.RootElement;

                    static decimal? ReadPerc(System.Text.Json.JsonElement el, string key)
                    {
                        if (el.ValueKind != System.Text.Json.JsonValueKind.Object || !el.TryGetProperty(key, out var prop)) return null;
                        if (prop.ValueKind == System.Text.Json.JsonValueKind.Number && prop.TryGetDecimal(out var d)) return d;
                        if (prop.ValueKind == System.Text.Json.JsonValueKind.String && decimal.TryParse(prop.GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var ds)) return ds;
                        return null;
                    }

                    item.Male = ReadPerc(root, "male");
                    item.Female = ReadPerc(root, "female");
                    item.NonGender = ReadPerc(root, "no_gender");

                    if (root.TryGetProperty("ages", out var ages) && ages.ValueKind == System.Text.Json.JsonValueKind.Object)
                    {
                        item.Age18_24 = ReadPerc(ages, "18-24");
                        item.Age25_34 = ReadPerc(ages, "25-34");
                        item.Age35_44 = ReadPerc(ages, "35-44");
                        item.Age45_54 = ReadPerc(ages, "45-54");
                        item.Age55Plus = ReadPerc(ages, "55+");
                    }

                    // Countries: JSON object { "ID": 1.000, ... } (VIEWERS country_distribution).
                    // Only present keys are surfaced; unknown shapes are skipped safely.
                    if (root.TryGetProperty("countries", out var countries) && countries.ValueKind == System.Text.Json.JsonValueKind.Object)
                    {
                        item.Countries = countries.EnumerateObject()
                            .Select(c => c.Value.ValueKind == System.Text.Json.JsonValueKind.Number && c.Value.TryGetDecimal(out var cv)
                                ? new KeyValuePair<string, decimal>(c.Name, cv)
                                : (KeyValuePair<string, decimal>?)null)
                            .Where(kv => kv.HasValue)
                            .Select(kv => kv!.Value)
                            .OrderByDescending(kv => kv.Value)
                            .ToList();
                    }
                }
                catch (System.Text.Json.JsonException)
                {
                    // Corrupt/unknown demographics JSON: leave demographic fields null (UI shows "—")
                }
            }

            // Hashtags: JSON array of strings
            if (!string.IsNullOrWhiteSpace(item.HashtagsJson))
            {
                try
                {
                    using var tagsDoc = System.Text.Json.JsonDocument.Parse(item.HashtagsJson);
                    if (tagsDoc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array)
                    {
                        item.Hashtags = tagsDoc.RootElement.EnumerateArray()
                            .Select(t => t.GetString())
                            .Where(t => !string.IsNullOrWhiteSpace(t))
                            .Select(t => t!)
                            .ToList();
                    }
                }
                catch (System.Text.Json.JsonException)
                {
                    // leave null
                }
            }

            // Products: JSON array of { id, name }
            if (!string.IsNullOrWhiteSpace(item.ProductsJson))
            {
                try
                {
                    using var prodDoc = System.Text.Json.JsonDocument.Parse(item.ProductsJson);
                    if (prodDoc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array)
                    {
                        item.Products = prodDoc.RootElement.EnumerateArray()
                            .Where(p => p.ValueKind == System.Text.Json.JsonValueKind.Object)
                            .Select(p => new ContentLogProductItem
                            {
                                Id = p.TryGetProperty("id", out var pid) && pid.ValueKind == System.Text.Json.JsonValueKind.String ? pid.GetString() : null,
                                Name = p.TryGetProperty("name", out var pname) && pname.ValueKind == System.Text.Json.JsonValueKind.String ? pname.GetString() : null
                            })
                            .ToList();
                    }
                }
                catch (System.Text.Json.JsonException)
                {
                    // leave null
                }
            }
        }

        var contentTypes = await _db.ContentTypes.Where(ct => ct.IsActive).OrderBy(ct => ct.Name).ToListAsync();
        var masterPics = await _db.MasterPics.Where(p => p.IsActive).OrderBy(p => p.Name).ToListAsync();
        // gather inactive/historical pics referenced by the current page items (so we can display "(dihapus dari master)")
        var referencedPicIds = items.Where(x => x.PicId.HasValue).Select(x => x.PicId!.Value).Distinct().ToList();
        var inactivePicIds = referencedPicIds.Except(masterPics.Select(p => p.Id)).ToList();
        var historicalPics = new List<MasterPic>();
        if (inactivePicIds.Count > 0)
        {
            historicalPics = await _db.MasterPics.Where(p => inactivePicIds.Contains(p.Id)).ToListAsync();
        }
        var productionMethods = await _db.ProductionMethods.Where(pm => pm.IsActive).OrderBy(pm => pm.Name).ToListAsync();

        var vm = new ContentLogIndexViewModel
        {
            Items = items,
            CurrentPage = page,
            PageSize = pageSize,
            TotalItems = totalItems,
            TotalPages = totalPages,
            Search = search ?? string.Empty,
            SelectedContentType = contentType ?? string.Empty,
            DateFrom = dateFrom ?? string.Empty,
            DateTo = dateTo ?? string.Empty,
            ContentTypes = contentTypes,
            MasterPics = masterPics,
            HistoricalPics = historicalPics,
            ProductionMethods = productionMethods
        };

        return View(vm);
    }

    // Parses a filter date: ISO yyyy-MM-dd (posted by <input type="date">) or the
    // dd/MM/yyyy / dd-MM-yyyy display formats. Returns null when absent or unparseable so
    // an invalid value narrows nothing instead of throwing.
    private static DateOnly? ParseFilterDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        return DateOnly.TryParseExact(
            value.Trim(),
            DateSearchFormats,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None,
            out var parsed)
            ? parsed
            : null;
    }

    [HttpPost]
    public async Task<IActionResult> SetPic(long id, int picId)
    {
        var cl = await _db.ContentLogs.FindAsync(id);
        if (cl == null) return NotFound();

        var pic = await _db.MasterPics.FindAsync(picId);
        if (pic == null) return BadRequest("PIC not found");

        cl.PicId = picId;
        cl.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();

        return Ok(new { success = true });
    }

    [HttpPost]
    public async Task<IActionResult> SetContentType(long id, int? contentTypeId)
    {
        var contentLog = await _db.ContentLogs.FindAsync(id);

        if (contentLog == null)
        {
            return NotFound();
        }

        ProductionMethod? selfPm = null;
        ProductionMethod? aiPm = null;
        ContentType? ct = null;

        if (contentTypeId.HasValue)
        {
            ct = await _db.ContentTypes.FirstOrDefaultAsync(x => x.Id == contentTypeId.Value && x.IsActive);
            if (ct == null)
            {
                return BadRequest("Content type tidak ditemukan atau tidak aktif.");
            }

            // resolve production methods once
            selfPm = await _db.ProductionMethods.FirstOrDefaultAsync(pm => pm.Code == "SELF_PRODUCE");
            aiPm = await _db.ProductionMethods.FirstOrDefaultAsync(pm => pm.Code == "AI_PRODUCE");

            // apply mapping rules
            contentLog.ContentTypeId = contentTypeId;

            if (ct.Code == "AUTO_GMV_LIVE")
            {
                // Auto GMV Live -> ProductionMethodId must be NULL
                contentLog.ProductionMethodId = null;
            }
            else
            {
                // KK and NON_KK -> set to Self Produce
                if (selfPm != null)
                {
                    contentLog.ProductionMethodId = selfPm.Id;
                }
            }
        }

        contentLog.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();

        // prepare response info for UI update
        int? retProdId = null;
        string? retProdName = null;
        bool isAuto = false;

        if (contentLog.ProductionMethodId.HasValue)
        {
            var pm = await _db.ProductionMethods.FindAsync(contentLog.ProductionMethodId.Value);
            if (pm != null)
            {
                retProdId = pm.Id;
                retProdName = pm.Name;
            }
        }

        if (ct != null && ct.Code == "AUTO_GMV_LIVE") isAuto = true;

        return Ok(new
        {
            success = true,
            contentTypeId,
            productionMethodId = retProdId,
            productionMethodName = retProdName,
            isAutoGmv = isAuto
        });
    }

    [HttpPost]
    public async Task<IActionResult> ToggleProductionMethod(long id)
    {
        var contentLog = await _db.ContentLogs.FindAsync(id);
        if (contentLog == null) return NotFound(new { success = false, message = "ContentLog not found" });

        // Do not allow toggling for Auto GMV Live (ContentTypeId = 3)
        if (contentLog.ContentTypeId == 3)
        {
            return BadRequest(new { success = false, message = "Cannot change production method for Auto GMV Live" });
        }

        // resolve production method ids from master table
        var selfPm = await _db.ProductionMethods.FirstOrDefaultAsync(pm => pm.Code == "SELF_PRODUCE");
        var aiPm = await _db.ProductionMethods.FirstOrDefaultAsync(pm => pm.Code == "AI_PRODUCE");

        if (selfPm == null || aiPm == null)
        {
            return BadRequest(new { success = false, message = "Production methods not configured" });
        }

        var selfId = selfPm.Id;
        var aiId = aiPm.Id;

        // If null, treat as Self Produce (do not persist before toggling; user action toggles to Self here)
        if (!contentLog.ProductionMethodId.HasValue)
        {
            contentLog.ProductionMethodId = selfId;
            contentLog.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();

            return Ok(new { success = true, productionMethodId = contentLog.ProductionMethodId, productionMethodName = selfPm.Name });
        }

        // Toggle
        if (contentLog.ProductionMethodId == selfId)
        {
            contentLog.ProductionMethodId = aiId;
            contentLog.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
            return Ok(new { success = true, productionMethodId = aiId, productionMethodName = aiPm.Name });
        }

        // For any other value (including aiId), switch to Self Produce
        contentLog.ProductionMethodId = selfId;
        contentLog.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        return Ok(new { success = true, productionMethodId = selfId, productionMethodName = selfPm.Name });
    }
}
