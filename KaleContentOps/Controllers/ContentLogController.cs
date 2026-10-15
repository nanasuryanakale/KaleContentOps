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
                    // Views filter matches the resolved display value: the newest snapshot that
                    // CARRIES Views (a newer details snapshot may omit them entirely).
                    || (parsedViews.HasValue && x.Metrics.Where(m => m.Views != null).OrderByDescending(m => m.CapturedAt).Select(m => (long?)m.Views).FirstOrDefault() == parsedViews.Value)
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
                // Metric SCALARS (Views, engagement, commerce/attribute) are deliberately NOT
                // projected here - only their timestamp is. The two syncs write disjoint column
                // sets: the list sync owns Views/commerce/date-range and stores engagement and
                // demographics as NULL; the details sync owns engagement/demographics and stores
                // commerce as NULL. Projecting "the newest row" therefore let whichever sync ran
                // LAST blank the fields the other one had already fetched. Each scalar is resolved
                // after materialization from the newest snapshot that carries it (one page-scoped
                // query) - which also replaces one correlated subquery per scalar per row.
                // NOTE: DemographicsJson is intentionally NOT taken from the latest metric here.
                // A newer views-only metric (DemographicsJson NULL - the ingestion representation
                // of "no demographic observation", e.g. viewer_profile=[]) must not hide an older
                // valid demographic snapshot. It is resolved after materialization from the most
                // recent meaningful snapshot below, exactly like every metric scalar.
                LatestMetricCapturedAt = x.Metrics.OrderByDescending(m => m.CapturedAt).Select(m => (DateTime?)m.CapturedAt).FirstOrDefault()
            })
            .AsNoTracking()
            .ToListAsync();

        // Resolve VIEWERS and NEW_FOLLOWER demographics per row from each population's own
        // most recent VALID, meaningful demographic snapshot (not blindly from the latest
        // metric). One page-scoped query (no N+1, consistent with the historical-pics lookup
        // below) covers both populations: only demographic-bearing metrics for the current
        // page's logs are loaded, newest first, and at most the two newest candidates per log
        // and per population are evaluated (the newest, plus one older fallback). The two
        // populations are kept strictly separate - one can never populate the other's fields.
        var pageLogIds = items.Select(i => i.Id).ToList();
        var viewersDemographicCandidates = new Dictionary<long, List<string?>>();
        var newFollowerDemographicCandidates = new Dictionary<long, List<string?>>();
        if (pageLogIds.Count > 0)
        {
            var candidateRows = await _db.ContentMetrics.AsNoTracking()
                .Where(m => pageLogIds.Contains(m.ContentLogId)
                            && ((m.DemographicsJson != null && m.DemographicsJson != "")
                                 || (m.NewFollowerDemographicsJson != null && m.NewFollowerDemographicsJson != "")))
                .OrderByDescending(m => m.CapturedAt)
                .ThenByDescending(m => m.Id)
                .Select(m => new { m.ContentLogId, m.DemographicsJson, m.NewFollowerDemographicsJson })
                .ToListAsync();

            // GroupBy preserves source order within each group (CapturedAt DESC, then Id DESC),
            // so the first meaningful candidate encountered is the newest meaningful snapshot.
            foreach (var group in candidateRows.GroupBy(m => m.ContentLogId))
            {
                viewersDemographicCandidates[group.Key] = group
                    .Select(m => m.DemographicsJson)
                    .Where(j => !string.IsNullOrWhiteSpace(j))
                    .Take(2)
                    .ToList();
                newFollowerDemographicCandidates[group.Key] = group
                    .Select(m => m.NewFollowerDemographicsJson)
                    .Where(j => !string.IsNullOrWhiteSpace(j))
                    .Take(2)
                    .ToList();
            }
        }

        // Every metric scalar is resolved field-wise from the newest snapshot that carries a value
        // for that field - the same rule this page already applies to demographics. The two
        // producers write disjoint column sets: the list sync owns Views / commerce / date-range and
        // stores engagement + demographics as NULL; the details sync owns engagement + demographics
        // and stores commerce as NULL (MapDetailsMetricsToContentMetric never sets it), and it only
        // sets Views when the response actually reports them ("if (viewsFound)"). Reading any scalar
        // from "the single newest metric" therefore let whichever sync ran LAST blank the fields the
        // other one had already fetched: a list re-sync hid engagement, and a details re-sync hid GMV
        // / ItemsSold / Products / Views. Ordering explicitly by Id as well keeps the choice
        // deterministic when two snapshots share an instant. A field no snapshot anywhere carries
        // stays NULL (never a fabricated 0), and an observed 0 is kept (0 is a value, not an absence).
        var metricSnapshots = new Dictionary<long, List<MetricSnapshot>>();
        if (pageLogIds.Count > 0)
        {
            // Only rows carrying at least one resolvable scalar are candidates - a row that carries
            // none cannot contribute. One page-scoped query (no N+1), mirroring the demographics
            // lookup directly above.
            var metricRows = await _db.ContentMetrics.AsNoTracking()
                .Where(m => pageLogIds.Contains(m.ContentLogId)
                            && (m.Views != null || m.Reach != null || m.Likes != null || m.Comments != null
                                || m.Shares != null || m.NewFollowers != null
                                || m.AverageWatch != null || m.FullWatchRate != null
                                || m.GmvAmount != null || m.GmvCurrency != null
                                || m.ItemsSold != null || m.SkuOrders != null
                                || m.AvgCustomers != null || m.ClickThroughRate != null
                                || m.HashtagsJson != null || m.ProductsJson != null))
                .OrderByDescending(m => m.CapturedAt)
                .ThenByDescending(m => m.Id)
                .Select(m => new MetricSnapshot
                {
                    ContentLogId = m.ContentLogId,
                    Views = m.Views,
                    Reach = m.Reach,
                    Likes = m.Likes,
                    Comments = m.Comments,
                    Shares = m.Shares,
                    NewFollowers = m.NewFollowers,
                    AverageWatch = m.AverageWatch,
                    FullWatchRate = m.FullWatchRate,
                    GmvAmount = m.GmvAmount,
                    GmvCurrency = m.GmvCurrency,
                    ItemsSold = m.ItemsSold,
                    SkuOrders = m.SkuOrders,
                    AvgCustomers = m.AvgCustomers,
                    ClickThroughRate = m.ClickThroughRate,
                    HashtagsJson = m.HashtagsJson,
                    ProductsJson = m.ProductsJson
                })
                .ToListAsync();

            // GroupBy preserves source order within each group (CapturedAt DESC, then Id DESC), so the
            // first snapshot that carries a field holds that field's most recent observation.
            foreach (var group in metricRows.GroupBy(m => m.ContentLogId))
            {
                metricSnapshots[group.Key] = group.ToList();
            }
        }

        // Parse the selected DemographicsJson into display fields (0..1 viewer shares).
        // Done after materialization to keep the query translatable to SQL (no client-eval in projection).
        foreach (var item in items)
        {
            // Resolve every metric scalar from the ordered snapshot list below: first non-null
            // wins, which is exactly "the newest snapshot that carries this field" (CapturedAt DESC,
            // Id DESC - deterministic even when two snapshots share an instant). The projection never
            // sets these columns, so they start null and this loop is their single source of truth -
            // including HashtagsJson/ProductsJson, which are parsed further down in this same loop.
            if (metricSnapshots.TryGetValue(item.Id, out var metricCandidates))
            {
                foreach (var snapshot in metricCandidates)
                {
                    item.Views ??= snapshot.Views;
                    item.Reach ??= snapshot.Reach;
                    item.Likes ??= snapshot.Likes;
                    item.Comments ??= snapshot.Comments;
                    item.Shares ??= snapshot.Shares;
                    item.NewFollowers ??= snapshot.NewFollowers;
                    item.AverageWatch ??= snapshot.AverageWatch;
                    item.FullWatchRate ??= snapshot.FullWatchRate;
                    item.GmvAmount ??= snapshot.GmvAmount;
                    item.GmvCurrency ??= snapshot.GmvCurrency;
                    item.ItemsSold ??= snapshot.ItemsSold;
                    item.SkuOrders ??= snapshot.SkuOrders;
                    item.AvgCustomers ??= snapshot.AvgCustomers;
                    item.ClickThroughRate ??= snapshot.ClickThroughRate;
                    item.HashtagsJson ??= snapshot.HashtagsJson;
                    item.ProductsJson ??= snapshot.ProductsJson;
                }
            }

            item.DemographicsJson = null;
            if (viewersDemographicCandidates.TryGetValue(item.Id, out var candidates))
            {
                foreach (var candidate in candidates)
                {
                    if (IsMeaningfulDemographics(candidate))
                    {
                        item.DemographicsJson = candidate;
                        break;
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(item.DemographicsJson))
            {
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(item.DemographicsJson);
                    var root = doc.RootElement;

                    item.Male = ReadDemographicShare(root, "male");
                    item.Female = ReadDemographicShare(root, "female");
                    item.NonGender = ReadDemographicShare(root, "no_gender");

                    if (root.TryGetProperty("ages", out var ages) && ages.ValueKind == System.Text.Json.JsonValueKind.Object)
                    {
                        item.Age18_24 = ReadDemographicShare(ages, "18-24");
                        item.Age25_34 = ReadDemographicShare(ages, "25-34");
                        item.Age35_44 = ReadDemographicShare(ages, "35-44");
                        item.Age45_54 = ReadDemographicShare(ages, "45-54");
                        item.Age55Plus = ReadDemographicShare(ages, "55+");
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

            // NEW_FOLLOWER demographics: same serialized shape as VIEWERS but a separate
            // TikTok viewer_profile population - parsed into its own Nf* fields only, so the
            // two populations can never cross-populate each other's display fields.
            item.NewFollowerDemographicsJson = null;
            if (newFollowerDemographicCandidates.TryGetValue(item.Id, out var nfCandidates))
            {
                foreach (var candidate in nfCandidates)
                {
                    if (IsMeaningfulDemographics(candidate))
                    {
                        item.NewFollowerDemographicsJson = candidate;
                        break;
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(item.NewFollowerDemographicsJson))
            {
                try
                {
                    using var nfDoc = System.Text.Json.JsonDocument.Parse(item.NewFollowerDemographicsJson);
                    var nfRoot = nfDoc.RootElement;

                    item.NfMale = ReadDemographicShare(nfRoot, "male");
                    item.NfFemale = ReadDemographicShare(nfRoot, "female");
                    item.NfNonGender = ReadDemographicShare(nfRoot, "no_gender");

                    if (nfRoot.TryGetProperty("ages", out var nfAges) && nfAges.ValueKind == System.Text.Json.JsonValueKind.Object)
                    {
                        item.NfAge18_24 = ReadDemographicShare(nfAges, "18-24");
                        item.NfAge25_34 = ReadDemographicShare(nfAges, "25-34");
                        item.NfAge35_44 = ReadDemographicShare(nfAges, "35-44");
                        item.NfAge45_54 = ReadDemographicShare(nfAges, "45-54");
                        item.NfAge55Plus = ReadDemographicShare(nfAges, "55+");
                    }

                    if (nfRoot.TryGetProperty("countries", out var nfCountries) && nfCountries.ValueKind == System.Text.Json.JsonValueKind.Object)
                    {
                        item.NfCountries = nfCountries.EnumerateObject()
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
                    // Corrupt/unknown NEW_FOLLOWER JSON: leave Nf* fields null (UI shows "—")
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

    /// <summary>
    /// One persisted metric snapshot, carrying every scalar column Content Log resolves field-wise
    /// (see the lookup in <see cref="Index"/>): a newer snapshot that does not carry a field must
    /// not blank a value an older snapshot observed. Columns of BOTH producers are present - list
    /// sync (Views / commerce) and details sync (engagement) - because neither writes them all.
    /// </summary>
    private sealed class MetricSnapshot
    {
        public long ContentLogId { get; set; }
        public long? Views { get; set; }
        public long? Reach { get; set; }
        public long? Likes { get; set; }
        public long? Comments { get; set; }
        public long? Shares { get; set; }
        public long? NewFollowers { get; set; }
        public decimal? AverageWatch { get; set; }
        public decimal? FullWatchRate { get; set; }
        public decimal? GmvAmount { get; set; }
        public string? GmvCurrency { get; set; }
        public long? ItemsSold { get; set; }
        public long? SkuOrders { get; set; }
        public decimal? AvgCustomers { get; set; }
        public decimal? ClickThroughRate { get; set; }
        public string? HashtagsJson { get; set; }
        public string? ProductsJson { get; set; }
    }

    // Reads a single 0..1 demographic share (numeric, or numeric-string) from a JSON object
    // of demographic keys. Returns null when the key is absent or not parseable, so unknown
    // shapes surface as blank fields instead of fabricated zeros. Used for BOTH populations
    // (VIEWERS and NEW_FOLLOWER) since they share the same serialized key contract.
    private static decimal? ReadDemographicShare(System.Text.Json.JsonElement el, string key)
    {
        if (el.ValueKind != System.Text.Json.JsonValueKind.Object || !el.TryGetProperty(key, out var prop)) return null;
        if (prop.ValueKind == System.Text.Json.JsonValueKind.Number && prop.TryGetDecimal(out var d)) return d;
        if (prop.ValueKind == System.Text.Json.JsonValueKind.String && decimal.TryParse(prop.GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var ds)) return ds;
        return null;
    }

    // A stored VIEWERS demographic snapshot is "meaningful" when it parses as a JSON object
    // carrying at least one recognized demographic share (gender / age / country), even when
    // that share is an explicit zero (zeros are valid source data per the display contract).
    // "{}", malformed JSON and unrecognized shapes are NOT meaningful, so they can never
    // shadow an older valid snapshot. Ingestion (TikTokVideoService.MapDetailsMetricsToContentMetric)
    // writes NULL - never an empty object - when viewer_profile has no VIEWERS observations,
    // so "no observation" is always NULL and is filtered out in the query above.
    // NEW_FOLLOWER demographics live in NewFollowerDemographicsJson and are never read here.
    private static bool IsMeaningfulDemographics(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return false;

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != System.Text.Json.JsonValueKind.Object) return false;

            static bool AnyNumber(System.Text.Json.JsonElement obj)
            {
                if (obj.ValueKind != System.Text.Json.JsonValueKind.Object) return false;
                foreach (var prop in obj.EnumerateObject())
                {
                    if (prop.Value.ValueKind == System.Text.Json.JsonValueKind.Number) return true;
                }
                return false;
            }

            foreach (var key in new[] { "male", "female", "no_gender" })
            {
                if (root.TryGetProperty(key, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.Number)
                {
                    return true;
                }
            }

            return (root.TryGetProperty("ages", out var ages) && AnyNumber(ages))
                || (root.TryGetProperty("countries", out var countries) && AnyNumber(countries));
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }
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
