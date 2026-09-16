IF OBJECT_ID(N'[__EFMigrationsHistory]') IS NULL
BEGIN
    CREATE TABLE [__EFMigrationsHistory] (
        [MigrationId] nvarchar(150) NOT NULL,
        [ProductVersion] nvarchar(32) NOT NULL,
        CONSTRAINT [PK___EFMigrationsHistory] PRIMARY KEY ([MigrationId])
    );
END;
 

BEGIN TRANSACTION;
CREATE TABLE [ContentTypes] (
    [Id] int NOT NULL IDENTITY,
    [Code] nvarchar(50) NOT NULL,
    [Name] nvarchar(100) NOT NULL,
    [Color] nvarchar(30) NULL,
    [IsActive] bit NOT NULL,
    [CreatedAt] datetime2 NOT NULL,
    [UpdatedAt] datetime2 NOT NULL,
    CONSTRAINT [PK_ContentTypes] PRIMARY KEY ([Id])
);

CREATE TABLE [MasterPics] (
    [Id] int NOT NULL IDENTITY,
    [Name] nvarchar(100) NOT NULL,
    [IsActive] bit NOT NULL,
    [CreatedAt] datetime2 NOT NULL,
    [UpdatedAt] datetime2 NOT NULL,
    CONSTRAINT [PK_MasterPics] PRIMARY KEY ([Id])
);

CREATE TABLE [ProductionMethods] (
    [Id] int NOT NULL IDENTITY,
    [Code] nvarchar(50) NOT NULL,
    [Name] nvarchar(100) NOT NULL,
    [IsActive] bit NOT NULL,
    CONSTRAINT [PK_ProductionMethods] PRIMARY KEY ([Id])
);

CREATE TABLE [ContentLogs] (
    [Id] bigint NOT NULL IDENTITY,
    [VideoId] nvarchar(100) NOT NULL,
    [VideoPostTime] datetime2 NULL,
    [Title] nvarchar(500) NULL,
    [Username] nvarchar(200) NULL,
    [Duration] int NULL,
    [VideoUrl] nvarchar(1000) NULL,
    [CreatorOpenId] nvarchar(200) NULL,
    [CreatorUsername] nvarchar(200) NULL,
    [CreatorNickname] nvarchar(200) NULL,
    [AuthorType] nvarchar(50) NULL,
    [ContentTypeId] int NULL,
    [ProductionMethodId] int NULL,
    [PicId] int NULL,
    [CreatedAt] datetime2 NOT NULL,
    [UpdatedAt] datetime2 NOT NULL,
    CONSTRAINT [PK_ContentLogs] PRIMARY KEY ([Id]),
    CONSTRAINT [FK_ContentLogs_ContentTypes_ContentTypeId] FOREIGN KEY ([ContentTypeId]) REFERENCES [ContentTypes] ([Id]) ON DELETE SET NULL,
    CONSTRAINT [FK_ContentLogs_MasterPics_PicId] FOREIGN KEY ([PicId]) REFERENCES [MasterPics] ([Id]) ON DELETE SET NULL,
    CONSTRAINT [FK_ContentLogs_ProductionMethods_ProductionMethodId] FOREIGN KEY ([ProductionMethodId]) REFERENCES [ProductionMethods] ([Id]) ON DELETE SET NULL
);

CREATE TABLE [ContentMetrics] (
    [Id] bigint NOT NULL IDENTITY,
    [ContentLogId] bigint NOT NULL,
    [Views] bigint NULL,
    [Reach] bigint NULL,
    [Likes] bigint NULL,
    [Comments] bigint NULL,
    [Shares] bigint NULL,
    [NewFollowers] bigint NULL,
    [AverageWatch] decimal(18,2) NULL,
    [FullWatchRate] decimal(18,2) NULL,
    [DemographicsJson] nvarchar(max) NULL,
    [MetricStartDate] datetime2 NULL,
    [MetricEndDate] datetime2 NULL,
    [CapturedAt] datetime2 NOT NULL,
    CONSTRAINT [PK_ContentMetrics] PRIMARY KEY ([Id]),
    CONSTRAINT [FK_ContentMetrics_ContentLogs_ContentLogId] FOREIGN KEY ([ContentLogId]) REFERENCES [ContentLogs] ([Id]) ON DELETE CASCADE
);

IF EXISTS (SELECT * FROM [sys].[identity_columns] WHERE [name] IN (N'Id', N'Code', N'Color', N'CreatedAt', N'IsActive', N'Name', N'UpdatedAt') AND [object_id] = OBJECT_ID(N'[ContentTypes]'))
    SET IDENTITY_INSERT [ContentTypes] ON;
INSERT INTO [ContentTypes] ([Id], [Code], [Color], [CreatedAt], [IsActive], [Name], [UpdatedAt])
VALUES (1, N'KK', N'yellow', '2026-01-01T00:00:00.0000000', CAST(1 AS bit), N'Keranjang Kuning', '2026-01-01T00:00:00.0000000'),
(2, N'NON_KK', N'blue', '2026-01-01T00:00:00.0000000', CAST(1 AS bit), N'Non-KK', '2026-01-01T00:00:00.0000000'),
(3, N'AUTO_GMV_LIVE', N'purple', '2026-01-01T00:00:00.0000000', CAST(1 AS bit), N'Auto GMV Live', '2026-01-01T00:00:00.0000000');
IF EXISTS (SELECT * FROM [sys].[identity_columns] WHERE [name] IN (N'Id', N'Code', N'Color', N'CreatedAt', N'IsActive', N'Name', N'UpdatedAt') AND [object_id] = OBJECT_ID(N'[ContentTypes]'))
    SET IDENTITY_INSERT [ContentTypes] OFF;

IF EXISTS (SELECT * FROM [sys].[identity_columns] WHERE [name] IN (N'Id', N'Code', N'IsActive', N'Name') AND [object_id] = OBJECT_ID(N'[ProductionMethods]'))
    SET IDENTITY_INSERT [ProductionMethods] ON;
INSERT INTO [ProductionMethods] ([Id], [Code], [IsActive], [Name])
VALUES (1, N'SELF_PRODUCE', CAST(1 AS bit), N'Self Produce'),
(2, N'AI_PRODUCE', CAST(1 AS bit), N'AI Produce');
IF EXISTS (SELECT * FROM [sys].[identity_columns] WHERE [name] IN (N'Id', N'Code', N'IsActive', N'Name') AND [object_id] = OBJECT_ID(N'[ProductionMethods]'))
    SET IDENTITY_INSERT [ProductionMethods] OFF;

CREATE INDEX [IX_ContentLogs_ContentTypeId] ON [ContentLogs] ([ContentTypeId]);

CREATE INDEX [IX_ContentLogs_PicId] ON [ContentLogs] ([PicId]);

CREATE INDEX [IX_ContentLogs_ProductionMethodId] ON [ContentLogs] ([ProductionMethodId]);

CREATE UNIQUE INDEX [IX_ContentLogs_VideoId] ON [ContentLogs] ([VideoId]);

CREATE INDEX [IX_ContentMetrics_ContentLogId] ON [ContentMetrics] ([ContentLogId]);

INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
VALUES (N'20260908083013_InitialCreate', N'10.0.11');

COMMIT;
 

BEGIN TRANSACTION;
DROP INDEX [IX_ContentLogs_VideoId] ON [ContentLogs];

ALTER TABLE [ContentLogs] ADD [TikTokShopId] bigint NULL;

CREATE TABLE [TikTokCredentials] (
    [Id] bigint NOT NULL IDENTITY,
    [AppKey] nvarchar(200) NULL,
    [EncryptedAccessToken] nvarchar(2000) NULL,
    [EncryptedRefreshToken] nvarchar(2000) NULL,
    [ExpiresAt] datetime2 NULL,
    [RefreshExpiresAt] datetime2 NULL,
    [OpenId] nvarchar(200) NULL,
    [SellerName] nvarchar(500) NULL,
    [Region] nvarchar(50) NULL,
    [CreatedAt] datetime2 NOT NULL,
    [UpdatedAt] datetime2 NOT NULL,
    CONSTRAINT [PK_TikTokCredentials] PRIMARY KEY ([Id])
);

CREATE TABLE [TikTokShops] (
    [Id] bigint NOT NULL IDENTITY,
    [TikTokAccountId] nvarchar(200) NULL,
    [ShopCipher] nvarchar(500) NULL,
    [ShopId] nvarchar(200) NULL,
    [ShopCode] nvarchar(200) NULL,
    [ShopName] nvarchar(500) NULL,
    [Region] nvarchar(50) NULL,
    [SellerType] nvarchar(100) NULL,
    [CreatedAt] datetime2 NOT NULL,
    [UpdatedAt] datetime2 NOT NULL,
    CONSTRAINT [PK_TikTokShops] PRIMARY KEY ([Id])
);

CREATE UNIQUE INDEX [IX_ContentLogs_TikTokShopId_VideoId] ON [ContentLogs] ([TikTokShopId], [VideoId]) WHERE [TikTokShopId] IS NOT NULL;

ALTER TABLE [ContentLogs] ADD CONSTRAINT [FK_ContentLogs_TikTokShops_TikTokShopId] FOREIGN KEY ([TikTokShopId]) REFERENCES [TikTokShops] ([Id]);

INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
VALUES (N'20260909064354_AddTikTokContentEntities', N'10.0.11');

COMMIT;
 

BEGIN TRANSACTION;
DECLARE @var nvarchar(max);
SELECT @var = QUOTENAME([d].[name])
FROM [sys].[default_constraints] [d]
INNER JOIN [sys].[columns] [c] ON [d].[parent_column_id] = [c].[column_id] AND [d].[parent_object_id] = [c].[object_id]
WHERE ([d].[parent_object_id] = OBJECT_ID(N'[ContentLogs]') AND [c].[name] = N'Username');
IF @var IS NOT NULL EXEC(N'ALTER TABLE [ContentLogs] DROP CONSTRAINT ' + @var + ';');
ALTER TABLE [ContentLogs] ALTER COLUMN [Username] nvarchar(500) NULL;

INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
VALUES (N'20260910031743_IncreaseTitleMaxLengthTo500', N'10.0.11');

COMMIT;
 

BEGIN TRANSACTION;
DECLARE @var1 nvarchar(max);
SELECT @var1 = QUOTENAME([d].[name])
FROM [sys].[default_constraints] [d]
INNER JOIN [sys].[columns] [c] ON [d].[parent_column_id] = [c].[column_id] AND [d].[parent_object_id] = [c].[object_id]
WHERE ([d].[parent_object_id] = OBJECT_ID(N'[ContentLogs]') AND [c].[name] = N'Username');
IF @var1 IS NOT NULL EXEC(N'ALTER TABLE [ContentLogs] DROP CONSTRAINT ' + @var1 + ';');
ALTER TABLE [ContentLogs] ALTER COLUMN [Username] nvarchar(max) NULL;

DECLARE @var2 nvarchar(max);
SELECT @var2 = QUOTENAME([d].[name])
FROM [sys].[default_constraints] [d]
INNER JOIN [sys].[columns] [c] ON [d].[parent_column_id] = [c].[column_id] AND [d].[parent_object_id] = [c].[object_id]
WHERE ([d].[parent_object_id] = OBJECT_ID(N'[ContentLogs]') AND [c].[name] = N'Title');
IF @var2 IS NOT NULL EXEC(N'ALTER TABLE [ContentLogs] DROP CONSTRAINT ' + @var2 + ';');
ALTER TABLE [ContentLogs] ALTER COLUMN [Title] nvarchar(max) NULL;

INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
VALUES (N'20260910033112_ChangeContentLogTitleToMax', N'10.0.11');

COMMIT;
 

BEGIN TRANSACTION;
CREATE INDEX [IX_ContentLogs_VideoPostTime_Id] ON [ContentLogs] ([VideoPostTime], [Id]);

INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
VALUES (N'20260910035625_AddContentLogVideoPostTimeIndex', N'10.0.11');

COMMIT;
 

BEGIN TRANSACTION;
ALTER TABLE [ContentLogs] ADD [HasCommerce] bit NULL;

ALTER TABLE [ContentLogs] ADD [IsArchived] bit NULL;

INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
VALUES (N'20260914023006_AddContentLogFlags', N'10.0.11');

COMMIT;
 

