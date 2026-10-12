SET NOCOUNT ON;
USE Holoo1;
GO
PRINT '=== Database collation ===';
SELECT DATABASEPROPERTYEX('Holoo1','Collation') AS Collation, DATABASEPROPERTYEX('Holoo1','CompatibilityLevel') AS CompatLevel;
GO
PRINT '=== Users (cashiers) ===';
SELECT c.name FROM sys.columns c WHERE c.object_id = OBJECT_ID('dbo.USERDB') ORDER BY c.column_id;
GO
SELECT TOP 20 * FROM dbo.USERDB;
GO
PRINT '=== Check_Tasvieh in archive ===';
SELECT Check_Tasvieh, COUNT(*) AS Cnt FROM dbo.Rs_Facture GROUP BY Check_Tasvieh;
GO
PRINT '=== Res_Factur_Log summary ===';
SELECT COUNT(*) AS Rows_, MIN(Fac_Date) AS MinD, MAX(Fac_Date) AS MaxD, MIN(Fac_Time) AS MinT, MAX(Fac_Time) AS MaxT,
       SUM(CASE WHEN EditFlag <> 0 THEN 1 ELSE 0 END) AS Edited
FROM dbo.Res_Factur_Log;
GO
SELECT TOP 5 * FROM dbo.Res_Factur_Log ORDER BY Id DESC;
GO
PRINT '=== DailyRep rows for the current day (should be absent until close) ===';
SELECT COUNT(*) AS RowsForCurrentDay FROM dbo.Res_DailyRep WHERE F_Date = (SELECT MAX(F_Date) FROM dbo.Res_Facture);
GO
PRINT '=== Volume per day (archive) for last 30 days ===';
SELECT AVG(CAST(c AS float)) AS AvgInvoicesPerDay, MIN(c) AS MinPerDay, MAX(c) AS MaxPerDay
FROM (SELECT F_Date, COUNT(*) AS c FROM dbo.Rs_Facture WHERE F_Date >= DATEADD(day,-30,(SELECT MAX(F_Date) FROM dbo.Rs_Facture)) GROUP BY F_Date) t;
GO
