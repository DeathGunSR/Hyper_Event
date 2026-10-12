SET NOCOUNT ON;
USE Holoo1;
GO
PRINT '=== USERDB key columns ===';
SELECT c.name AS Col, t.name AS Typ, c.is_nullable AS Nullable
FROM sys.columns c JOIN sys.types t ON t.user_type_id = c.user_type_id
WHERE c.object_id = OBJECT_ID('dbo.USERDB')
  AND c.name IN ('UserCodeInc','Name','DeActive','LockField','ChangeTakhfif','EditFact','SuperVizor','Fac_Sell')
ORDER BY c.column_id;
GO
PRINT '=== USERDB all users (active flag) ===';
SELECT UserCodeInc, Name, ISNULL(CAST(DeActive AS int), -1) AS DeActive FROM dbo.USERDB ORDER BY UserCodeInc;
GO
PRINT '=== Res_Facture invoice types ===';
SELECT Fac_Type, F_Type, FType_KF, COUNT(*) AS Cnt, MIN(F_Code) AS MinF, MAX(F_Code) AS MaxF
FROM dbo.Res_Facture GROUP BY Fac_Type, F_Type, FType_KF ORDER BY Cnt DESC;
GO
PRINT '=== last 5 invoices: F_SumPrice vs line sum ===';
SELECT TOP 5 r.F_Code, r.F_Code_C, r.F_Date, r.F_Time, r.UserCode, r.F_SumPrice, r.F_Takhfif,
       l.LineSum, l.Items, r.F_Naghd, r.F_Pos, r.F_Nesieh, r.Pos_Code,
       CASE WHEN ABS(ISNULL(l.LineSum,0) - ISNULL(r.F_SumPrice,0)) < 1 THEN 'MATCH' ELSE 'DIFF' END AS CheckSum
FROM dbo.Res_Facture r
OUTER APPLY (SELECT SUM(fl.FL_Few * fl.FL_Price) AS LineSum, COUNT(*) AS Items
             FROM dbo.Res_Fac_List fl WHERE fl.F_Code = r.F_Code) l
WHERE r.F_Type <> 3
ORDER BY r.F_Code DESC;
GO
PRINT '=== per-user invoice counts (Res_Facture) ===';
SELECT r.UserCode, u.Name, COUNT(*) AS Cnt, MAX(r.F_Code) AS MaxF
FROM dbo.Res_Facture r LEFT JOIN dbo.USERDB u ON u.UserCodeInc = r.UserCode
GROUP BY r.UserCode, u.Name ORDER BY Cnt DESC;
GO
PRINT '=== server version ===';
SELECT CAST(SERVERPROPERTY('ProductVersion') AS varchar(30)) AS ProductVersion,
       CAST(SERVERPROPERTY('Edition') AS varchar(60)) AS Edition;
GO
