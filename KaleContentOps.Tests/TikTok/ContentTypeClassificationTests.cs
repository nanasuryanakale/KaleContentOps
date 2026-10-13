using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using KaleContentOps.Data;
using KaleContentOps.Models;
using KaleContentOps.Services.TikTok;
using Xunit;
using KaleContentOps.Tests;

namespace KaleContentOps.TikTok.Tests;

/// <summary>
/// Tests untuk automated content type classification berdasarkan business rules:
/// 1. AUTO_GMV_LIVE: IsLive=true AND HasCommerce=true
/// 2. KK: ProductsJson tidak kosong (video dengan product link)
/// 3. NON-KK: Video reguler tanpa product link
/// </summary>
public class ContentTypeClassificationTests
{
    private static AppDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static async Task<AppDbContext> CreateDbWithSeededContentTypesAsync()
    {
        var db = CreateDb();

        // Seed content types
        db.ContentTypes.AddRange(
            new ContentType { Id = 1, Code = "KK", Name = "KK (Video with Product Link)" },
            new ContentType { Id = 2, Code = "NON_KK", Name = "NON-KK (Regular Video)" },
            new ContentType { Id = 3, Code = "AUTO_GMV_LIVE", Name = "AUTO GMV LIVE (Shop Live Stream)" }
        );

        db.TikTokShops.Add(new TikTokShop { Id = 1, ShopCipher = "test_shop" });

        await db.SaveChangesAsync();
        return db;
    }

    [Fact]
    public void ClassifyContentType_AutoGmvLive_WhenIsLiveAndHasCommerceTrue()
    {
        // Arrange
        bool? isLive = true;
        bool? hasCommerce = true;
        string? productsJson = null;

        // Act
        var typeId = ContentTypeClassifier.ClassifyContentType(isLive, hasCommerce, productsJson);

        // Assert
        Assert.Equal(ContentTypeClassifier.AUTO_GMV_LIVE_TYPE_ID, typeId);
    }

    [Fact]
    public void ClassifyContentType_KK_WhenProductsJsonPresent()
    {
        // Arrange
        bool? isLive = false;
        bool? hasCommerce = false;
        string productsJson = "[{\"id\":\"123\",\"name\":\"Product\"}]";

        // Act
        var typeId = ContentTypeClassifier.ClassifyContentType(isLive, hasCommerce, productsJson);

        // Assert
        Assert.Equal(ContentTypeClassifier.KK_TYPE_ID, typeId);
    }

    [Fact]
    public void ClassifyContentType_KK_WhenHasCommerceWithoutLive()
    {
        // Arrange
        bool? isLive = false;
        bool? hasCommerce = true;
        string? productsJson = null;

        // Act
        var typeId = ContentTypeClassifier.ClassifyContentType(isLive, hasCommerce, productsJson);

        // Assert
        Assert.Equal(ContentTypeClassifier.KK_TYPE_ID, typeId);
    }

    [Fact]
    public void ClassifyContentType_NonKK_WhenNoSignals()
    {
        // Arrange
        bool? isLive = false;
        bool? hasCommerce = false;
        string? productsJson = null;

        // Act
        var typeId = ContentTypeClassifier.ClassifyContentType(isLive, hasCommerce, productsJson);

        // Assert
        Assert.Equal(ContentTypeClassifier.NON_KK_TYPE_ID, typeId);
    }

    [Fact]
    public void ClassifyContentType_NonKK_WhenIsLiveFalseAndNoCommerce()
    {
        // Arrange
        bool? isLive = false;
        bool? hasCommerce = false;
        string? productsJson = null;

        // Act
        var typeId = ContentTypeClassifier.ClassifyContentType(isLive, hasCommerce, productsJson);

        // Assert
        Assert.Equal(ContentTypeClassifier.NON_KK_TYPE_ID, typeId);
    }

    [Fact]
    public void ClassifyContentType_EmptyProductsJson_TreatedAsNoProducts()
    {
        // Arrange
        bool? isLive = false;
        bool? hasCommerce = false;
        string productsJson = "[]";

        // Act
        var typeId = ContentTypeClassifier.ClassifyContentType(isLive, hasCommerce, productsJson);

        // Assert
        Assert.Equal(ContentTypeClassifier.NON_KK_TYPE_ID, typeId);
    }

    [Fact]
    public void ClassifyContentType_WhitespaceProductsJson_TreatedAsNoProducts()
    {
        // Arrange
        bool? isLive = false;
        bool? hasCommerce = false;
        string productsJson = "   ";

        // Act
        var typeId = ContentTypeClassifier.ClassifyContentType(isLive, hasCommerce, productsJson);

        // Assert
        Assert.Equal(ContentTypeClassifier.NON_KK_TYPE_ID, typeId);
    }

    [Fact]
    public void ClassifyContentType_PriorityAutoGmvLive_OverriddenByProductsJson()
    {
        // Arrange: Auto GMV Live signals present
        bool? isLive = true;
        bool? hasCommerce = true;
        string productsJson = "[{\"id\":\"123\",\"name\":\"Product\"}]";

        // Act
        var typeId = ContentTypeClassifier.ClassifyContentType(isLive, hasCommerce, productsJson);

        // Assert: Priority 1 should win (AUTO_GMV_LIVE)
        Assert.Equal(ContentTypeClassifier.AUTO_GMV_LIVE_TYPE_ID, typeId);
    }

    [Fact]
    public async Task ContentLogCreation_SetsInitialContentTypeId_FromApiSignals()
    {
        // Arrange
        var db = await CreateDbWithSeededContentTypesAsync();
        var log = new ContentLog
        {
            VideoId = "V1",
            TikTokShopId = 1,
            Title = "Test Video",
            Username = "testuser",
            IsLive = true,
            HasCommerce = true
        };

        // Manually classify (simulating what FetchAndSaveVideoListAsync does)
        log.ContentTypeId = ContentTypeClassifier.ClassifyContentType(log.IsLive, log.HasCommerce, null);

        db.ContentLogs.Add(log);
        await db.SaveChangesAsync();

        // Act
        var saved = await db.ContentLogs.FirstAsync(cl => cl.VideoId == "V1");

        // Assert
        Assert.NotNull(saved.ContentTypeId);
        Assert.Equal(ContentTypeClassifier.AUTO_GMV_LIVE_TYPE_ID, saved.ContentTypeId);
    }

    [Fact]
    public async Task ContentMetricWithProducts_ReClassifiesContentType_ToKK()
    {
        // Arrange
        var db = await CreateDbWithSeededContentTypesAsync();
        var log = new ContentLog
        {
            VideoId = "V2",
            TikTokShopId = 1,
            Title = "Test Video",
            Username = "testuser",
            IsLive = false,
            HasCommerce = false,
            ContentTypeId = ContentTypeClassifier.NON_KK_TYPE_ID // Initially NON-KK
        };

        db.ContentLogs.Add(log);
        await db.SaveChangesAsync();

        // Act: Add ContentMetric with products
        var metric = new ContentMetric
        {
            ContentLogId = log.Id,
            Views = 100,
            ProductsJson = "[{\"id\":\"456\",\"name\":\"Kale Shirt\"}]"
        };
        db.ContentMetrics.Add(metric);
        await db.SaveChangesAsync();

        // Re-classify based on products
        var reclassified = ContentTypeClassifier.ClassifyContentType(log.IsLive, log.HasCommerce, metric.ProductsJson);
        log.ContentTypeId = reclassified;
        await db.SaveChangesAsync();

        // Assert
        var updated = await db.ContentLogs.FirstAsync(cl => cl.VideoId == "V2");
        Assert.Equal(ContentTypeClassifier.KK_TYPE_ID, updated.ContentTypeId);
    }

    [Fact]
    public async Task UpdatedContentLog_PreservesContentTypeId_IfSignalsUnchanged()
    {
        // Arrange
        var db = await CreateDbWithSeededContentTypesAsync();
        var log = new ContentLog
        {
            VideoId = "V3",
            TikTokShopId = 1,
            Title = "Test Video",
            Username = "testuser",
            IsLive = false,
            HasCommerce = true,
            ContentTypeId = ContentTypeClassifier.KK_TYPE_ID
        };

        db.ContentLogs.Add(log);
        await db.SaveChangesAsync();

        var originalContentTypeId = log.ContentTypeId;

        // Act: Update other fields (simulating sync update)
        log.Title = "Updated Title";
        log.Duration = 30;

        // Re-classify with same signals
        var reclassified = ContentTypeClassifier.ClassifyContentType(log.IsLive, log.HasCommerce, null);
        if (reclassified.HasValue && !log.ContentTypeId.HasValue)
        {
            log.ContentTypeId = reclassified;
        }

        db.Update(log);
        await db.SaveChangesAsync();

        // Assert
        var updated = await db.ContentLogs.FirstAsync(cl => cl.VideoId == "V3");
        Assert.Equal(originalContentTypeId, updated.ContentTypeId);
        Assert.Equal("Updated Title", updated.Title);
    }

    [Fact]
    public async Task BackfillService_ClassifiesUnclassifiedLogs()
    {
        // Arrange
        var db = await CreateDbWithSeededContentTypesAsync();

        var log1 = new ContentLog
        {
            VideoId = "V4",
            TikTokShopId = 1,
            Title = "Live Video",
            Username = "user1",
            IsLive = true,
            HasCommerce = true
            // ContentTypeId not set initially
        };

        var log2 = new ContentLog
        {
            VideoId = "V5",
            TikTokShopId = 1,
            Title = "Regular Video",
            Username = "user2",
            IsLive = false,
            HasCommerce = false
            // ContentTypeId not set initially
        };

        db.ContentLogs.AddRange(log1, log2);
        await db.SaveChangesAsync();

        var backfillService = new BackfillContentTypeService(db, NullLogger<BackfillContentTypeService>.Instance);

        // Act
        var (totalProcessed, updated, couldNotClassify) = await backfillService.BackfillAllAsync();

        // Assert
        Assert.Equal(2, totalProcessed);
        Assert.Equal(2, updated);
        Assert.Equal(0, couldNotClassify);

        var v4 = await db.ContentLogs.FirstAsync(cl => cl.VideoId == "V4");
        var v5 = await db.ContentLogs.FirstAsync(cl => cl.VideoId == "V5");

        Assert.Equal(ContentTypeClassifier.AUTO_GMV_LIVE_TYPE_ID, v4.ContentTypeId);
        Assert.Equal(ContentTypeClassifier.NON_KK_TYPE_ID, v5.ContentTypeId);
    }

    [Fact]
    public async Task BackfillService_IdempotentAcrossMultipleRuns()
    {
        // Arrange
        var db = await CreateDbWithSeededContentTypesAsync();

        var log = new ContentLog
        {
            VideoId = "V6",
            TikTokShopId = 1,
            Title = "Test Video",
            Username = "user",
            IsLive = false,
            HasCommerce = true
        };

        db.ContentLogs.Add(log);
        await db.SaveChangesAsync();

        var backfillService = new BackfillContentTypeService(db, NullLogger<BackfillContentTypeService>.Instance);

        // Act: First backfill run
        var (total1, updated1, _) = await backfillService.BackfillAllAsync();
        var contentTypeId1 = (await db.ContentLogs.FirstAsync(cl => cl.VideoId == "V6")).ContentTypeId;

        // Act: Second backfill run (should not change anything)
        var (total2, updated2, _) = await backfillService.BackfillAllAsync();
        var contentTypeId2 = (await db.ContentLogs.FirstAsync(cl => cl.VideoId == "V6")).ContentTypeId;

        // Assert
        Assert.Equal(1, total1);
        Assert.Equal(1, updated1);
        Assert.Equal(1, total2);
        Assert.Equal(0, updated2); // No updates on second run
        Assert.Equal(contentTypeId1, contentTypeId2);
    }

    [Fact]
    public async Task BackfillService_SkipsLogsWithoutClassificationSignals()
    {
        // Arrange
        var db = await CreateDbWithSeededContentTypesAsync();

        var logNoSignals = new ContentLog
        {
            VideoId = "V7",
            TikTokShopId = 1,
            Title = "Mystery Video",
            Username = "user"
            // No IsLive, HasCommerce, or ProductsJson
        };

        var logWithSignals = new ContentLog
        {
            VideoId = "V8",
            TikTokShopId = 1,
            Title = "Live Video",
            Username = "user2",
            IsLive = true,
            HasCommerce = true
        };

        db.ContentLogs.AddRange(logNoSignals, logWithSignals);
        await db.SaveChangesAsync();

        var backfillService = new BackfillContentTypeService(db, NullLogger<BackfillContentTypeService>.Instance);

        // Act
        var (totalProcessed, updated, couldNotClassify) = await backfillService.BackfillAllAsync();

        // Assert
        Assert.Equal(2, totalProcessed);
        Assert.Equal(1, updated); // Only logWithSignals
        Assert.Equal(1, couldNotClassify); // logNoSignals skipped

        var v7 = await db.ContentLogs.FirstAsync(cl => cl.VideoId == "V7");
        var v8 = await db.ContentLogs.FirstAsync(cl => cl.VideoId == "V8");

        Assert.Null(v7.ContentTypeId); // Unchanged
        Assert.NotNull(v8.ContentTypeId); // Classified
    }

    [Fact]
    public async Task BackfillService_WorksWithMultipleShops()
    {
        // Arrange
        var db = await CreateDbWithSeededContentTypesAsync();

        // Add another shop
        var shop2 = new TikTokShop { Id = 2, ShopCipher = "shop2" };
        db.TikTokShops.Add(shop2);
        await db.SaveChangesAsync();

        var logShop1 = new ContentLog
        {
            VideoId = "V9",
            TikTokShopId = 1,
            Title = "Shop1 Video",
            Username = "user1",
            IsLive = true,
            HasCommerce = true
        };

        var logShop2 = new ContentLog
        {
            VideoId = "V10",
            TikTokShopId = 2,
            Title = "Shop2 Video",
            Username = "user2",
            IsLive = false,
            HasCommerce = false
        };

        db.ContentLogs.AddRange(logShop1, logShop2);
        await db.SaveChangesAsync();

        var backfillService = new BackfillContentTypeService(db, NullLogger<BackfillContentTypeService>.Instance);

        // Act
        var (total, updated, _) = await backfillService.BackfillAllAsync();

        // Assert
        Assert.Equal(2, total);
        Assert.Equal(2, updated);

        var v9 = await db.ContentLogs.FirstAsync(cl => cl.VideoId == "V9");
        var v10 = await db.ContentLogs.FirstAsync(cl => cl.VideoId == "V10");

        Assert.Equal(ContentTypeClassifier.AUTO_GMV_LIVE_TYPE_ID, v9.ContentTypeId);
        Assert.Equal(ContentTypeClassifier.NON_KK_TYPE_ID, v10.ContentTypeId);
    }

    [Fact]
    public void GetCodeForId_ReturnsCorrectCode()
    {
        // Act & Assert
        Assert.Equal(ContentTypeClassifier.KK_CODE, ContentTypeClassifier.GetCodeForId(ContentTypeClassifier.KK_TYPE_ID));
        Assert.Equal(ContentTypeClassifier.NON_KK_CODE, ContentTypeClassifier.GetCodeForId(ContentTypeClassifier.NON_KK_TYPE_ID));
        Assert.Equal(ContentTypeClassifier.AUTO_GMV_LIVE_CODE, ContentTypeClassifier.GetCodeForId(ContentTypeClassifier.AUTO_GMV_LIVE_TYPE_ID));
        Assert.Null(ContentTypeClassifier.GetCodeForId(null));
        Assert.Null(ContentTypeClassifier.GetCodeForId(999));
    }

    [Fact]
    public void ClassifyContentType_IsLiveTrue_WithoutCommerce_ShouldNotBeAutoGmvLive()
    {
        // Edge case: Live stream but NO commerce capability -> should NOT be AUTO_GMV_LIVE
        // Arrange
        bool? isLive = true;
        bool? hasCommerce = false;
        string? productsJson = null;

        // Act
        var typeId = ContentTypeClassifier.ClassifyContentType(isLive, hasCommerce, productsJson);

        // Assert: Should be NON-KK (not AUTO_GMV_LIVE, which requires BOTH isLive AND hasCommerce)
        Assert.Equal(ContentTypeClassifier.NON_KK_TYPE_ID, typeId);
    }

    [Fact]
    public void ClassifyContentType_IsLiveNull_WithCommerce_ShouldBeKK()
    {
        // Edge case: Commerce but IsLive signal not available -> should be KK
        // Arrange
        bool? isLive = null;
        bool? hasCommerce = true;
        string? productsJson = null;

        // Act
        var typeId = ContentTypeClassifier.ClassifyContentType(isLive, hasCommerce, productsJson);

        // Assert: Should be KK (hasCommerce alone makes it KK)
        Assert.Equal(ContentTypeClassifier.KK_TYPE_ID, typeId);
    }

    [Fact]
    public void ClassifyContentType_ProductsJson_OverridesDefaultClassification()
    {
        // TEST: Ensure ProductsJson presence is deterministic for KK classification
        // Arrange
        bool? isLive = false;
        bool? hasCommerce = false;
        string productsJson = "[{\"id\":\"789\",\"name\":\"Product\"}]";

        // Act
        var typeId = ContentTypeClassifier.ClassifyContentType(isLive, hasCommerce, productsJson);

        // Assert: Should be KK because ProductsJson is present
        Assert.Equal(ContentTypeClassifier.KK_TYPE_ID, typeId);
    }
}
