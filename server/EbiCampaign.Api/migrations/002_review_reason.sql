/* Phase 2 — reconciliation evidence for invoices that need manual review. */

IF COL_LENGTH(N'dbo.Invoices', N'ReviewReason') IS NULL
    ALTER TABLE dbo.Invoices ADD ReviewReason NVARCHAR(20) NULL;

IF COL_LENGTH(N'dbo.Invoices', N'ReviewEvidence') IS NULL
    ALTER TABLE dbo.Invoices ADD ReviewEvidence NVARCHAR(400) NULL;
