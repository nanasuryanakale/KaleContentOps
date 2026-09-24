using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using KaleContentOps.Controllers;
using KaleContentOps.Models;
using KaleContentOps.Security;
using KaleContentOps.Services;
using KaleContentOps.Services.Targets;
using KaleContentOps.ViewModels;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace KaleContentOps.Tests.Targets;

/// <summary>
/// Phase 3 API tests for TargetsController (unit tests over a stubbed ITargetService,
/// following the existing project pattern: xunit + hand-written stubs, no new framework).
/// Verifies result/DTO mapping, error-code to HTTP-status mapping, and the server-date
/// boundary (the request payload can never influence the effective date).
/// </summary>
public class TargetsControllerTests
{
    private const string NonKk = TargetService.NonKkCode; // "NON_KK"
    private const string Kk = TargetService.KkCode;       // "KK"

    private static readonly DateOnly Today = new(2026, 9, 22);

    // ------------------------------------------------------------------
    // Test doubles
    // ------------------------------------------------------------------

    private sealed class StubTargetService : ITargetService
    {
        public IReadOnlyList<TargetCurrentItem> CurrentItems { get; set; } =
            Array.Empty<TargetCurrentItem>();

        public TargetSaveRequest? CapturedRequest { get; private set; }
        public TargetSaveResult NextSaveResult { get; set; } =
            TargetSaveResult.Fail(TargetSaveErrorCodes.ContentTypeNotFound, "not configured");
        public Exception? ThrowOnSave { get; set; }

        public Task<IReadOnlyList<TargetCurrentItem>> GetCurrentTargetsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(CurrentItems);

        public Task<IReadOnlyList<TargetCurrentItem>> GetScheduledTargetsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<TargetCurrentItem>>(Array.Empty<TargetCurrentItem>());

        public Task<IReadOnlyList<TargetHistoryItem>> GetTargetHistoryAsync(int contentTypeId, int maxRows = 50, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<TargetHistoryItem>>(Array.Empty<TargetHistoryItem>());

        public Task<Target?> GetTargetForDateAsync(int contentTypeId, DateOnly reportDate, CancellationToken cancellationToken = default)
            => Task.FromResult<Target?>(null);

        // Compile fix (pre-existing): interface member added in Phase 4a but never stubbed here.
        public Task<TargetActualItem?> GetActualAsync(int contentTypeId, DateOnly endDate, int days = 7, CancellationToken cancellationToken = default)
            => Task.FromResult<TargetActualItem?>(null);

        // Phase 5: controller-level contract only (authorization + passthrough); the
        // business calculation itself is covered by TargetActualsPhase5Tests.
        public Task<TargetActualsSummary> GetActualsSummaryAsync(DateOnly endDate, int days = 7, CancellationToken cancellationToken = default)
            => Task.FromResult(new TargetActualsSummary
            {
                StartDate = endDate.AddDays(-(days - 1)),
                EndDate = endDate,
                Days = days,
                TimeZoneId = ShopTimeZoneOptions.DefaultTimeZoneId,
                Items = Array.Empty<TargetActualItem>()
            });

        public Task<TargetSaveResult> SaveTargetAsync(TargetSaveRequest request, CancellationToken cancellationToken = default)
        {
            if (ThrowOnSave is not null) throw ThrowOnSave;
            CapturedRequest = request;
            return Task.FromResult(NextSaveResult);
        }
    }

    private sealed class StubShopTimeZone : IShopTimeZone
    {
        public string TimeZoneId => ShopTimeZoneOptions.DefaultTimeZoneId;
        public DateTimeOffset ToShopLocal(DateTimeOffset utcInstant) => utcInstant;
        public DateOnly GetShopLocalDate(DateTimeOffset utcInstant) => DateOnly.FromDateTime(utcInstant.DateTime);
        public DateOnly Today() => TodayConstant;
        public DateTime ToDateTime(DateOnly shopLocalDate) => shopLocalDate.ToDateTime(TimeOnly.MinValue);
        public DateTime TodayMidnight() => ToDateTime(TodayConstant);

        // Qualified: the interface method Today() shadows the outer field inside this class.
        public static readonly DateOnly TodayConstant = TargetsControllerTests.Today;
    }

    private static TargetsController CreateController(
        StubTargetService service,
        IShopTimeZone? timeZone = null)
        => new(service, timeZone ?? new StubShopTimeZone(), new StubCurrentUser());

    /// <summary>Authenticated principal stub resolving a stable Identity user id server-side.</summary>
    private sealed class StubCurrentUser : ICurrentUser
    {
        public string? UserId { get; set; } = "8f0c2d64-6f2e-4a2b-9a3f-0d5c1e7b9a11"; // stable Identity user id shape
        public string? UserName { get; set; } = "nana";
        public string? DisplayName { get; set; } = "Nana";
        public bool IsAuthenticated => UserId is not null;

        public bool HasPermission(string permission) => false; // controller relies on [Authorize] policies, not this
    }

    private static TargetSaveDto Dto(int contentTypeId = 2, int upload = 10, long views = 100_000, DateOnly? effectiveDate = null) =>
        new() { ContentTypeId = contentTypeId, TargetUpload = upload, TargetViews = views, EffectiveDate = effectiveDate };

    private static Target SavedTarget(int contentTypeId, int upload = 10, long views = 100_000) => new()
    {
        ContentTypeId = contentTypeId,
        TargetUpload = upload,
        TargetViews = views,
        EffectiveFrom = Today,
        EffectiveTo = null
    };

    // ==================================================================
    // GET targets/current
    // ==================================================================

    [Fact]
    public async Task Current_NonKkAndKkPresent_ReturnsBothWithOk()
    {
        var service = new StubTargetService
        {
            CurrentItems = new List<TargetCurrentItem>
            {
                new() { ContentTypeId = 2, ContentTypeCode = NonKk, ContentTypeName = "Non-KK", TargetUpload = 12, TargetViews = 110_000, EffectiveFrom = Today, EffectiveTo = null },
                new() { ContentTypeId = 1, ContentTypeCode = Kk, ContentTypeName = "Keranjang Kuning", TargetUpload = 20, TargetViews = 200_000, EffectiveFrom = Today, EffectiveTo = null }
            }
        };
        var controller = CreateController(service);

        var actionResult = await controller.Current(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(actionResult);
        var items = Assert.IsAssignableFrom<IEnumerable<TargetCurrentItem>>(ok.Value);
        var list = items.ToList();
        Assert.Equal(2, list.Count);
        Assert.Contains(list, x => x.ContentTypeCode == NonKk && x.TargetUpload == 12 && x.TargetViews == 110_000L && x.EffectiveFrom == Today && x.EffectiveTo == null);
        Assert.Contains(list, x => x.ContentTypeCode == Kk && x.TargetViews == 200_000L);
    }

    [Fact]
    public async Task Current_AutoGmvLiveFromService_IsNotExposedToClient()
    {
        // Defensive: the service never returns AUTO_GMV_LIVE, but the controller must not
        // re-introduce it either. Whatever the service gives is passed through unmodified.
        var service = new StubTargetService
        {
            CurrentItems = new List<TargetCurrentItem>
            {
                new() { ContentTypeId = 2, ContentTypeCode = NonKk, ContentTypeName = "Non-KK", TargetUpload = 5, TargetViews = 50_000, EffectiveFrom = Today, EffectiveTo = null },
                new() { ContentTypeId = 1, ContentTypeCode = Kk, ContentTypeName = "Keranjang Kuning", TargetUpload = 6, TargetViews = 60_000, EffectiveFrom = Today, EffectiveTo = null }
            }
        };
        var controller = CreateController(service);

        var actionResult = await controller.Current(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(actionResult);
        var list = Assert.IsAssignableFrom<IEnumerable<TargetCurrentItem>>(ok.Value).ToList();
        Assert.Equal(2, list.Count);
        Assert.DoesNotContain(list, x => x.ContentTypeCode == "AUTO_GMV_LIVE");
    }

    [Fact]
    public async Task Current_NoTargetsYet_ReturnsEmptyCollectionWithOk()
    {
        var service = new StubTargetService { CurrentItems = Array.Empty<TargetCurrentItem>() };
        var controller = CreateController(service);

        var actionResult = await controller.Current(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(actionResult);
        var list = Assert.IsAssignableFrom<IEnumerable<TargetCurrentItem>>(ok.Value).ToList();
        Assert.Empty(list);
    }

    // ==================================================================
    // POST targets/save - happy paths
    // ==================================================================

    [Fact]
    public async Task Save_ValidNonKk_ReturnsOkWithFinalState()
    {
        var service = new StubTargetService
        {
            NextSaveResult = TargetSaveResult.Ok(SavedTarget(contentTypeId: 2), NonKk, createdNewVersion: false)
        };
        var controller = CreateController(service);

        var actionResult = await controller.Save(Dto(contentTypeId: 2, upload: 10, views: 100_000), CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(actionResult);
        var response = Assert.IsType<TargetSaveResponseDto>(ok.Value);
        Assert.True(response.Success);
        Assert.Equal(2, response.ContentTypeId);
        Assert.Equal(NonKk, response.ContentTypeCode);
        Assert.Equal(10, response.TargetUpload);
        Assert.Equal(100_000L, response.TargetViews);
        Assert.Equal(Today, response.EffectiveFrom);
        Assert.Null(response.EffectiveTo);
        Assert.False(response.CreatedNewVersion);
    }

    [Fact]
    public async Task Save_ValidKk_ReturnsOkWithFinalState()
    {
        var service = new StubTargetService
        {
            NextSaveResult = TargetSaveResult.Ok(SavedTarget(contentTypeId: 1, upload: 25, views: 120_000), Kk, createdNewVersion: true)
        };
        var controller = CreateController(service);

        var actionResult = await controller.Save(Dto(contentTypeId: 1, upload: 25, views: 120_000), CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(actionResult);
        var response = Assert.IsType<TargetSaveResponseDto>(ok.Value);
        Assert.True(response.Success);
        Assert.Equal(Kk, response.ContentTypeCode);
        Assert.Equal(25, response.TargetUpload);
        Assert.Equal(120_000L, response.TargetViews);
        Assert.True(response.CreatedNewVersion);
    }

    // ==================================================================
    // POST targets/save - error mapping
    // ==================================================================

    [Fact]
    public async Task Save_AutoGmvLive_ReturnsUnprocessableEntityWithCode()
    {
        var service = new StubTargetService
        {
            NextSaveResult = TargetSaveResult.Fail(
                TargetSaveErrorCodes.ContentTypeNotTargetable,
                "Content type 'AUTO_GMV_LIVE' tidak boleh memiliki target.")
        };
        var controller = CreateController(service);

        var actionResult = await controller.Save(Dto(contentTypeId: 3), CancellationToken.None);

        var unprocessable = Assert.IsType<UnprocessableEntityObjectResult>(actionResult);
        var code = (string)unprocessable.Value!.GetType().GetProperty("code")!.GetValue(unprocessable.Value)!;
        var error = (string)unprocessable.Value.GetType().GetProperty("error")!.GetValue(unprocessable.Value)!;
        Assert.Equal(TargetSaveErrorCodes.ContentTypeNotTargetable, code);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public async Task Save_UnknownContentType_ReturnsNotFoundWithCode()
    {
        var service = new StubTargetService
        {
            NextSaveResult = TargetSaveResult.Fail(
                TargetSaveErrorCodes.ContentTypeNotFound,
                "Content type dengan id 9999 tidak ditemukan.")
        };
        var controller = CreateController(service);

        var actionResult = await controller.Save(Dto(contentTypeId: 9999), CancellationToken.None);

        var notFound = Assert.IsType<NotFoundObjectResult>(actionResult);
        var code = (string)notFound.Value!.GetType().GetProperty("code")!.GetValue(notFound.Value)!;
        Assert.Equal(TargetSaveErrorCodes.ContentTypeNotFound, code);
    }

    [Fact]
    public async Task Save_NegativeTargetUpload_ReturnsUnprocessableEntity()
    {
        var service = new StubTargetService
        {
            NextSaveResult = TargetSaveResult.Fail(
                TargetSaveErrorCodes.TargetUploadNegative,
                "Target Upload tidak boleh negatif.")
        };
        var controller = CreateController(service);

        var actionResult = await controller.Save(Dto(contentTypeId: 2, upload: -1), CancellationToken.None);

        Assert.IsType<UnprocessableEntityObjectResult>(actionResult);
    }

    [Fact]
    public async Task Save_NegativeTargetViews_ReturnsUnprocessableEntity()
    {
        var service = new StubTargetService
        {
            NextSaveResult = TargetSaveResult.Fail(
                TargetSaveErrorCodes.TargetViewsNegative,
                "Target Views tidak boleh negatif.")
        };
        var controller = CreateController(service);

        var actionResult = await controller.Save(Dto(contentTypeId: 2, upload: 10, views: -5), CancellationToken.None);

        Assert.IsType<UnprocessableEntityObjectResult>(actionResult);
    }

    // ==================================================================
    // POST targets/save - server date boundary
    // ==================================================================

    [Fact]
    public async Task Save_EffectiveDateComesOnlyFromServerClock()
    {
        var service = new StubTargetService
        {
            NextSaveResult = TargetSaveResult.Ok(SavedTarget(contentTypeId: 2), NonKk, createdNewVersion: false)
        };
        var controller = CreateController(service);

        // No EffectiveDate in the payload: Today is set by the controller from IShopTimeZone.
        await controller.Save(Dto(), CancellationToken.None);

        var captured = Assert.IsType<TargetSaveRequest>(service.CapturedRequest);
        Assert.Equal(Today, captured.Today);
        Assert.Null(captured.EffectiveDate);
    }

    [Fact]
    public async Task Save_WithEffectiveDate_PassesThroughToService()
    {
        var service = new StubTargetService
        {
            NextSaveResult = TargetSaveResult.Ok(SavedTarget(contentTypeId: 2), NonKk, createdNewVersion: true)
        };
        var controller = CreateController(service);
        var effectiveDate = new DateOnly(2026, 11, 1); // future date is allowed (scheduled)

        await controller.Save(Dto(effectiveDate: effectiveDate), CancellationToken.None);

        var captured = Assert.IsType<TargetSaveRequest>(service.CapturedRequest);
        Assert.Equal(effectiveDate, captured.EffectiveDate);
        Assert.Equal(Today, captured.Today); // server clock still supplies "today"
    }

    [Fact]
    public async Task Save_ChangedByUserId_AlwaysFromServerSidePrincipal()
    {
        var service = new StubTargetService
        {
            NextSaveResult = TargetSaveResult.Ok(SavedTarget(contentTypeId: 2), NonKk, createdNewVersion: true)
        };
        var controller = CreateController(service);

        await controller.Save(Dto(), CancellationToken.None);

        var captured = Assert.IsType<TargetSaveRequest>(service.CapturedRequest);
        // Resolved from ICurrentUser (server-side), never from the request body.
        Assert.Equal("8f0c2d64-6f2e-4a2b-9a3f-0d5c1e7b9a11", captured.ChangedByUserId);
    }

    [Fact]
    public async Task Save_AnonymousRequest_ChangedByUserIdIsNull()
    {
        // (Save requires Target.Edit so this cannot happen in production; defensive check.)
        // Simulate an unauthenticated context via a principal-less stub.
        var service = new StubTargetService
        {
            NextSaveResult = TargetSaveResult.Ok(SavedTarget(contentTypeId: 2), NonKk, createdNewVersion: true)
        };
        var controller = new TargetsController(service, new StubShopTimeZone(), new StubCurrentUser { UserId = null });

        await controller.Save(Dto(), CancellationToken.None);

        Assert.Null(service.CapturedRequest!.ChangedByUserId);
    }

    [Fact]
    public async Task Save_ScheduledResult_MapsIsScheduledToResponse()
    {
        var service = new StubTargetService
        {
            NextSaveResult = TargetSaveResult.Ok(
                new Target { ContentTypeId = 2, TargetUpload = 20, TargetViews = 40_000, EffectiveFrom = new DateOnly(2026, 11, 1) },
                NonKk, createdNewVersion: true, isScheduled: true)
        };
        var controller = CreateController(service);

        var actionResult = await controller.Save(Dto(effectiveDate: new DateOnly(2026, 11, 1)), CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(actionResult);
        var response = Assert.IsType<TargetSaveResponseDto>(ok.Value);
        Assert.True(response.IsScheduled);
        Assert.Equal(new DateOnly(2026, 11, 1), response.EffectiveFrom);
    }

    // ==================================================================
    // GET targets/history - authorization + passthrough (Phase 4b)
    // ==================================================================

    [Fact]
    public async Task History_ReturnsServiceRows()
    {
        var service = new StubTargetService();
        var controller = CreateController(service);

        // Stub returns an empty list; the endpoint must still return Ok with a list.
        var actionResult = await controller.History(contentTypeId: 2, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(actionResult);
        Assert.IsAssignableFrom<IEnumerable<TargetHistoryItem>>(ok.Value);
    }

    // ==================================================================
    // POST targets/save - infrastructure failures are NOT business errors
    // ==================================================================

    [Fact]
    public async Task Save_InfrastructureFailure_Returns500NotBusinessError()
    {
        var service = new StubTargetService { ThrowOnSave = new InvalidOperationException("store down") };
        var controller = CreateController(service);

        var actionResult = await controller.Save(Dto(), CancellationToken.None);

        var problem = Assert.IsType<ObjectResult>(actionResult);
        Assert.Equal(StatusCodes.Status500InternalServerError, problem.StatusCode);
        // ProblemDetails must not carry a business validation code.
        var problemDetails = Assert.IsType<ProblemDetails>(problem.Value);
        Assert.DoesNotContain(problemDetails.Extensions, kv => kv.Key == "code");
    }

    // ==================================================================
    // POST targets/save - null body
    // ==================================================================

    [Fact]
    public async Task Save_NullBody_ReturnsBadRequest()
    {
        var controller = CreateController(new StubTargetService());

        var actionResult = await controller.Save(null!, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(actionResult);
    }
}
