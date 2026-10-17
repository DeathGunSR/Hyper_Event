/* Phase 3 — public campaign surface: public entries hash, publication consent and winner image.
   قواعد: نتیجه تا زمانی که مدیر انتشار را تأیید نکند در خروجی عمومی دیده نمی‌شود؛
   تصویر برنده فقط با تأیید جداگانه منتشر می‌شود. */

IF COL_LENGTH('dbo.LotteryRuns', 'PublicEntriesHash') IS NULL
    ALTER TABLE dbo.LotteryRuns ADD PublicEntriesHash CHAR(64) NULL;

IF COL_LENGTH('dbo.LotteryRuns', 'PublishApproved') IS NULL
    ALTER TABLE dbo.LotteryRuns ADD PublishApproved BIT NOT NULL
        CONSTRAINT DF_LotteryRuns_PublishApproved DEFAULT(0);

IF COL_LENGTH('dbo.LotteryRuns', 'PublishApprovedBy') IS NULL
    ALTER TABLE dbo.LotteryRuns ADD PublishApprovedBy NVARCHAR(100) NULL;

IF COL_LENGTH('dbo.LotteryRuns', 'PublishApprovedAt') IS NULL
    ALTER TABLE dbo.LotteryRuns ADD PublishApprovedAt DATETIME2(0) NULL;

IF COL_LENGTH('dbo.LotteryRuns', 'PublishedDisplayName') IS NULL
    ALTER TABLE dbo.LotteryRuns ADD PublishedDisplayName NVARCHAR(60) NULL;

IF COL_LENGTH('dbo.LotteryRuns', 'WinnerImageUrl') IS NULL
    ALTER TABLE dbo.LotteryRuns ADD WinnerImageUrl NVARCHAR(400) NULL;

IF COL_LENGTH('dbo.LotteryRuns', 'WinnerImageApproved') IS NULL
    ALTER TABLE dbo.LotteryRuns ADD WinnerImageApproved BIT NOT NULL
        CONSTRAINT DF_LotteryRuns_ImageApproved DEFAULT(0);

IF COL_LENGTH('dbo.LotteryRuns', 'WinnerImageUpdatedAt') IS NULL
    ALTER TABLE dbo.LotteryRuns ADD WinnerImageUpdatedAt DATETIME2(0) NULL;

/* فهرست برندگان منتشرشده با یک ایندکس پوشش می‌شود. */
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_LotteryRuns_Published' AND object_id = OBJECT_ID('dbo.LotteryRuns'))
    CREATE INDEX IX_LotteryRuns_Published ON dbo.LotteryRuns(PublishApproved, Status, RunId DESC);