SET NOCOUNT ON;
USE Holoo1;
GO
PRINT '=== Res_Facture: distinct dates (is it only current day?) ===';
SELECT F_Date, COUNT(*) AS Cnt FROM dbo.Res_Facture GROUP BY F_Date ORDER BY F_Date;
GO
PRINT '=== Res_Facture: discount field usage (current day) ===';
SELECT COUNT(*) AS Rows_,
       SUM(CASE WHEN F_Takhfif <> 0 THEN 1 ELSE 0 END) AS With_Takhfif,
       SUM(CASE WHEN DarsadTakhfif <> 0 THEN 1 ELSE 0 END) AS With_DarsadTakhfif,
       SUM(CASE WHEN SumTakhfif_P <> 0 THEN 1 ELSE 0 END) AS With_SumTakhfifP,
       SUM(CASE WHEN Takhfif_P_KolFact <> 0 THEN 1 ELSE 0 END) AS With_KolFact,
       SUM(CASE WHEN Bon_Price <> 0 THEN 1 ELSE 0 END) AS With_Bon,
       SUM(CASE WHEN F_Service <> 0 THEN 1 ELSE 0 END) AS With_Service,
       SUM(CASE WHEN F_Haz1 <> 0 OR F_Haz2 <> 0 THEN 1 ELSE 0 END) AS With_Haz,
       SUM(CASE WHEN Sum_Levy <> 0 THEN 1 ELSE 0 END) AS With_Levy,
       SUM(CASE WHEN Sum_Scot <> 0 THEN 1 ELSE 0 END) AS With_Scot,
       SUM(CASE WHEN F_Peik <> 0 THEN 1 ELSE 0 END) AS With_Peik
FROM dbo.Res_Facture;
GO
PRINT '=== Res_Fac_List: line-level discount usage (current day) ===';
SELECT COUNT(*) AS Lines,
       SUM(CASE WHEN TakhfifSatrir <> 0 THEN 1 ELSE 0 END) AS With_TakhfifSatrir,
       SUM(CASE WHEN Darsad_Takhfif <> 0 THEN 1 ELSE 0 END) AS With_DarsadTakhfif,
       SUM(CASE WHEN SumTakhfifSatri_P <> 0 THEN 1 ELSE 0 END) AS With_SumTakhfifSatriP,
       SUM(CASE WHEN Levy <> 0 THEN 1 ELSE 0 END) AS With_Levy,
       SUM(CASE WHEN Scot <> 0 THEN 1 ELSE 0 END) AS With_Scot,
       SUM(CASE WHEN MinA_Name <> '' THEN 1 ELSE 0 END) AS With_Comment
FROM dbo.Res_Fac_List;
GO
PRINT '=== Archive: Rs_FacList line discount usage (sample of last 30 days) ===';
SELECT COUNT(*) AS Lines,
       SUM(CASE WHEN TakhfifSatrir <> 0 THEN 1 ELSE 0 END) AS With_TakhfifSatrir,
       SUM(CASE WHEN Darsad_Takhfif <> 0 THEN 1 ELSE 0 END) AS With_DarsadTakhfif,
       SUM(CASE WHEN SumTakhfifSatri_P <> 0 THEN 1 ELSE 0 END) AS With_SumTakhfifSatriP
FROM dbo.Rs_FacList fl JOIN dbo.Rs_Facture f ON f.F_Code = fl.F_Code
WHERE f.F_Date >= '2026-01-10';
GO
PRINT '=== Articles with tax flags ===';
SELECT SUM(CASE WHEN Include_Tax = 1 THEN 1 ELSE 0 END) AS Taxable, COUNT(*) AS Total FROM dbo.ARTICLE;
GO
PRINT '=== Set_Sarfasl rows used by POS module ===';
SELECT * FROM dbo.Set_Sarfasl WHERE S_Name LIKE '%Res%';
GO
PRINT '=== PosTrans last 5 (bank POS) ===';
SELECT TOP 5 ID, PosType, Pos_Serial, PosPrice, bank_name, TransDate, Pos_AcceptorId, Pos_TerminalId
FROM dbo.PosTrans ORDER BY ID DESC;
GO
PRINT '=== Res_Fac_List for the newest invoice, joined to Article (print path) ===';
SELECT TOP 12 F.F_Code, F.F_Code_C, F.F_Time, FL.FL_Index, FL.A_Code, A.A_Name, FL.FL_Few, FL.FL_Price,
       (FL.FL_Few*FL.FL_Price) AS LineTotal, FL.TakhfifSatrir, F.F_SumPrice
FROM dbo.Res_Facture F JOIN dbo.Res_Fac_List FL ON FL.F_Code = F.F_Code
LEFT JOIN dbo.ARTICLE A ON A.A_Code = FL.A_Code
WHERE F.F_Date = (SELECT MAX(F_Date) FROM dbo.Res_Facture)
ORDER BY F.F_Code DESC, FL.FL_Index;
GO
