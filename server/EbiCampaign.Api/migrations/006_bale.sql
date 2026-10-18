IF OBJECT_ID(N'dbo.BaleRegistration', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.BaleRegistration(
        BaleUserId    BIGINT        NOT NULL PRIMARY KEY,
        Mobile        NVARCHAR(11)  NOT NULL,
        FirstName     NVARCHAR(100) NULL,
        ContactShared BIT           NOT NULL CONSTRAINT DF_BaleRegistration_Contact DEFAULT(0),
        RegisteredAt  DATETIME2(0)  NOT NULL,
        VerifiedAt    DATETIME2(0)  NULL,
        LastCheckAt   DATETIME2(0)  NULL,
        LastStatus    NVARCHAR(20)  NULL,
        LastEvidence  NVARCHAR(200) NULL
    );
END;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_BaleRegistration_Mobile'
               AND object_id = OBJECT_ID(N'dbo.BaleRegistration'))
    CREATE UNIQUE INDEX UX_BaleRegistration_Mobile ON dbo.BaleRegistration(Mobile);

IF OBJECT_ID(N'dbo.BaleMembershipCheck', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.BaleMembershipCheck(
        CheckId    BIGINT        IDENTITY(1,1) PRIMARY KEY,
        Mobile     NVARCHAR(11)  NOT NULL,
        BaleUserId BIGINT        NULL,
        Status     NVARCHAR(20)  NOT NULL,
        Evidence   NVARCHAR(200) NULL,
        CheckedAt  DATETIME2(0)  NOT NULL
    );
    CREATE INDEX IX_BaleMembershipCheck_Mobile ON dbo.BaleMembershipCheck(Mobile, CheckedAt DESC);
END;
