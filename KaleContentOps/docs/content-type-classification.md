# Content Type Classification

## Overview

Automated classification of video content into three types:

1. **AUTO_GMV_LIVE** - Content that originates from TikTok Shop Live streams (requires both `IsLive=true` AND `HasCommerce=true`)
2. **KK** - Regular videos with product links (detected via `ProductsJson` in ContentMetric)
3. **NON-KK** - Regular videos without product links

## Classification Rules

### Priority Order
1. AUTO_GMV_LIVE (highest)
2. KK  
3. NON-KK (default)

### Deterministic Signals

- **Auto GMV Live**: Requires BOTH signals
  - `IsLive = true` (from TikTok API) 
  - `HasCommerce = true` (from TikTok API)
  - Notes: A live stream without commerce is NOT AUTO_GMV_LIVE

- **KK (With Product Link)**: Requires ProductsJson
  - `ProductsJson` not empty in latest ContentMetric
  - Must contain valid product data (not `[]` or `{}`)

- **NON-KK (Regular Video)**: Default classification
  - No AUTO_GMV_LIVE signals
  - No products in ProductsJson

### Important Constraints

- **Video URL** is NOT used for classification (only as reference link)
- **Video Title** is NOT used for guessing content type
- **ProductsJson must come from ContentMetric**, not inferred from other fields
- Classification only occurs when deterministic signals are available
- If no signals exist, the log is NOT classified (remains null)

## Automatic Classification Pipeline

### On Video Ingestion (TikTokVideoService)
1. Video data fetched from TikTok API
2. `IsLive` and `HasCommerce` flags parsed and stored
3. **Initial classification** applied using available signals (ProductsJson not yet available)
4. If classification successful, `ContentTypeId` set and saved

### On Detail Sync (TikTokDetailsSyncService)
1. After detail metrics sync, new `ContentMetric` includes `ProductsJson`
2. **Re-classification** triggered with complete signal set
3. If new classification differs, `ContentTypeId` updated
4. Changes marked idempotent (same result if re-run)

## Backfill for Historical Data

For existing ContentLogs that may be missing or have incorrect classification:

### Backfill All ContentLogs
```bash
dotnet run --backfill-content-type
```

**What it does:**
- Scans all ContentLogs with valid `VideoId`
- For each, fetches latest ContentMetric to access ProductsJson
- Reclassifies based on complete signal set (IsLive, HasCommerce, ProductsJson)
- Updates ContentTypeId only if necessary (idempotent)
- Batch processes in chunks of 100 for efficiency

**Output:**
- Total ContentLogs processed
- Count of records updated
- Count of records that could not be classified (no signals)

### Backfill Specific Shop
```bash
dotnet run --backfill-content-type --shop=SHOP_CIPHER
```

**What it does:** Same as above, but only for ContentLogs belonging to the specified shop.

### Dry Run (Preview)
```bash
dotnet run --backfill-content-type --dry-run
```

**What it does:**
- Counts total classifiable vs non-classifiable ContentLogs
- Does NOT make any database changes
- Useful for validating data before actual backfill

### Dry Run for Specific Shop
```bash
dotnet run --backfill-content-type --shop=SHOP_CIPHER --dry-run
```

## Implementation Details

### ContentTypeClassifier (Models/ContentTypeClassifier.cs)
Static class with core classification logic:
- `ClassifyContentType(isLive, hasCommerce, productsJson)` - main method
- Returns ContentTypeId (1=KK, 2=NON_KK, 3=AUTO_GMV_LIVE) or null if cannot classify

### BackfillContentTypeService (Services/TikTok/BackfillContentTypeService.cs)
Service for re-classifying historical data:
- `BackfillAllAsync()` - process all ContentLogs
- `BackfillShopAsync(shopCipher)` - process specific shop
- `CountClassifiableAsync()` - report statistics (for dry-run)

### Database Integration
- ContentLog model: Fields `IsLive`, `HasCommerce`, `ContentTypeId`
- ContentMetric model: Field `ProductsJson`
- EF Core handles migrations

## Testing

Unit tests in `KaleContentOps.Tests/TikTok/ContentTypeClassificationTests.cs`:
- 19 test cases covering all classification scenarios
- Edge cases: isLive without commerce, null signals, empty ProductsJson
- Integration tests: ContentLog + ContentMetric interaction
- Backfill idempotency tests
- Run with: `dotnet test`

## Safety Notes

- **No automatic backfill on startup** - backfill only runs when explicitly invoked via CLI
- **Idempotent operations** - safe to run multiple times
- **Batch processing** - efficient for large datasets
- **Development only** - production backfill requires explicit authorization
- **Logging** - all operations logged for audit trail
