SET NOCOUNT ON;
USE Holoo1;
GO
PRINT '=== Exact COUNT(*) ===';
SELECT 'FACTURE' AS T, COUNT(*) AS C FROM dbo.FACTURE
UNION ALL SELECT 'FACTART', COUNT(*) FROM dbo.FACTART
UNION ALL SELECT 'Res_Facture', COUNT(*) FROM dbo.Res_Facture
UNION ALL SELECT 'Res_Fac_List', COUNT(*) FROM dbo.Res_Fac_List
UNION ALL SELECT 'Rs_Facture', COUNT(*) FROM dbo.Rs_Facture
UNION ALL SELECT 'Rs_FacList', COUNT(*) FROM dbo.Rs_FacList
UNION ALL SELECT 'Res_DailyRep', COUNT(*) FROM dbo.Res_DailyRep
UNION ALL SELECT 'PosTrans', COUNT(*) FROM dbo.PosTrans
UNION ALL SELECT 'PosActive', COUNT(*) FROM dbo.PosActive
UNION ALL SELECT 'TmpFacture', COUNT(*) FROM dbo.TmpFacture
UNION ALL SELECT 'TmpFactart', COUNT(*) FROM dbo.TmpFactart
UNION ALL SELECT 'Res_Factur_Log', COUNT(*) FROM dbo.Res_Factur_Log;
GO
PRINT '=== Columns: Res_Facture ===';
SELECT c.name, t.name AS TypeName, c.max_length FROM sys.columns c
JOIN sys.types t ON t.user_type_id = c.user_type_id
WHERE c.object_id = OBJECT_ID('dbo.Res_Facture') ORDER BY c.column_id;
GO
PRINT '=== Columns: Rs_Facture ===';
SELECT c.name, t.name AS TypeName, c.max_length FROM sys.columns c
JOIN sys.types t ON t.user_type_id = c.user_type_id
WHERE c.object_id = OBJECT_ID('dbo.Rs_Facture') ORDER BY c.column_id;
GO
PRINT '=== Columns: Res_DailyRep ===';
SELECT c.name, t.name AS TypeName, c.max_length FROM sys.columns c
JOIN sys.types t ON t.user_type_id = c.user_type_id
WHERE c.object_id = OBJECT_ID('dbo.Res_DailyRep') ORDER BY c.column_id;
GO
PRINT '=== Columns: PosTrans / PosActive ===';
SELECT c.name, t.name AS TypeName, c.max_length FROM sys.columns c
JOIN sys.types t ON t.user_type_id = c.user_type_id
WHERE c.object_id = OBJECT_ID('dbo.PosTrans') ORDER BY c.column_id;
GO
SELECT c.name, t.name AS TypeName, c.max_length FROM sys.columns c
JOIN sys.types t ON t.user_type_id = c.user_type_id
WHERE c.object_id = OBJECT_ID('dbo.PosActive') ORDER BY c.column_id;
GO
PRINT '=== Res_Facture: sample current-day invoices ===';
SELECT TOP 20 F_Code, F_Type, F_Code_C, F_Date, F_Time, C_Code, Desk_Name, Res_No, Pos_Code,
       F_SumPrice, F_Takhfif, DarsadTakhfif, SumTakhfif_P, Takhfif_P_KolFact, F_Service, F_Haz1, F_Haz2,
       F_Naghd, F_Nesieh, Bon_Price, Sum_Levy, Sum_Scot, Check_Tasvieh, Fac_Type, FType_KF, UserCode, [Selected]
FROM dbo.Res_Facture ORDER BY F_Code DESC;
GO
PRINT '=== Res_Facture: hourly distribution of current day ===';
SELECT F_Date, LEFT(F_Time,2) AS Hour, COUNT(*) AS Cnt, SUM(F_SumPrice) AS Total FROM dbo.Res_Facture
GROUP BY F_Date, LEFT(F_Time,2) ORDER BY 2;
GO
PRINT '=== Rs_Facture: sample latest archive rows ===';
SELECT TOP 10 * FROM dbo.Rs_Facture ORDER BY F_Code DESC;
GO
PRINT '=== Res_DailyRep: sample ===';
SELECT TOP 10 * FROM dbo.Res_DailyRep ORDER BY 1 DESC;
GO
