IF OBJECT_ID(N'[__EFMigrationsHistory]') IS NULL
BEGIN
    CREATE TABLE [__EFMigrationsHistory] (
        [MigrationId] nvarchar(150) NOT NULL,
        [ProductVersion] nvarchar(32) NOT NULL,
        CONSTRAINT [PK___EFMigrationsHistory] PRIMARY KEY ([MigrationId])
    );
END;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261013175703_Initial'
)
BEGIN
    CREATE TABLE [BuildLeads] (
        [Id] int NOT NULL IDENTITY,
        [Name] nvarchar(120) NOT NULL,
        [Phone] nvarchar(20) NOT NULL,
        [Email] nvarchar(160) NOT NULL,
        [Country] nvarchar(2) NOT NULL,
        [City] nvarchar(40) NOT NULL,
        [Area] int NOT NULL,
        [Level] nvarchar(20) NOT NULL,
        [Soil] nvarchar(20) NOT NULL,
        [Low] bigint NOT NULL,
        [High] bigint NOT NULL,
        [Currency] nvarchar(3) NOT NULL,
        [Plan] nvarchar(80) NOT NULL,
        [Notes] nvarchar(2000) NOT NULL,
        [Lang] nvarchar(5) NOT NULL,
        [Page] nvarchar(600) NOT NULL,
        [Status] nvarchar(20) NOT NULL,
        [CreatedAt] datetimeoffset NOT NULL,
        CONSTRAINT [PK_BuildLeads] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261013175703_Initial'
)
BEGIN
    CREATE TABLE [ChatMessages] (
        [Id] bigint NOT NULL IDENTITY,
        [SessionId] nvarchar(64) NOT NULL,
        [Role] nvarchar(12) NOT NULL,
        [Content] nvarchar(max) NOT NULL,
        [Lang] nvarchar(5) NOT NULL,
        [Page] nvarchar(300) NOT NULL,
        [CreatedAt] datetimeoffset NOT NULL,
        CONSTRAINT [PK_ChatMessages] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261013175703_Initial'
)
BEGIN
    CREATE TABLE [CostRates] (
        [Id] int NOT NULL IDENTITY,
        [Rates] nvarchar(max) NOT NULL,
        [Status] nvarchar(10) NOT NULL,
        [Updated] nvarchar(7) NOT NULL,
        [Token] nvarchar(32) NOT NULL,
        [Notes] nvarchar(1500) NOT NULL,
        [Sources] nvarchar(4000) NOT NULL,
        [CreatedAt] datetimeoffset NOT NULL,
        CONSTRAINT [PK_CostRates] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261013175703_Initial'
)
BEGIN
    CREATE TABLE [Designers] (
        [Id] int NOT NULL IDENTITY,
        [Email] nvarchar(160) NOT NULL,
        [Name] nvarchar(80) NOT NULL,
        [Skills] nvarchar(300) NOT NULL,
        [Countries] nvarchar(120) NOT NULL,
        [Note] nvarchar(1000) NOT NULL,
        [Status] nvarchar(20) NOT NULL,
        [CreatedAt] datetimeoffset NOT NULL,
        [UpdatedAt] datetimeoffset NOT NULL,
        CONSTRAINT [PK_Designers] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261013175703_Initial'
)
BEGIN
    CREATE TABLE [JobRuns] (
        [Key] nvarchar(60) NOT NULL,
        [RanAt] datetimeoffset NOT NULL,
        CONSTRAINT [PK_JobRuns] PRIMARY KEY ([Key])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261013175703_Initial'
)
BEGIN
    CREATE TABLE [Leads] (
        [Id] int NOT NULL IDENTITY,
        [ContactName] nvarchar(120) NOT NULL,
        [Phone] nvarchar(40) NOT NULL,
        [Email] nvarchar(160) NOT NULL,
        [City] nvarchar(120) NOT NULL,
        [Package] nvarchar(40) NOT NULL,
        [Area] nvarchar(40) NOT NULL,
        [Needs] nvarchar(max) NOT NULL,
        [PreferredContact] nvarchar(200) NOT NULL,
        [Channel] nvarchar(20) NOT NULL,
        [Lang] nvarchar(5) NOT NULL,
        [Page] nvarchar(300) NOT NULL,
        [Context] nvarchar(4000) NOT NULL,
        [Status] nvarchar(20) NOT NULL,
        [CreatedAt] datetimeoffset NOT NULL,
        CONSTRAINT [PK_Leads] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261013175703_Initial'
)
BEGIN
    CREATE TABLE [Listings] (
        [Id] nvarchar(10) NOT NULL,
        [Status] nvarchar(10) NOT NULL,
        [Title] nvarchar(100) NOT NULL,
        [City] nvarchar(60) NOT NULL,
        [District] nvarchar(60) NOT NULL,
        [Price] nvarchar(30) NOT NULL,
        [Phone] nvarchar(30) NOT NULL,
        [Email] nvarchar(160) NOT NULL,
        [Lang] nvarchar(5) NOT NULL,
        [Style] nvarchar(20) NOT NULL,
        [Area] float NOT NULL,
        [Plan] nvarchar(max) NOT NULL,
        [Copy] nvarchar(max) NOT NULL,
        [Views] int NOT NULL,
        [InvoiceId] nvarchar(64) NULL,
        [PaidAt] datetimeoffset NULL,
        [CreatedAt] datetimeoffset NOT NULL,
        CONSTRAINT [PK_Listings] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261013175703_Initial'
)
BEGIN
    CREATE TABLE [OptOuts] (
        [Id] int NOT NULL IDENTITY,
        [Email] nvarchar(160) NOT NULL,
        [Reason] nvarchar(20) NOT NULL,
        [CreatedAt] datetimeoffset NOT NULL,
        CONSTRAINT [PK_OptOuts] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261013175703_Initial'
)
BEGIN
    CREATE TABLE [Orders] (
        [Id] int NOT NULL IDENTITY,
        [Package] nvarchar(20) NOT NULL,
        [ContactName] nvarchar(120) NOT NULL,
        [Phone] nvarchar(40) NOT NULL,
        [Email] nvarchar(160) NOT NULL,
        [City] nvarchar(120) NOT NULL,
        [Area] float NOT NULL,
        [Style] nvarchar(20) NOT NULL,
        [Notes] nvarchar(3000) NOT NULL,
        [FileNames] nvarchar(2000) NOT NULL,
        [Summary] nvarchar(max) NULL,
        [Rooms] nvarchar(4000) NOT NULL,
        [PriceNote] nvarchar(200) NOT NULL,
        [AmountDue] decimal(10,2) NOT NULL,
        [Status] nvarchar(20) NOT NULL,
        [Lang] nvarchar(5) NOT NULL,
        [Page] nvarchar(300) NOT NULL,
        [ReturnUrl] nvarchar(300) NOT NULL,
        [InvoiceId] nvarchar(64) NULL,
        [PaidAt] datetimeoffset NULL,
        [Designer] nvarchar(160) NULL,
        [DeliveredAt] datetimeoffset NULL,
        [Rating] int NOT NULL,
        [Review] nvarchar(1000) NOT NULL,
        [ReviewAsked] bit NOT NULL,
        [NotifiedDesigners] bit NOT NULL,
        [CreatedAt] datetimeoffset NOT NULL,
        [UpdatedAt] datetimeoffset NOT NULL,
        CONSTRAINT [PK_Orders] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261013175703_Initial'
)
BEGIN
    CREATE TABLE [Projects] (
        [Id] int NOT NULL IDENTITY,
        [Pid] nvarchar(14) NOT NULL,
        [Owner] nvarchar(160) NOT NULL,
        [Title] nvarchar(80) NOT NULL,
        [Kind] nvarchar(20) NOT NULL,
        [Data] nvarchar(max) NOT NULL,
        [ShareId] nvarchar(16) NOT NULL,
        [Public] bit NOT NULL,
        [Lang] nvarchar(5) NOT NULL,
        [Nudged] bit NOT NULL,
        [CreatedAt] datetimeoffset NOT NULL,
        [UpdatedAt] datetimeoffset NOT NULL,
        CONSTRAINT [PK_Projects] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261013175703_Initial'
)
BEGIN
    CREATE TABLE [Purchases] (
        [Id] int NOT NULL IDENTITY,
        [WalletId] nvarchar(48) NOT NULL,
        [Pack] nvarchar(10) NOT NULL,
        [Renders] int NOT NULL,
        [Amount] decimal(10,2) NOT NULL,
        [Email] nvarchar(160) NOT NULL,
        [Lang] nvarchar(5) NOT NULL,
        [Status] nvarchar(20) NOT NULL,
        [InvoiceId] nvarchar(64) NULL,
        [PaidAt] datetimeoffset NULL,
        [CreatedAt] datetimeoffset NOT NULL,
        CONSTRAINT [PK_Purchases] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261013175703_Initial'
)
BEGIN
    CREATE TABLE [RenderLogs] (
        [Id] bigint NOT NULL IDENTITY,
        [Kind] nvarchar(10) NOT NULL,
        [SessionId] nvarchar(64) NOT NULL,
        [Ip] nvarchar(64) NOT NULL,
        [Style] nvarchar(20) NOT NULL,
        [View] nvarchar(20) NOT NULL,
        [Mode] nvarchar(10) NOT NULL,
        [Lang] nvarchar(5) NOT NULL,
        [Ok] bit NOT NULL,
        [CreatedAt] datetimeoffset NOT NULL,
        CONSTRAINT [PK_RenderLogs] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261013175703_Initial'
)
BEGIN
    CREATE TABLE [UsageCounters] (
        [Key] nvarchar(120) NOT NULL,
        [Day] date NOT NULL,
        [Count] int NOT NULL,
        CONSTRAINT [PK_UsageCounters] PRIMARY KEY ([Key], [Day])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261013175703_Initial'
)
BEGIN
    CREATE TABLE [Waitlist] (
        [Id] int NOT NULL IDENTITY,
        [Email] nvarchar(160) NOT NULL,
        [Plan] nvarchar(20) NOT NULL,
        [Role] nvarchar(40) NOT NULL,
        [Lang] nvarchar(5) NOT NULL,
        [Country] nvarchar(2) NOT NULL,
        [CreatedAt] datetimeoffset NOT NULL,
        [UpdatedAt] datetimeoffset NOT NULL,
        CONSTRAINT [PK_Waitlist] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261013175703_Initial'
)
BEGIN
    CREATE TABLE [Wallets] (
        [Id] nvarchar(48) NOT NULL,
        [Credits] int NOT NULL,
        [Purchased] int NOT NULL,
        [Used] int NOT NULL,
        [Email] nvarchar(160) NOT NULL,
        [CreatedAt] datetimeoffset NOT NULL,
        [UpdatedAt] datetimeoffset NOT NULL,
        CONSTRAINT [PK_Wallets] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261013175703_Initial'
)
BEGIN
    CREATE TABLE [OrderComments] (
        [Id] bigint NOT NULL IDENTITY,
        [OrderId] int NOT NULL,
        [Author] nvarchar(160) NOT NULL,
        [Role] nvarchar(20) NOT NULL,
        [Text] nvarchar(2000) NOT NULL,
        [CreatedAt] datetimeoffset NOT NULL,
        CONSTRAINT [PK_OrderComments] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_OrderComments_Orders_OrderId] FOREIGN KEY ([OrderId]) REFERENCES [Orders] ([Id]) ON DELETE CASCADE
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261013175703_Initial'
)
BEGIN
    CREATE TABLE [OrderFiles] (
        [Id] int NOT NULL IDENTITY,
        [OrderId] int NOT NULL,
        [Name] nvarchar(120) NOT NULL,
        [Mime] nvarchar(80) NOT NULL,
        [Role] nvarchar(20) NOT NULL,
        [Field] nvarchar(40) NOT NULL,
        [Size] bigint NOT NULL,
        [Path] nvarchar(300) NOT NULL,
        [CreatedAt] datetimeoffset NOT NULL,
        CONSTRAINT [PK_OrderFiles] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_OrderFiles_Orders_OrderId] FOREIGN KEY ([OrderId]) REFERENCES [Orders] ([Id]) ON DELETE CASCADE
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261013175703_Initial'
)
BEGIN
    CREATE INDEX [IX_ChatMessages_SessionId_Id] ON [ChatMessages] ([SessionId], [Id]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261013175703_Initial'
)
BEGIN
    CREATE INDEX [IX_CostRates_Status_Id] ON [CostRates] ([Status], [Id]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261013175703_Initial'
)
BEGIN
    CREATE UNIQUE INDEX [IX_Designers_Email] ON [Designers] ([Email]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261013175703_Initial'
)
BEGIN
    CREATE INDEX [IX_Leads_CreatedAt] ON [Leads] ([CreatedAt]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261013175703_Initial'
)
BEGIN
    CREATE UNIQUE INDEX [IX_OptOuts_Email] ON [OptOuts] ([Email]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261013175703_Initial'
)
BEGIN
    CREATE INDEX [IX_OrderComments_OrderId_Id] ON [OrderComments] ([OrderId], [Id]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261013175703_Initial'
)
BEGIN
    CREATE INDEX [IX_OrderFiles_OrderId] ON [OrderFiles] ([OrderId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261013175703_Initial'
)
BEGIN
    CREATE INDEX [IX_Orders_Designer] ON [Orders] ([Designer]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261013175703_Initial'
)
BEGIN
    CREATE INDEX [IX_Orders_Email] ON [Orders] ([Email]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261013175703_Initial'
)
BEGIN
    CREATE INDEX [IX_Orders_Status_CreatedAt] ON [Orders] ([Status], [CreatedAt]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261013175703_Initial'
)
BEGIN
    CREATE INDEX [IX_Projects_Owner] ON [Projects] ([Owner]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261013175703_Initial'
)
BEGIN
    CREATE UNIQUE INDEX [IX_Projects_Pid] ON [Projects] ([Pid]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261013175703_Initial'
)
BEGIN
    CREATE INDEX [IX_Projects_Public_UpdatedAt] ON [Projects] ([Public], [UpdatedAt]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261013175703_Initial'
)
BEGIN
    CREATE UNIQUE INDEX [IX_Projects_ShareId] ON [Projects] ([ShareId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261013175703_Initial'
)
BEGIN
    CREATE INDEX [IX_Purchases_WalletId] ON [Purchases] ([WalletId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261013175703_Initial'
)
BEGIN
    CREATE INDEX [IX_RenderLogs_CreatedAt] ON [RenderLogs] ([CreatedAt]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261013175703_Initial'
)
BEGIN
    CREATE UNIQUE INDEX [IX_Waitlist_Email] ON [Waitlist] ([Email]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261013175703_Initial'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261013175703_Initial', N'10.0.12');
END;

COMMIT;
GO

