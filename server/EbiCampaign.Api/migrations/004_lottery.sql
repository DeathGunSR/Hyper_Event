/* Phase 2 — lottery engine: committed entry snapshot, commitment, audit and result.
   قواعد: هر دوره یک برنده، ورودی‌ها پس از تثبیت تغییرناپذیر، Seed فقط پس از انتخاب افشا می‌شود. */

IF OBJECT_ID(N'dbo.LotteryRuns', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.LotteryRuns(
        RunId              INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
        RoundId            INT               NOT NULL,
        Status             VARCHAR(20)       NOT NULL,
        Algorithm          NVARCHAR(60)      NOT NULL,
        EntriesHash        CHAR(64)          NOT NULL,
        EntryCount         INT               NOT NULL,
        EligibleCount      INT               NOT NULL,
        TotalWeight        BIGINT            NOT NULL,
        CommitmentHash     CHAR(64)          NOT NULL,
        SeedHex            CHAR(64)          NULL,
        AcknowledgedReviews INT              NOT NULL CONSTRAINT DF_LotteryRuns_Ack DEFAULT(0),
        WinnerInvoiceId    BIGINT            NULL,
        WinnerFCode        BIGINT            NULL,
        WinnerEntryNo      INT               NULL,
        WinnerCustomerCode NVARCHAR(20)      NULL,
        WinnerMobile       NVARCHAR(20)      NULL,
        WinnerPoints       INT               NULL,
        WinnerDrawValue    BIGINT            NULL,
        WinnerTotalWeight  BIGINT            NULL,
        ReviewReason       NVARCHAR(200)     NULL,
        ResultsQuestioned  BIT               NOT NULL CONSTRAINT DF_LotteryRuns_Questioned DEFAULT(0),
        PreparedBy         NVARCHAR(100)     NOT NULL,
        PreparedAt         DATETIME2(0)      NOT NULL,
        SelectedAt         DATETIME2(0)      NULL,
        SelectedBy         NVARCHAR(100)     NULL,
        FinalizedBy        NVARCHAR(100)     NULL,
        FinalizedAt        DATETIME2(0)      NULL,
        CONSTRAINT FK_LotteryRuns_Round FOREIGN KEY(RoundId) REFERENCES dbo.Rounds(RoundId),
        CONSTRAINT CK_LotteryRuns_Status CHECK (Status IN ('Committed','Selected','Finalized','RequiresReview','Aborted'))
    );

    /* برای هر دوره فقط یک فراگرفت فعال؛ تلاش‌های لغوشده برای حسابرسی باقی می‌مانند. */
    CREATE UNIQUE INDEX UX_LotteryRuns_ActiveRound ON dbo.LotteryRuns(RoundId) WHERE Status <> 'Aborted';
END;

IF OBJECT_ID(N'dbo.LotteryEntries', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.LotteryEntries(
        RunId         INT           NOT NULL,
        EntryNo       INT           NOT NULL,
        InvoiceId     BIGINT        NOT NULL,
        FCode         BIGINT        NOT NULL,
        FDate         DATE          NOT NULL,
        CustomerCode  NVARCHAR(20)  NULL,
        Mobile        NVARCHAR(20)  NULL,
        Points        INT           NOT NULL,
        Weight        BIGINT        NOT NULL,
        Gross         DECIMAL(18,2) NOT NULL,
        Eligibility   VARCHAR(20)   NOT NULL,
        BaleStatus    VARCHAR(20)   NULL,
        BaleEvidence  NVARCHAR(200) NULL,
        BaleCheckedAt DATETIME2(0)  NULL,
        CONSTRAINT PK_LotteryEntries PRIMARY KEY(RunId, EntryNo),
        CONSTRAINT UQ_LotteryEntries_Invoice UNIQUE(RunId, InvoiceId),
        CONSTRAINT FK_LotteryEntries_Run FOREIGN KEY(RunId) REFERENCES dbo.LotteryRuns(RunId) ON DELETE CASCADE,
        CONSTRAINT CK_LotteryEntries_Eligibility CHECK
            (Eligibility IN ('eligible','zero_weight','no_mobile','bale_not_member','bale_unknown'))
    );
END;

/* تغییرناپذیری Snapshot: هیچ UPDATE روی ورودی‌های تثبیت‌شده مجاز نیست. */
IF OBJECT_ID(N'dbo.TR_LotteryEntries_NoUpdate', N'TR') IS NULL
    EXEC(N'CREATE TRIGGER dbo.TR_LotteryEntries_NoUpdate ON dbo.LotteryEntries AFTER UPDATE AS
BEGIN
    THROW 51004, N''ورودی‌های قرعه‌کشی پس از تثبیت تغییرناپذیرند.'', 1;
END');

/* حذف دستی ورودی‌های یک فراگرفت موجود مجاز نیست؛ پاک‌سازی فقط با حذف خود فراگرفت انجام می‌شود. */
IF OBJECT_ID(N'dbo.TR_LotteryEntries_NoDelete', N'TR') IS NULL
    EXEC(N'CREATE TRIGGER dbo.TR_LotteryEntries_NoDelete ON dbo.LotteryEntries AFTER DELETE AS
BEGIN
    IF EXISTS (SELECT 1 FROM deleted d INNER JOIN dbo.LotteryRuns r ON r.RunId = d.RunId)
        THROW 51005, N''حذف ورودی‌های یک فراگرفت موجود مجاز نیست.'', 1;
END');