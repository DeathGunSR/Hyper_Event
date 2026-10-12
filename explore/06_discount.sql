SET NOCOUNT ON;
USE Holoo1;
GO
PRINT '=== Discount semantics check on Rs_Facture (rows with F_Takhfif<>0) ===';
SELECT TOP 25 F_Code, F_Code_C, F_Date, F_Time, F_SumPrice, F_Takhfif, DarsadTakhfif, SumTakhfif_P, Takhfif_P_KolFact,
       Bon_Price, F_Pos, F_Naghd, F_Nesieh, Sum_Levy, Sum_Scot, F_Service,
       (F_SumPrice - F_Takhfif) AS NetAfterTakhfif,
       (F_Naghd + F_Nesieh + F_Pos) AS SumPaid
FROM dbo.Rs_Facture
WHERE F_Takhfif <> 0 OR DarsadTakhfif <> 0 OR SumTakhfif_P <> 0 OR Takhfif_P_KolFact <> 0
ORDER BY F_Code DESC;
GO
PRINT '=== How many archived invoices carry a discount? ===';
SELECT COUNT(*) AS TotalRows,
       SUM(CASE WHEN F_Takhfif <> 0 THEN 1 ELSE 0 END) AS WithTakhfifAmount,
       SUM(CASE WHEN DarsadTakhfif <> 0 THEN 1 ELSE 0 END) AS WithTakhfifPercent,
       SUM(CASE WHEN SumTakhfif_P <> 0 THEN 1 ELSE 0 END) AS WithTakhfifSatriP,
       SUM(CASE WHEN Takhfif_P_KolFact <> 0 THEN 1 ELSE 0 END) AS WithTakhfifKolFact,
       SUM(CASE WHEN Bon_Price <> 0 THEN 1 ELSE 0 END) AS WithBon
FROM dbo.Rs_Facture;
GO
PRINT '=== MSetup columns of interest ===';
SELECT c.name, t.name AS TypeName FROM sys.columns c JOIN sys.types t ON t.user_type_id=c.user_type_id
WHERE c.object_id = OBJECT_ID('dbo.MSetup')
  AND (c.name LIKE '%Res%' OR c.name LIKE '%Pos%' OR c.name LIKE '%Fact%' OR c.name LIKE '%Takhfif%'
       OR c.name LIKE '%Date%' OR c.name LIKE '%Type%' OR c.name LIKE '%Shop%' OR c.name LIKE '%Sale%')
ORDER BY c.column_id;
GO
PRINT '=== MSetup all columns ===';
SELECT c.name, t.name AS TypeName FROM sys.columns c JOIN sys.types t ON t.user_type_id=c.user_type_id
WHERE c.object_id = OBJECT_ID('dbo.MSetup') ORDER BY c.column_id;
GO
PRINT '=== MSetup row count ===';
SELECT COUNT(*) AS MSetupRows FROM dbo.MSetup;
GO
PRINT '=== Res_Order / Res_OrderList / Res_Factur_Log / Res_Desk ===';
SELECT 'Res_Order' AS T, COUNT(*) AS C FROM dbo.Res_Order
UNION ALL SELECT 'Res_OrderList', COUNT(*) FROM dbo.Res_OrderList
UNION ALL SELECT 'Res_Desk', COUNT(*) FROM dbo.Res_Desk;
GO
SELECT TOP 5 * FROM dbo.Res_Factur_Log ORDER BY 1 DESC;
GO
PRINT '=== CurrentDate proc ===';
EXEC sp_helptext 'dbo.CurrentDate';
GO
