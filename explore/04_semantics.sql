SET NOCOUNT ON;
USE Holoo1;
GO
PRINT '=== FACTURE by type (fixed) ===';
SELECT Fac_Type, COUNT(*) AS Cnt, SUM(CASE WHEN [Delete] = 1 THEN 1 ELSE 0 END) AS Deleted,
       MIN(Fac_Date) AS MinD, MAX(Fac_Date) AS MaxD, SUM(Sum_Price) AS TotalPrice
FROM dbo.FACTURE GROUP BY Fac_Type ORDER BY Cnt DESC;
GO
PRINT '=== FACTURE: last 40 rows (all types) with customer name ===';
SELECT TOP 40 F.Fac_Code, F.Fac_Type, F.Fac_Code_C, F.C_Code, C.C_Name, F.Fac_Date, F.Fac_Time,
       F.Sum_Price, F.Takhfif, F.FNaghd, F.FCheck, F.FNesieh, F.Card, F.Sum_Levy, F.Sum_Scot,
       F.ResFac_Code_C, F.Res_Alias_Name, F.Fac_Comment, F.Sanad_Code, F.[Delete], F.Fac_State, F.UsedByUser
FROM dbo.FACTURE F LEFT JOIN dbo.CUSTOMER C ON C.C_Code = F.C_Code
ORDER BY F.Fac_Date DESC, F.Fac_Time DESC;
GO
PRINT '=== Res_Facture vs its lines (Res_Fac_List) ===';
SELECT TOP 15 rf.F_Code, rf.F_Code_C, rf.F_Date, rf.F_Time, rf.F_SumPrice, rf.F_Takhfif, rf.Bon_Price,
       L.Lines, L.SumLine, L.SumTakhfifSatri, L.SumDarsad
FROM dbo.Res_Facture rf
OUTER APPLY (
  SELECT COUNT(*) AS Lines, SUM(FL_Few * FL_Price) AS SumLine,
         SUM(TakhfifSatrir) AS SumTakhfifSatri, SUM(Darsad_Takhfif) AS SumDarsad
  FROM dbo.Res_Fac_List fl WHERE fl.F_Code = rf.F_Code
) L
ORDER BY rf.F_Code DESC;
GO
PRINT '=== Res_Fac_List columns ===';
SELECT c.name, t.name AS TypeName FROM sys.columns c JOIN sys.types t ON t.user_type_id=c.user_type_id
WHERE c.object_id = OBJECT_ID('dbo.Res_Fac_List') ORDER BY c.column_id;
GO
PRINT '=== Res_Fac_List sample for last invoices ===';
SELECT TOP 25 * FROM dbo.Res_Fac_List WHERE F_Code >= 590650 ORDER BY F_Code DESC, FL_Index;
GO
PRINT '=== Triggers on POS / invoice tables ===';
SELECT OBJECT_NAME(t.parent_id) AS TableName, t.name AS TriggerName, t.is_disabled, t.is_instead_of_trigger
FROM sys.triggers t WHERE OBJECT_NAME(t.parent_id) IN
 ('Res_Facture','Rs_Facture','Res_Fac_List','Rs_FacList','FACTURE','FACTART','Res_DailyRep','Res_Factur_Log');
GO
PRINT '=== Modules mentioning Res_Facture or Rs_Facture ===';
SELECT o.type_desc, o.name FROM sys.sql_modules m JOIN sys.objects o ON o.object_id = m.object_id
WHERE m.definition LIKE '%Rs_Facture%' OR m.definition LIKE '%Res_Facture%';
GO
PRINT '=== All stored procedures (names) ===';
SELECT name FROM sys.procedures ORDER BY name;
GO
