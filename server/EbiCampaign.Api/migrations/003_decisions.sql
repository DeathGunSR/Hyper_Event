/* Phase 2 — append-only decision history for invoices that needed manual review.
   نقش این جدول: ثبت کامل تصمیم مدیر (اعتبار/ابطال)، مبنا، هویت، زمان و اثر شمارشی.
   تنها INSERT مجاز است؛ هیچ مسیری رکورد را بهروزرسانی یا حذف نمیکند. */

IF OBJECT_ID(N'dbo.InvoiceDecisions', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.InvoiceDecisions(
        DecisionId     BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
        InvoiceId      BIGINT        NOT NULL,
        FCode          BIGINT        NOT NULL,
        Verdict        VARCHAR(10)   NOT NULL,   /* valid | void */
        Basis          VARCHAR(20)   NOT NULL,   /* evidence | manager | forced */
        PrevStatus     VARCHAR(20)   NOT NULL,
        NewStatus      VARCHAR(20)   NOT NULL,
        PrevRoundId    INT           NULL,
        NewRoundId     INT           NULL,
        CountBefore    INT           NULL,
        CountAfter     INT           NULL,
        CountEffected  BIT           NOT NULL CONSTRAINT DF_Decisions_CountEffected DEFAULT(0),
        RoundFinalized BIT           NOT NULL CONSTRAINT DF_Decisions_RoundFinalized DEFAULT(0),
        Forced         BIT           NOT NULL CONSTRAINT DF_Decisions_Forced DEFAULT(0),
        Actor          NVARCHAR(100) NOT NULL,
        Reason         NVARCHAR(400) NULL,
        Evidence       NVARCHAR(400) NULL,
        DecisionKey    NVARCHAR(80)  NULL,
        DecidedAt      DATETIME2(0)  NOT NULL,
        CONSTRAINT FK_Decisions_Invoice FOREIGN KEY(InvoiceId) REFERENCES dbo.Invoices(InvoiceId),
        CONSTRAINT CK_Decisions_Verdict CHECK (Verdict IN ('valid','void')),
        CONSTRAINT CK_Decisions_Basis   CHECK (Basis IN ('evidence','manager','forced'))
    );

    CREATE INDEX IX_Decisions_Invoice ON dbo.InvoiceDecisions(InvoiceId, DecisionId);
    CREATE UNIQUE INDEX UX_Decisions_Key ON dbo.InvoiceDecisions(DecisionKey) WHERE DecisionKey IS NOT NULL;
END;