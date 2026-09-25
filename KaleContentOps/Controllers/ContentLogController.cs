using KaleContentOps.Data;
using KaleContentOps.Models;
using KaleContentOps.ViewModels;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace KaleContentOps.Controllers;

public class ContentLogController : Controller
{
    private readonly AppDbContext _db;

    public ContentLogController(AppDbContext db)
    {
        _db = db;
    }

    public async Task<IActionResult> Index(
        string? search,
        string? contentType,
        int page = 1,
        int pageSize = 20)
    {
        if (page < 1) page = 1;
        if (pageSize < 1) pageSize = 20;

        IQueryable<ContentLog> query = _db.ContentLogs.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(search))
        {
            search = search.Trim();

            // support date search in dd-MM-yyyy format (using range for SQL translation)
            if (DateTime.TryParseExact(search, "dd-MM-yyyy", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var parsedDate))
            {
                var start = parsedDate.Date;
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

                // build predicate that checks multiple fields (Title, ContentType code/name, ProductionMethod name, PIC name, latest metric Views)
                query = query.Where(x =>
                    (x.Title != null && x.Title.Contains(search))
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
            ContentTypes = contentTypes,
            MasterPics = masterPics,
            HistoricalPics = historicalPics,
            ProductionMethods = productionMethods
        };

        return View(vm);
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
