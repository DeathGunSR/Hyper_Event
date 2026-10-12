SET NOCOUNT ON;
USE Holoo1;
GO
PRINT '=== MSetup key flags ===';
SELECT Typefac, Showdate, Fac_Kharid, Fac_Pish, Fac_Forush, Fac_Amani, Fac_RetKharid, Fac_RetForush, Fac_Z,
       Negative_Fact, Type_Art, TypeArtName, Firstrun, [System]
FROM dbo.MSetup;
GO
PRINT '=== Rs_Facture: invoices per day (last 10 days) ===';
SELECT TOP 10 F_Date, COUNT(*) AS Invoices, SUM(F_SumPrice) AS GrossSum, SUM(F_Takhfif) AS SumTakhfif,
       MIN(F_Code_C) AS MinSeq, MAX(F_Code_C) AS MaxSeq, MIN(F_Time) AS FirstTime, MAX(F_Time) AS LastTime
FROM dbo.Rs_Facture GROUP BY F_Date ORDER BY F_Date DESC;
GO
PRINT '=== Res_DailyRep: last 5 daily reports ===';
SELECT TOP 5 * FROM dbo.Res_DailyRep ORDER BY F_Date DESC;
GO
PRINT '=== FACTURE F invoices around last days (link check) ===';
SELECT TOP 10 F.Fac_Code, F.Fac_Type, F.Fac_Code_C, F.C_Code, F.Fac_Date, F.Fac_Time, F.Sum_Price, F.Takhfif,
       F.FNaghd, F.FCheck, F.FNesieh, F.Card, F.ResFac_Code_C, F.Res_Alias_Name, F.Fac_Comment, F.Sanad_Code,
       F.SumTakhfif_P, F.Takhfif_P_KolFact, F.HazHamlFact, F.SayerAvarez, F.FRent, F.HazinehBar
FROM dbo.FACTURE F
WHERE F.Fac_Type = 'F'
ORDER BY F.Fac_Date DESC, F.Fac_Time DESC;
GO
PRINT '=== Cross check: POS totals for 2026-02-08 vs FACTURE F 2026-02-08 ===';
SELECT (SELECT SUM(F_SumPrice) FROM dbo.Rs_Facture WHERE F_Date = '2026-02-08') AS PosGross_20260208,
       (SELECT SUM(F_Takhfif)  FROM dbo.Rs_Facture WHERE F_Date = '2026-02-08') AS PosTakhfif_20260208,
       (SELECT SUM(Sum_Price)  FROM dbo.FACTURE  WHERE Fac_Type = 'F' AND Fac_Date = '2026-02-08') AS FactF_20260208,
       (SELECT SUM(Sum_Price)  FROM dbo.FACTURE  WHERE Fac_Type = 'F' AND Fac_Date = '2026-02-07') AS FactF_20260207,
       (SELECT SUM(F_SumPrice) FROM dbo.Rs_Facture WHERE F_Date = '2026-02-07') AS PosGross_20260207;
GO
PRINT '=== FACTURE 'F' rows by date: how many per day? ===';
SELECT Fac_Date, COUNT(*) AS Invoices, SUM(Sum_Price) AS SumPrice FROM dbo.FACTURE
WHERE Fac_Type='F' GROUP BY Fac_Date ORDER BY Fac_Date DESC OFFSET 0 ROWS FETCH NEXT 15 ROWS ONLY;
GO
PRINT '=== Res_Factur_Log columns + sample ===';
SELECT c.name, t.name AS TypeName FROM sys.columns c JOIN sys.types t ON t.user_type_id=c.user_type_id
WHERE c.object_id = OBJECT_ID('dbo.Res_Factur_Log') ORDER BY c.column_id;
GO
SELECT TOP 5 * FROM dbo.Res_Factur_Log ORDER BY F_Code DESC;
GO
PRINT '=== Other live candidates: Cash / Check / PosTrans recent ===';
SELECT TOP 5 * FROM dbo.PosTrans ORDER BY TransDate DESC;
GO
