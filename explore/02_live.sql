SET NOCOUNT ON;
USE Holoo1;
GO
PRINT '=== NOW (server clock) ===';
SELECT SYSDATETIME() AS NowGregorian, dbo.MiladiToShamsi(GETDATE()) AS NowShamsi;
GO
PRINT '=== Correct row counts (candidates) ===';
SELECT t.name AS TableName,
       SUM(CASE WHEN p.index_id IN (0,1) THEN p.rows ELSE 0 END) AS ClusteredRows,
       SUM(CASE WHEN p.index_id > 1   THEN p.rows ELSE 0 END) AS IndexRows,
       MAX(CASE WHEN c.name = 'Fac_Date'  THEN 1 ELSE 0 END) AS Fac_Date,
       MAX(CASE WHEN c.name = 'Fac_Time'  THEN 1 ELSE 0 END) AS Fac_Time,
       MAX(CASE WHEN c.name = 'F_Date'    THEN 1 ELSE 0 END) AS F_Date,
       MAX(CASE WHEN c.name = 'F_Time'    THEN 1 ELSE 0 END) AS F_Time,
       MAX(CASE WHEN c.name = 'Fac_Type'  THEN 1 ELSE 0 END) AS Fac_Type,
       MAX(CASE WHEN c.name = 'F_Type'    THEN 1 ELSE 0 END) AS F_Type,
       MAX(CASE WHEN c.name = 'Sum_Price' THEN 1 ELSE 0 END) AS Sum_Price,
       MAX(CASE WHEN c.name = 'F_SumPrice'THEN 1 ELSE 0 END) AS F_SumPrice,
       MAX(CASE WHEN c.name = 'Takhfif'   THEN 1 ELSE 0 END) AS Takhfif,
       MAX(CASE WHEN c.name = 'F_Takhfif' THEN 1 ELSE 0 END) AS F_Takhfif,
       MAX(CASE WHEN c.name = 'Delete'    THEN 1 ELSE 0 END) AS [Delete]
FROM sys.tables t
JOIN sys.indexes i      ON i.object_id = t.object_id
JOIN sys.partitions p   ON p.object_id = t.object_id AND p.index_id = i.index_id
LEFT JOIN sys.columns c ON c.object_id = t.object_id
WHERE t.name IN ('FACTURE','FACTART','FBAILPRE','ABAILPRE','TmpFacture','TmpFactart',
                 'Res_Facture','Res_Fac_List','Rs_Facture','Rs_FacList','RMInvoice','RMInvoiceDtl',
                 'Res_DailyRep','Kh_Facture','Kh_Fac_List','PosTrans','PosActive','TmpFactart','DelFACTSPD')
GROUP BY t.name
ORDER BY ClusteredRows DESC;
GO
PRINT '=== FACTURE: date span + type breakdown (all rows) ===';
SELECT COUNT(*) AS Total,
       MIN(Fac_Date) AS MinDate, MAX(Fac_Date) AS MaxDate,
       MAX(Fac_Time) AS MaxTime
FROM dbo.FACTURE;
GO
SELECT Fac_Type, COUNT(*) AS Cnt, SUM(CASE WHEN Delete = 1 THEN 1 ELSE 0 END) AS DeletedCnt,
       MIN(Fac_Date) AS MinD, MAX(Fac_Date) AS MaxD, MIN(Fac_Code_C) AS MinSeq, MAX(Fac_Code_C) AS MaxSeq
FROM dbo.FACTURE
GROUP BY Fac_Type
ORDER BY Cnt DESC;
GO
PRINT '=== FACTURE: last 15 invoices overall ===';
SELECT TOP 15 Fac_Code, Fac_Type, Fac_Code_C, C_Code, Fac_Date, Fac_Time, Sum_Price, Takhfif, Sum_Few,
       FNaghd, FCheck, FNesieh, Sanad_Code, [Delete], UserCode, DateUser, TimeUser, Fac_State
FROM dbo.FACTURE
ORDER BY Fac_Date DESC, Fac_Time DESC;
GO
PRINT '=== Res_Facture: span/types ===';
SELECT COUNT(*) AS Total, MIN(F_Date) AS MinD, MAX(F_Date) AS MaxD FROM dbo.Res_Facture;
GO
SELECT F_Type, COUNT(*) AS Cnt, MIN(F_Date) AS MinD, MAX(F_Date) AS MaxD FROM dbo.Res_Facture GROUP BY F_Type;
GO
PRINT '=== Rs_Facture: span/types ===';
SELECT COUNT(*) AS Total, MIN(F_Date) AS MinD, MAX(F_Date) AS MaxD FROM dbo.Rs_Facture;
GO
SELECT Fac_Type, COUNT(*) AS Cnt, MIN(F_Date) AS MinD, MAX(F_Date) AS MaxD FROM dbo.Rs_Facture GROUP BY Fac_Type;
GO
PRINT '=== TmpFacture (unsaved/in-progress) ===';
SELECT COUNT(*) AS Total, MIN(Fac_Date) AS MinD, MAX(Fac_Date) AS MaxD FROM dbo.TmpFacture;
GO
SELECT TOP 10 * FROM dbo.TmpFacture ORDER BY Fac_Date DESC;
GO
PRINT '=== RMInvoice / Res_DailyRep / PosTrans ===';
SELECT COUNT(*) AS RMInvoiceCnt FROM dbo.RMInvoice;
SELECT COUNT(*) AS ResDailyRepCnt FROM dbo.Res_DailyRep;
SELECT COUNT(*) AS PosTransCnt FROM dbo.PosTrans;
GO
