/* Phase 1 — campaign core schema. Source tables of Holoo are never touched. */

IF OBJECT_ID(N'dbo.SchemaVersions', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.SchemaVersions(
        Version      INT           NOT NULL PRIMARY KEY,
        AppliedAt    DATETIME2(0)  NOT NULL
    );
END;

IF OBJECT_ID(N'dbo.Rounds', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.Rounds(
        RoundId      INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
        RoundNo      INT               NOT NULL,
        Status       VARCHAR(20)       NOT NULL,
        Target       INT               NOT NULL,
        [Count]      INT               NOT NULL CONSTRAINT DF_Rounds_Count DEFAULT(0),
        StartedAt    DATETIME2(0)      NOT NULL,
        CompletedAt  DATETIME2(0)      NULL,
        ClosedAt     DATETIME2(0)      NULL,
        OpenedBy     NVARCHAR(100)     NOT NULL,
        CONSTRAINT UQ_Rounds_No UNIQUE(RoundNo),
        CONSTRAINT CK_Rounds_Status CHECK (Status IN ('Collecting','Completed','Closed')),
        CONSTRAINT CK_Rounds_Count CHECK ([Count] >= 0 AND [Count] <= Target)
    );

    /* فقط یک دورهٔ در حال جمع‌آوری هم‌زمان مجاز است */
    CREATE UNIQUE INDEX UX_Rounds_Open ON dbo.Rounds(Status) WHERE Status = 'Collecting';
END;

IF OBJECT_ID(N'dbo.Invoices', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.Invoices(
        InvoiceId     BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
        SourceId      NVARCHAR(50)  NOT NULL,
        FCode         BIGINT        NOT NULL,
        FCodeC        BIGINT        NULL,
        FType         TINYINT       NOT NULL,
        FDate         DATE          NOT NULL,
        Jalali        NVARCHAR(10)  NULL,
        FTime         NVARCHAR(10)  NULL,
        UserCode      INT           NOT NULL,
        Cashier       NVARCHAR(100) NULL,
        Gross         DECIMAL(18,2) NOT NULL,
        Discount      DECIMAL(18,2) NOT NULL,
        Net           DECIMAL(18,2) NOT NULL,
        CustomerCode  NVARCHAR(20)  NULL,
        Mobile        NVARCHAR(20)  NULL,
        Points        INT           NOT NULL CONSTRAINT DF_Invoices_Points DEFAULT(0),
        Status        VARCHAR(20)   NOT NULL,
        RoundId       INT           NULL,
        AgentId       NVARCHAR(50)  NULL,
        ReceivedAt    DATETIME2(0)  NOT NULL,
        VoidedAt      DATETIME2(0)  NULL,
        VoidedBy      NVARCHAR(100) NULL,
        VoidReason    NVARCHAR(400) NULL,
        CONSTRAINT UQ_Invoices_Source_FCode UNIQUE(SourceId, FCode),
        CONSTRAINT FK_Invoices_Round FOREIGN KEY(RoundId) REFERENCES dbo.Rounds(RoundId),
        CONSTRAINT CK_Invoices_Status CHECK (Status IN ('Valid','Held','NeedsReview','OutOfScope','Voided')),
        CONSTRAINT CK_Invoices_Points CHECK (Points >= 0)
    );

    CREATE INDEX IX_Invoices_Round  ON dbo.Invoices(RoundId, Status);
    CREATE INDEX IX_Invoices_Status ON dbo.Invoices(Status);
    CREATE INDEX IX_Invoices_Date   ON dbo.Invoices(FDate);
END;

IF OBJECT_ID(N'dbo.IngestRequests', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.IngestRequests(
        RequestId     NVARCHAR(64)  NOT NULL PRIMARY KEY,
        AgentId       NVARCHAR(50)  NOT NULL,
        BodyHash      CHAR(64)      NOT NULL,
        StatusCode    INT           NOT NULL,
        ResponseJson  NVARCHAR(MAX) NOT NULL,
        CreatedAt     DATETIME2(0)  NOT NULL
    );
END;

IF OBJECT_ID(N'dbo.AuditLog', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.AuditLog(
        Id        BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
        At        DATETIME2(0)  NOT NULL,
        Actor     NVARCHAR(100) NOT NULL,
        Action    NVARCHAR(50)  NOT NULL,
        Entity    NVARCHAR(50)  NOT NULL,
        EntityId  NVARCHAR(50)  NULL,
        Details   NVARCHAR(MAX) NULL
    );

    CREATE INDEX IX_Audit_At ON dbo.AuditLog(At DESC);
END;
