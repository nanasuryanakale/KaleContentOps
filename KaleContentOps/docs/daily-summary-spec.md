# KALE Content Ops — Daily Summary Specification
## Version 2.0 — Updated after Manager confirmation

> This document is the implementation reference for the Daily Summary module. It contains confirmed business rules, current mockup/reference behavior, and unresolved items. Mockup sample values are test/reference data, not permanent constants.

## 1. Project Context

Application: KALE Content Ops

Stack:
- ASP.NET Core MVC
- C#
- Entity Framework Core
- SQL Server

Existing related module: Content Log

Purpose: summarize daily content count and accumulated/current views by:
- Non-KK
- Keranjang Kuning
- Auto GMV Live

## 2. Context Priority

When information conflicts, use this priority:
1. Existing production code/schema and established business logic
2. Confirmed Manager rules in this specification
3. Existing application behavior
4. Mockup visual behavior
5. Mockup/sample values as test data only

Do not hardcode mockup sample targets or invent missing business rules.

## 3. Confirmed Reporting Date

Use the actual TikTok `VideoPostTime` as the reporting date.

Example: a video uploaded/scheduled on Sept 1 but actually posted on Sept 2 belongs to Sept 2.

Do not use upload-created date or scheduled-created date as the Daily Summary reporting date.

## 4. Confirmed Views Semantics

Daily Summary uses the video's latest/current accumulated Views value.

It is NOT a historical "views gained on that day" or "views as-of each selected date" report.

Example: a video posted Sept 10 and viewed on Sept 18 uses its latest/current accumulated Views when the report is retrieved. The VideoPostTime determines the date row; latest Views determines the metric value.

Existing ContentMetrics contains historical snapshots, but Daily Summary should use the latest metric according to this confirmed rule.

## 5. Archived Content Filter

Manager confirmed that archived content must be optionally included/excluded.

Filter behavior:
- Include archived = archived records participate in all calculations
- Exclude archived = archived records are excluded from all calculations

The filter affects:
- daily content count
- daily views
- TOTAL
- summary
- average
- composition
- target achievement

Default filter state is not yet confirmed; do not invent a permanent default if project conventions do not define one.

## 6. Content Classification

Business expectation: uploaded content should have a category.

Relevant categories:
- Non-KK
- Keranjang Kuning
- Auto GMV Live

No additional business category should be invented.

If a database record has null/missing ContentType, treat it as a data-quality/unknown classification issue and follow existing project conventions; do not silently assign a new business category.

## 7. Confirmed Target Business Model

Targets are manually controlled by management and may change over time.

Current beta/testing values:

### Non-KK
- Content target: 3 videos/day
- Views target: 2,000 views/video

### Keranjang Kuning
- Content target: 15 videos/day
- Views target: 1,000 views/video

These values must NOT be hardcoded into production code.
They are configuration/master data.

## 8. Target Scope

Confirmed target scope:
- target belongs to the KALE account/content operation
- target is then allocated to PICs manually
- allocation does not have to be equal
- beta testing may use arbitrary/manual allocations such as 50:50

The current Daily Summary mockup is at overall account level and does not show PIC rows/columns.

Do not add PIC columns to Daily Summary unless a later requirement requests it.

PIC allocation may be a separate management/configuration concern.

## 9. Target Metric Model

Current target inputs are conceptually:
- daily content target
- views target per content

Derived daily views target:
`DailyViewsTarget = DailyContentTarget * TargetViewsPerVideo`

Current beta values:
- Non-KK: 3 * 2,000 = 6,000 views/day
- Keranjang Kuning: 15 * 1,000 = 15,000 views/day

Do not hardcode these values.

## 10. Weekly Target Derivation

Based on 7 days:

Non-KK:
- content = 3 * 7 = 21/week
- views = 3 * 2,000 * 7 = 42,000/week

Keranjang Kuning:
- content = 15 * 7 = 105/week
- views = 15 * 1,000 * 7 = 105,000/week

These are derived/configuration values only; do not hardcode.

If the implementation stores weekly target instead, keep a single source of truth and derive other values consistently.

## 11. Confirmed Daily Color Rule

For any category/metric with a confirmed target:

`Actual < DailyTarget` -> RED
`Actual >= DailyTarget` -> GREEN

Current beta examples:
- Non-KK content target = 3/day
- KK content target = 15/day
- Non-KK views target = 6,000/day
- KK views target = 15,000/day

Do not use arbitrary thresholds.

Backend/service should determine performance state when practical; View renders the visual class.

Existing CSS classes such as `.tgt-below` and `.tgt-above` may be reused.

## 12. Confirmed Date-Range Target Rule

Selected date range is inclusive.

`TargetPeriod = DailyTarget * SelectedCalendarDays`

Equivalent if weekly target is the source:
`TargetPeriod = (WeeklyTarget / 7) * SelectedCalendarDays`

Do NOT always multiply by 7.
Seven is only used to convert a weekly target to a daily target.

Example for 18–20 September (3 days):
- Non-KK content = 3 * 3 = 9
- Non-KK views = 6,000 * 3 = 18,000
- KK content = 15 * 3 = 45
- KK views = 15,000 * 3 = 45,000

This 3-day rule is confirmed by Manager and is also consistent with the supplied mockup behavior.

## 13. Target Achievement

Current mockup displays:
- Non-KK / Jumlah Konten
- Non-KK / Total Views
- Keranjang Kuning / Jumlah Konten
- Keranjang Kuning / Total Views

Columns:
- Jenis Konten
- Metrik
- Target Periode Ini
- Actual
- Selisih
- Status

Auto GMV Live currently has no target and should not be compared to a target.

## 14. Variance

`Variance = Actual - TargetPeriod`

Examples:
- target 9, actual 11 -> +2
- target 18,000, actual 15,000 -> -3,000

Use existing project formatting conventions.

## 15. Auto GMV Live

Auto GMV Live is TikTok auto-generated content used to support live-shopping boosting.

It has no management target currently.

It may appear as its own category in:
- daily table
- summary
- composition

Do not invent an Auto GMV target.

## 16. TOTAL Actual Rule

Separate control:
`Termasuk Auto GMV Live di kolom Total`

When enabled:
- Total Content = Non-KK + KK + Auto GMV Live
- Total Views = Non-KK Views + KK Views + Auto GMV Live Views

When disabled:
- Total Content = Non-KK + KK
- Total Views = Non-KK Views + KK Views

Column visibility is independent from TOTAL calculation.

## 17. TOTAL Target Rule

Current management target exists only for Non-KK and Keranjang Kuning.

Unless management later defines an Auto GMV target:
- Total target = Non-KK target + Keranjang Kuning target
- Auto GMV Live actual may be included in Total actual
- Auto GMV Live has no separate target

If a future requirement says an Auto-inclusive total must not be compared to an Auto-excluding target, revise the rule then. For current beta implementation, preserve the mockup/confirmed behavior and do not invent an Auto GMV target.

## 18. Main Daily Table

One row per calendar date in selected inclusive range.

Fixed:
- No
- Tanggal

TOTAL:
- Jumlah Konten
- Total Views

NON-KK:
- Jumlah Konten
- Total Views

KERANJANG KUNING:
- Jumlah Konten
- Total Views

AUTO GMV LIVE:
- Jumlah Konten
- Total Views

## 19. Date Range Filter

Support:
- custom StartDate/EndDate
- Hari Ini
- Kemarin
- 1 Minggu Terakhir
- 1 Bulan Terakhir
- 3 Bulan Terakhir

Display selected range and GMT+7.

Selected range is inclusive.

## 20. Zero-Content Dates

Keep calendar dates in the selected range even when there is no content, consistent with the current Daily Summary implementation.

Example: selecting 18–20 September produces rows for all three dates, including zero rows if needed.

## 21. Period Summary

Section `RINGKASAN`

Columns:
- Jenis Konten
- Avg Konten/Hari
- Avg Views/Hari
- Total Konten
- Total Views

Rows:
- Non-KK
- Keranjang Kuning
- Auto GMV Live

Calculations:
- Total Content = count of valid content records in selected period
- Total Views = sum of latest/current Views for relevant records
- Average Content/Day = Total Content / number of calendar days in selected range
- Average Views/Day = Total Views / number of calendar days in selected range

Zero-content days remain in the calendar-day denominator.

## 22. Composition Chart

Donut chart with:
- Non-KK
- Keranjang Kuning
- Auto GMV Live

Composition is based on content count, not views.

`CategorySharePercent = CategoryTotalContent / TotalContentAllCategories * 100`

Do not hardcode mockup percentages.

**FINAL DECISION (locked):** Composition ALWAYS includes all three content types
(Non-KK, Keranjang Kuning, Auto GMV Live). It is a data summary of the whole
account, independent of the table column selector, independent of the
`show*Columns` flags, and independent of the Include Auto GMV toggle
(which only affects the TOTAL columns of the table). Hiding a table column
group must never remove a Composition legend item or donut segment.

## 23. Column Selector

Button `Kolom` controls visibility of column groups:
- Non-KK (Jumlah Konten + Total Views)
- Keranjang Kuning (Jumlah Konten + Total Views)
- Auto GMV Live (Jumlah Konten + Total Views)

Visibility is presentation state, not aggregation filtering.

**Scope is the Daily Summary TABLE ONLY (FINAL DECISION):**
- Table: hiding a group hides its columns/group header cells.
- Ringkasan, Komposisi Jenis Konten, and Pencapaian vs Target are NOT
  affected by column visibility and always cover all content types.
- This is deliberately different from the Include Auto GMV toggle, which is
  a business rule that changes the TOTAL calculation (see section 15).

## 24. Data Source Mapping

### ContentLogs
Likely/confirmed source for:
- content records
- VideoPostTime
- archive state
- content classification/reference

### ContentMetrics
Source for current/latest accumulated Views.

### ContentTypes
Source for content classification if confirmed by existing schema.

### Targets
A persistent/manual target configuration source is needed because management changes target values.

### MasterPics
Potential source for manual PIC allocation; not required by current Daily Summary UI.

### TikTokShops
Potential account/shop scope dependency if existing architecture needs it.

### TikTokCredentials
Authentication/integration dependency only.

## 25. Target Storage / Migration

Current inspection found no existing Target table/entity/property.

Status: `MIGRATION POSSIBLY REQUIRED`

A migration is required only if management targets must be persisted in SQL Server and no existing source can store them.

Before creating a migration, confirm the target model. At minimum it may need concepts equivalent to:
- target category/content type
- daily content target
- target views per content
- effective period/version if target changes over time
- account/shop scope if needed
- PIC allocation if stored in the same system

Do not create a migration until this structure is confirmed.

## 26. Timezone

UI displays GMT+7.
VideoPostTime is the authoritative business date.

Existing code may use UTC internally. Before final production implementation, confirm/implement the existing project's timezone conversion behavior so date extraction does not move videos across calendar days.

Do not silently change global timestamp conventions.

## 27. Historical Metrics

ContentMetrics contains historical snapshots (CapturedAt), but Daily Summary uses latest/current accumulated Views per confirmed Manager rule.

Do not convert Daily Summary to an as-of-date/historical snapshot report unless that requirement changes.

## 28. Performance / Query Requirements

Prefer:
- AsNoTracking()
- index-friendly date filtering
- existing inclusive/end-exclusive date range pattern
- latest metric selection consistent with existing implementation
- server-side aggregation when practical

Avoid:
- loading entire tables into memory unnecessarily
- N+1 queries
- moving core aggregation/business rules into Razor/JavaScript

## 29. ViewModel Proposal

Preferred root:
`DailySummaryIndexViewModel`

Logical sections:
- Filter
- DailyRows
- SummaryItems
- CompositionItems
- TargetAchievementItems

Filter:
- StartDate
- EndDate
- IncludeArchived
- IncludeAutoGmvLiveInTotal
- ShowNonKkColumns
- ShowKeranjangKuningColumns
- ShowAutoGmvLiveColumns

Daily row:
- Date
- TotalContentCount
- TotalViews
- NonKkContentCount
- NonKkViews
- KeranjangKuningContentCount
- KeranjangKuningViews
- AutoGmvLiveContentCount
- AutoGmvLiveViews
- performance states if target support is enabled

Summary item:
- ContentTypeName
- AverageContentPerDay
- AverageViewsPerDay
- TotalContentCount
- TotalViews

Composition item:
- ContentTypeName
- Percentage

Target achievement:
- ContentTypeName
- MetricName
- TargetPeriodValue
- ActualValue
- VarianceValue
- Status

Reuse existing Daily Summary ViewModels whenever possible.

## 30. Architecture

Follow existing application architecture.

### Controller
- receive/bind filters
- validate input
- invoke service
- return ViewModel

Keep controller thin.

### Service
Responsible for:
- querying content
- latest metric selection
- archived filtering
- classification
- daily aggregation
- TOTAL aggregation
- summary
- averages
- composition
- targets
- target-period calculations
- variance/status

### View
Responsible for:
- rendering data
- UI interactions
- date picker
- column selector
- archived filter
- Include Auto GMV Live toggle

Do not put core business calculations in JavaScript/Razor.

## 31. UI Requirements

Follow the supplied mockup:
- dark theme consistent with KALE Content Ops
- grouped table headers
- category colors
- red/green performance states
- summary table
- donut chart
- target achievement table
- informational notes

Category colors:
- Non-KK = blue
- Keranjang Kuning = yellow/gold
- Auto GMV Live = purple

Performance:
- below target = red
- achieved = green

Do not redesign the page.

## 32. Out of Scope Unless Requested

Do not automatically add:
- search
- pagination
- export
- PIC filter inside Daily Summary
- Production Method filter
- drill-down
- unrelated metrics
- unrelated navigation

## 33. Current Beta Validation Scenario

Current management beta values:

Non-KK:
- 3 content/day
- 2,000 views/content
- 6,000 views/day
- 21 content/week
- 42,000 views/week

Keranjang Kuning:
- 15 content/day
- 1,000 views/content
- 15,000 views/day
- 105 content/week
- 105,000 views/week

For 3 days:
- Non-KK = 9 content, 18,000 views
- KK = 45 content, 45,000 views

For 7 days:
- Non-KK = 21 content, 42,000 views
- KK = 105 content, 105,000 views

These are test/reference values only and must never be hardcoded.

## 34. Remaining Open Questions

1. What is the default state of Include Archived?
2. RESOLVED (see sections 22 and 23): Composition always includes all three
   content types; column visibility only affects the table columns and never
   changes Ringkasan, Composition, or Pencapaian vs Target.
3. What exact SQL persistence model should store management targets?
4. Should target changes be effective-dated/versioned so historical reports preserve the target that was active at that time?
5. Should PIC allocation live in the target model or a separate configuration model?
6. How should TOTAL target/status be presented if Auto GMV Live actual is included but has no target?
7. What exact timezone conversion should be used before extracting the VideoPostTime date? (ground truth for video_post_time still pending)

## 35. Copilot Working Rule

Before modifying files:
1. Inspect the existing implementation.
2. Report findings.
3. List proposed files to change/create.
4. Explain whether migration is necessary.
5. Identify unresolved business ambiguities.
6. Only then implement after approval.

During implementation:
- reuse existing entities/services
- avoid duplicate models
- avoid unnecessary migrations
- never hardcode target values
- keep business logic outside Razor where practical
- keep target configuration separate from mockup sample data
