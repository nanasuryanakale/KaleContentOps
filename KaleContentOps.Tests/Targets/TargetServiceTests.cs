using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using KaleContentOps.Data;
using KaleContentOps.Models;
using KaleContentOps.Services;
using KaleContentOps.Services.Targets;
using Xunit;

namespace KaleContentOps.Tests.Targets;

/// <summary>
/// Phase 2 tests for TargetService: versioning, ContentType.Code validation,
/// historical lookup, and atomicity of the new-day close+create flow.
/// Uses the EF InMemory provider, consistent with existing tests in this project.
/// </summary>
public class TargetServiceTests
{
    private const string NonKk = TargetService.NonKkCode; // "NON_KK"
    private const string Kk = TargetService.KkCode;       // "KK"
    private const string AutoGmv = "AUTO_GMV_LIVE";

    private static readonly DateOnly Today = new(2026, 9, 22);

    private class TestHost
    {
        public string DatabaseName { get; } = Guid.NewGuid().ToString();
        public AppDbContext Db { get; }
        public TargetService Service { get; }
        public StubShopTimeZone Clock { get; }
        public int NonKkId { get; }
        public int KkId { get; }
        public int AutoGmvId { get; }

        public TestHost()
        {
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase(DatabaseName)
                .Options;
            Db = new AppDbContext(options);

            Db.ContentTypes.AddRange(
                new ContentType { Code = NonKk, Name = "Non-KK" },
                new ContentType { Code = Kk, Name = "Keranjang Kuning" },
                new ContentType { Code = AutoGmv, Name = "Auto GMV Live" });
            Db.SaveChanges();

            NonKkId = Db.ContentTypes.Single(c => c.Code == NonKk).Id;
            KkId = Db.ContentTypes.Single(c => c.Code == Kk).Id;
            AutoGmvId = Db.ContentTypes.Single(c => c.Code == AutoGmv).Id;

            Clock = new StubShopTimeZone(Today);
            Service = new TargetService(Db, Clock);
        }

        public AppDbContext NewContext() => new(
            new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase(DatabaseName)
                .Options);

        public TargetSaveRequest Request(int contentTypeId, int upload, long views, DateOnly? today = null, DateOnly? effectiveDate = null, string? changedBy = null) =>
            new()
            {
                ContentTypeId = contentTypeId,
                TargetUpload = upload,
                TargetViews = views,
                Today = today ?? Today,
                EffectiveDate = effectiveDate,
                ChangedByUserId = changedBy
            };
    }

    /// <summary>Injectable server clock so scheduled-version activation can be simulated.</summary>
    private sealed class StubShopTimeZone : IShopTimeZone
    {
        public StubShopTimeZone(DateOnly today) => TodayValue = today;

        public DateOnly TodayValue { get; set; }

        public string TimeZoneId => "Asia/Jakarta";
        public DateTimeOffset ToShopLocal(DateTimeOffset utcInstant) => utcInstant;
        public DateOnly GetShopLocalDate(DateTimeOffset utcInstant) => DateOnly.FromDateTime(utcInstant.DateTime);
        public DateOnly Today() => TodayValue;
        public DateTime ToDateTime(DateOnly shopLocalDate) => shopLocalDate.ToDateTime(TimeOnly.MinValue);
        public DateTime TodayMidnight() => ToDateTime(TodayValue);
    }

    // ============================================================
    // Test 1: first target creation
    // ============================================================

    [Fact]
    public async Task SaveTarget_NoActiveVersion_CreatesFirstVersion()
    {
        var host = new TestHost();

        var result = await host.Service.SaveTargetAsync(host.Request(host.KkId, upload: 20, views: 100_000));

        Assert.True(result.Success);
        Assert.True(result.CreatedNewVersion);
        Assert.NotNull(result.Target);
        Assert.Equal(1, host.Db.Targets.Count());
        var saved = host.Db.Targets.Single();
        Assert.Equal(host.KkId, saved.ContentTypeId);
        Assert.Equal(20, saved.TargetUpload);
        Assert.Equal(100_000L, saved.TargetViews);
        Assert.Equal(Today, saved.EffectiveFrom);
        Assert.Null(saved.EffectiveTo);
    }

    // ============================================================
    // Test 2: same-day update keeps one row and updates values
    // ============================================================

    [Fact]
    public async Task SaveTarget_SameDayEdit_UpdatesExistingRow_NoNewVersion()
    {
        var host = new TestHost();
        await host.Service.SaveTargetAsync(host.Request(host.KkId, upload: 20, views: 100_000));

        var result = await host.Service.SaveTargetAsync(host.Request(host.KkId, upload: 25, views: 120_000));

        Assert.True(result.Success);
        Assert.False(result.CreatedNewVersion);
        Assert.Equal(1, host.Db.Targets.Count());
        var saved = host.Db.Targets.Single();
        Assert.Equal(25, saved.TargetUpload);
        Assert.Equal(120_000L, saved.TargetViews);
        Assert.Equal(Today, saved.EffectiveFrom);
        Assert.Null(saved.EffectiveTo);
    }

    // ============================================================
    // Test 3: next-day edit closes old version and creates a new one
    // ============================================================

    [Fact]
    public async Task SaveTarget_NewDayEdit_ClosesOldAndCreatesNewVersion()
    {
        var host = new TestHost();
        var firstDay = Today.AddDays(-1); // created "yesterday"
        await host.Service.SaveTargetAsync(host.Request(host.KkId, upload: 20, views: 100_000, today: firstDay));

        var result = await host.Service.SaveTargetAsync(host.Request(host.KkId, upload: 20, views: 120_000));

        Assert.True(result.Success);
        Assert.True(result.CreatedNewVersion);
        Assert.Equal(2, host.Db.Targets.Count());

        var oldRow = host.Db.Targets.Single(t => t.Id != result.Target!.Id);
        Assert.Equal(firstDay, oldRow.EffectiveFrom);
        Assert.Equal(Today.AddDays(-1), oldRow.EffectiveTo); // closed at yesterday
        Assert.Equal(100_000L, oldRow.TargetViews);          // old value preserved

        var newRow = result.Target!;
        Assert.Equal(Today, newRow.EffectiveFrom);
        Assert.Null(newRow.EffectiveTo);
        Assert.Equal(120_000L, newRow.TargetViews);
    }

    // ============================================================
    // Test 4: multiple same-day saves produce exactly one version
    // ============================================================

    [Fact]
    public async Task SaveTarget_ThreeSavesOnSameDay_ProducesExactlyOneVersion()
    {
        var host = new TestHost();

        await host.Service.SaveTargetAsync(host.Request(host.KkId, 10, 50_000));
        await host.Service.SaveTargetAsync(host.Request(host.KkId, 20, 75_000));
        var result = await host.Service.SaveTargetAsync(host.Request(host.KkId, 30, 150_000));

        Assert.True(result.Success);
        Assert.False(result.CreatedNewVersion);
        Assert.Equal(1, host.Db.Targets.Count());
        var saved = host.Db.Targets.Single();
        Assert.Equal(30, saved.TargetUpload);
        Assert.Equal(150_000L, saved.TargetViews);
        Assert.Equal(Today, saved.EffectiveFrom);
        Assert.Null(saved.EffectiveTo);
    }

    // ============================================================
    // Test 5: historical lookup returns the version effective on reportDate
    // ============================================================

    [Fact]
    public async Task GetTargetForDate_ReturnsVersionEffectiveOnReportDate()
    {
        var host = new TestHost();
        var d20 = new DateOnly(2026, 9, 20);
        var d21 = new DateOnly(2026, 9, 21);
        var d22 = new DateOnly(2026, 9, 22);

        // Build a 3-version history via the service itself (close+create per new day).
        await host.Service.SaveTargetAsync(host.Request(host.KkId, 1, 100, today: d20));   // A
        await host.Service.SaveTargetAsync(host.Request(host.KkId, 2, 200, today: d21));   // B
        await host.Service.SaveTargetAsync(host.Request(host.KkId, 3, 300, today: d22));   // C

        var a = await host.Service.GetTargetForDateAsync(host.KkId, d20);
        var b = await host.Service.GetTargetForDateAsync(host.KkId, d21);
        var c = await host.Service.GetTargetForDateAsync(host.KkId, d22);

        Assert.NotNull(a);
        Assert.Equal(d20, a!.EffectiveFrom);
        Assert.Equal(100L, a.TargetViews);

        Assert.NotNull(b);
        Assert.Equal(d21, b!.EffectiveFrom);
        Assert.Equal(200L, b.TargetViews);

        Assert.NotNull(c);
        Assert.Equal(d22, c!.EffectiveFrom);
        Assert.Equal(300L, c.TargetViews);
        Assert.Null(c.EffectiveTo);
    }

    [Fact]
    public async Task GetTargetForDate_BeforeFirstVersion_ReturnsNull()
    {
        var host = new TestHost();
        await host.Service.SaveTargetAsync(host.Request(host.KkId, 1, 100, today: Today));

        var before = await host.Service.GetTargetForDateAsync(host.KkId, Today.AddDays(-7));

        Assert.Null(before);
    }

    // ============================================================
    // Test 6: AUTO_GMV_LIVE rejected
    // ============================================================

    [Fact]
    public async Task SaveTarget_AutoGmvLive_IsRejected()
    {
        var host = new TestHost();

        var result = await host.Service.SaveTargetAsync(host.Request(host.AutoGmvId, 10, 50_000));

        Assert.False(result.Success);
        Assert.Equal(TargetSaveErrorCodes.ContentTypeNotTargetable, result.ErrorCode);
        Assert.Empty(host.Db.Targets);
    }

    // ============================================================
    // Test 7: unknown content type rejected
    // ============================================================

    [Fact]
    public async Task SaveTarget_UnknownContentType_IsRejected()
    {
        var host = new TestHost();

        var result = await host.Service.SaveTargetAsync(host.Request(contentTypeId: 9999, 10, 50_000));

        Assert.False(result.Success);
        Assert.Equal(TargetSaveErrorCodes.ContentTypeNotFound, result.ErrorCode);
        Assert.Empty(host.Db.Targets);
    }

    // ============================================================
    // Test 8: negative TargetUpload rejected
    // ============================================================

    [Fact]
    public async Task SaveTarget_NegativeTargetUpload_IsRejected()
    {
        var host = new TestHost();

        var result = await host.Service.SaveTargetAsync(host.Request(host.KkId, -1, 50_000));

        Assert.False(result.Success);
        Assert.Equal(TargetSaveErrorCodes.TargetUploadNegative, result.ErrorCode);
        Assert.Empty(host.Db.Targets);
    }

    // ============================================================
    // Test 9: negative TargetViews rejected
    // ============================================================

    [Fact]
    public async Task SaveTarget_NegativeTargetViews_IsRejected()
    {
        var host = new TestHost();

        var result = await host.Service.SaveTargetAsync(host.Request(host.KkId, 10, -5));

        Assert.False(result.Success);
        Assert.Equal(TargetSaveErrorCodes.TargetViewsNegative, result.ErrorCode);
        Assert.Empty(host.Db.Targets);
    }

    // ============================================================
    // Test 10: failure during the new-day save must not close the old version
    // ============================================================

    private sealed class FailingSaveService : TargetService
    {
        public FailingSaveService(AppDbContext db) : base(db, new StubShopTimeZone(Today)) { }

        protected override Task<int> SaveAsync(CancellationToken cancellationToken)
            => throw new InvalidOperationException("simulated store failure after staging");
    }

    [Fact]
    public async Task SaveTarget_NewDaySaveFails_OldVersionRemainsActive()
    {
        var host = new TestHost();
        var firstDay = Today.AddDays(-1);
        await host.Service.SaveTargetAsync(host.Request(host.KkId, 20, 100_000, today: firstDay));

        var failing = new FailingSaveService(host.Db);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => failing.SaveTargetAsync(host.Request(host.KkId, 20, 120_000)));

        // Verify with a FRESH context so staged (unsaved) tracker changes cannot fake the result.
        using var verify = host.NewContext();
        var targets = verify.Targets.ToList();
        var active = targets.Single(t => t.EffectiveTo == null);
        Assert.Equal(firstDay, active.EffectiveFrom);          // old version NOT closed
        Assert.Equal(100_000L, active.TargetViews);            // old values intact
        Assert.Single(targets);                                // no new version persisted
    }

    // ============================================================
    // Extra: current targets read model
    // ============================================================

    [Fact]
    public async Task GetCurrentTargets_ReturnsActiveVersions_NonKkFirst_AutoGmvExcluded()
    {
        var host = new TestHost();
        var yesterday = Today.AddDays(-1);
        await host.Service.SaveTargetAsync(host.Request(host.NonKkId, 10, 100_000, today: yesterday));
        await host.Service.SaveTargetAsync(host.Request(host.NonKkId, 12, 110_000, today: Today)); // new-day version
        await host.Service.SaveTargetAsync(host.Request(host.KkId, 20, 200_000));

        var current = await host.Service.GetCurrentTargetsAsync();

        Assert.Equal(2, current.Count);
        // Fixed display order: NON_KK first, then KK.
        Assert.Equal(NonKk, current[0].ContentTypeCode);
        Assert.Equal(Kk, current[1].ContentTypeCode);
        // Latest version values (new-day edit), not the superseded ones.
        Assert.Equal(host.NonKkId, current[0].ContentTypeId);
        Assert.Equal(12, current[0].TargetUpload);
        Assert.Equal(110_000L, current[0].TargetViews);
        Assert.Equal(Today, current[0].EffectiveFrom);
        Assert.Null(current[0].EffectiveTo);
        Assert.Equal("Non-KK", current[0].ContentTypeName);
        Assert.Equal(200_000L, current[1].TargetViews);
        // AUTO_GMV_LIVE never appears.
        Assert.DoesNotContain(current, x => x.ContentTypeCode == AutoGmv);
    }

    [Fact]
    public async Task GetCurrentTargets_NoTargets_ReturnsTargetableTypesWithZeroValues()
    {
        // Since Phase 4a the service returns one placeholder row per targetable
        // content type (NON_KK/KK) so the UI can render empty inputs.
        var host = new TestHost();

        var current = await host.Service.GetCurrentTargetsAsync();

        Assert.Equal(2, current.Count);
        Assert.All(current, x =>
        {
            Assert.Equal(0, x.TargetUpload);
            Assert.Equal(0L, x.TargetViews);
            Assert.Equal(default, x.EffectiveFrom);
            Assert.Null(x.EffectiveTo);
        });
        Assert.DoesNotContain(current, x => x.ContentTypeCode == AutoGmv);
    }

    // ============================================================
    // Extra: same-day save does not violate uniqueness when active starts today
    // (regression: same-day path must take the update branch, not close+create)
    // ============================================================

    [Fact]
    public async Task SaveTarget_SameDayEditAfterEarlierTodayCreation_NeverCreatesSecondActiveRow()
    {
        var host = new TestHost();
        await host.Service.SaveTargetAsync(host.Request(host.NonKkId, 5, 10_000));

        for (var i = 1; i <= 5; i++)
        {
            var result = await host.Service.SaveTargetAsync(host.Request(host.NonKkId, 5 + i, 10_000L * i));
            Assert.True(result.Success);
            Assert.False(result.CreatedNewVersion);
        }

        Assert.Equal(1, host.Db.Targets.Count(t => t.EffectiveTo == null));
        Assert.Equal(1, host.Db.Targets.Count());
    }

    // ============================================================
    // Phase 4b: Effective Date + ChangedBy
    // ============================================================

    [Fact]
    public async Task SaveTarget_WithEffectiveDate_NewVersionCreated_OldVersionIntact()
    {
        var host = new TestHost();
        var aug1 = new DateOnly(2026, 8, 1);
        var sep1 = new DateOnly(2026, 9, 1);

        await host.Service.SaveTargetAsync(host.Request(host.NonKkId, 10, 20_000, effectiveDate: aug1, changedBy: "user-a"));
        var result = await host.Service.SaveTargetAsync(host.Request(host.NonKkId, 15, 30_000, effectiveDate: sep1, changedBy: "user-b"));

        Assert.True(result.Success);
        Assert.True(result.CreatedNewVersion);
        Assert.Equal(2, host.Db.Targets.Count());

        var aug = host.Db.Targets.Single(t => t.EffectiveFrom == aug1);
        var sep = host.Db.Targets.Single(t => t.EffectiveFrom == sep1);

        // Existing version unchanged (values + audit).
        Assert.Equal(10, aug.TargetUpload);
        Assert.Equal(20_000L, aug.TargetViews);
        Assert.Equal("user-a", aug.ChangedByUserId);

        // New version carries its own audit stamp.
        Assert.Equal(15, sep.TargetUpload);
        Assert.Equal(30_000L, sep.TargetViews);
        Assert.Equal("user-b", sep.ChangedByUserId);
        Assert.True(sep.UpdatedAt >= sep.CreatedAt);
    }

    [Fact]
    public async Task Resolve_Aug15UsesAugTarget_Sep20UsesSepTarget()
    {
        var host = new TestHost();
        var aug1 = new DateOnly(2026, 8, 1);
        var sep1 = new DateOnly(2026, 9, 1);

        await host.Service.SaveTargetAsync(host.Request(host.NonKkId, 10, 20_000, effectiveDate: aug1));
        await host.Service.SaveTargetAsync(host.Request(host.NonKkId, 15, 30_000, effectiveDate: sep1));

        var aug15 = await host.Service.GetTargetForDateAsync(host.NonKkId, new DateOnly(2026, 8, 15));
        var sep20 = await host.Service.GetTargetForDateAsync(host.NonKkId, new DateOnly(2026, 9, 20));
        var nov10 = await host.Service.GetTargetForDateAsync(host.NonKkId, new DateOnly(2026, 11, 10));

        Assert.NotNull(aug15);
        Assert.Equal(10, aug15!.TargetUpload);
        Assert.Equal(20_000L, aug15.TargetViews);

        Assert.NotNull(sep20);
        Assert.Equal(15, sep20!.TargetUpload);
        Assert.Equal(30_000L, sep20.TargetViews);

        // After the last version: latest EffectiveFrom <= D wins (still the Sep version).
        Assert.NotNull(nov10);
        Assert.Equal(15, nov10!.TargetUpload);
    }

    [Fact]
    public async Task SaveTarget_SameEffectiveDate_RevisesExistingVersion_NoDuplicate()
    {
        var host = new HostWithExistingTarget();

        var result = await host.Service.SaveTargetAsync(host.Request(host.NonKkId, 18, 36_000, effectiveDate: HostWithExistingTarget.EffectiveDate, changedBy: "user-revise"));

        Assert.True(result.Success);
        Assert.False(result.CreatedNewVersion);
        Assert.Single(host.Db.Targets);
        var version = host.Db.Targets.Single();
        Assert.Equal(18, version.TargetUpload);
        Assert.Equal(36_000L, version.TargetViews);
        Assert.Equal("user-revise", version.ChangedByUserId);
    }

    [Fact]
    public async Task SaveTarget_FutureEffectiveDate_IsScheduled_NotCurrent()
    {
        var host = new HostWithExistingTarget();
        var future = HostWithExistingTarget.EffectiveDate.AddMonths(2); // e.g. 01 Nov while today is 22 Sep

        var result = await host.Service.SaveTargetAsync(host.Request(host.NonKkId, 20, 40_000, effectiveDate: future, changedBy: "user-sched"));

        Assert.True(result.Success);
        Assert.True(result.CreatedNewVersion);
        Assert.True(result.IsScheduled);
        Assert.Equal(2, host.Db.Targets.Count());

        // The scheduled version must not become "current" before its date.
        var current = await host.Service.GetCurrentTargetsAsync();
        var nonKk = current.Single(x => x.ContentTypeCode == NonKk);
        Assert.Equal(10, nonKk.TargetUpload);          // Sep version still current
        Assert.Equal(HostWithExistingTarget.EffectiveDate, nonKk.EffectiveFrom);

        // Scheduled listing exposes it with the future date.
        var scheduled = await host.Service.GetScheduledTargetsAsync();
        var row = scheduled.Single(x => x.ContentTypeId == host.NonKkId);
        Assert.Equal(future, row.EffectiveFrom);
        Assert.Equal(20, row.TargetUpload);

        // Date-based resolution: before the future date -> Sep version; on/after -> scheduled values.
        var dayBefore = await host.Service.GetTargetForDateAsync(host.NonKkId, future.AddDays(-1));
        Assert.Equal(10, dayBefore!.TargetUpload);
        var onDate = await host.Service.GetTargetForDateAsync(host.NonKkId, future);
        Assert.Equal(20, onDate!.TargetUpload);
    }
    [Fact]
    public async Task SaveTarget_BackdatedVersion_HistoricalPeriodsKeepOlderTarget()
    {
        var host = new HostWithExistingTarget();
        var backdated = HostWithExistingTarget.EffectiveDate.AddMonths(-1); // e.g. 22 Aug while today is 22 Sep

        var result = await host.Service.SaveTargetAsync(host.Request(host.NonKkId, 5, 12_000, effectiveDate: backdated, changedBy: "user-back"));

        Assert.True(result.Success);
        Assert.True(result.CreatedNewVersion);

        // Period before the backdated date keeps the "pre-target" null state? No target existed before,
        // so dates before the backdated version have no target (null) - that is the historical truth.
        var before = await host.Service.GetTargetForDateAsync(host.NonKkId, backdated.AddDays(-1));
        Assert.Null(before);

        // From the backdated date until the day before the existing version: new values.
        var inBetween = await host.Service.GetTargetForDateAsync(host.NonKkId, backdated.AddDays(5));
        Assert.NotNull(inBetween);
        Assert.Equal(5, inBetween!.TargetUpload);
        Assert.Equal(12_000L, inBetween.TargetViews);

        // From the existing version onwards: Sep values unchanged.
        var atExisting = await host.Service.GetTargetForDateAsync(host.NonKkId, HostWithExistingTarget.EffectiveDate);
        Assert.Equal(10, atExisting!.TargetUpload);
    }

    [Fact]
    public async Task GetTargetHistory_ReturnsAllVersions_EffectiveDateDescending()
    {
        var host = new TestHost();
        var aug1 = new DateOnly(2026, 8, 1);
        var sep1 = new DateOnly(2026, 9, 1);

        await host.Service.SaveTargetAsync(host.Request(host.NonKkId, 10, 20_000, effectiveDate: aug1, changedBy: "user-a"));
        await host.Service.SaveTargetAsync(host.Request(host.NonKkId, 15, 30_000, effectiveDate: sep1, changedBy: "user-b"));

        var history = await host.Service.GetTargetHistoryAsync(host.NonKkId);

        Assert.Equal(2, history.Count);
        Assert.Equal(sep1, history[0].EffectiveDate);   // newest first
        Assert.Equal(aug1, history[1].EffectiveDate);
        Assert.Equal("user-b", history[0].ChangedByUserId);
        Assert.Equal("user-a", history[1].ChangedByUserId);
        Assert.NotNull(history[0].ChangedAt);
        Assert.False(history[0].IsScheduled);
    }

    [Fact]
    public async Task GetTargetHistory_UnknownContentType_ReturnsEmpty()
    {
        var host = new TestHost();
        var history = await host.Service.GetTargetHistoryAsync(contentTypeId: 9999);
        Assert.Empty(history);
    }

    [Fact]
    public async Task SaveTarget_ChangedBy_MatchesIdentityUserIdShape()
    {
        // ICurrentUser.UserId is the stable Identity user Id (GUID string). The service
        // only persists what the controller resolved server-side - here proven with a
        // GUID-shaped id (not a display name).
        var host = new TestHost();
        var identityUserId = Guid.NewGuid().ToString();

        var result = await host.Service.SaveTargetAsync(host.Request(host.KkId, 8, 16_000, changedBy: identityUserId));

        Assert.True(result.Success);
        Assert.Equal(identityUserId, host.Db.Targets.Single().ChangedByUserId);
    }

    /// <summary>Host pre-seeded with one existing version at EffectiveDate (via the service save path).</summary>
    private sealed class HostWithExistingTarget : TestHost
    {
        public static readonly DateOnly EffectiveDate = Today;

        public HostWithExistingTarget()
        {
            Service.SaveTargetAsync(Request(NonKkId, 10, 20_000, effectiveDate: EffectiveDate)).GetAwaiter().GetResult();
        }
    }
}
