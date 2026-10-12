SET NOCOUNT ON;
USE Holoo1;
GO
PRINT '=== half 1 (live) ===';
SELECT TOP 5 CAST('LIVE' AS varchar(6)) AS Source, r.F_Code AS InvoiceId, r.F_Code_C AS DailyNo, r.F_Type, r.Fac_Type,
      r.FType_KF, dbo.MiladiToShamsi(r.F_Date) AS JalaliDate, r.F_Time, r.UserCode, u.Name AS CashierName, r.Pos_Code,
      ln.ItemCount, ln.LineSum, r.F_SumPrice AS GrossTotal, r.F_Takhfif AS DiscountAmount, (r.F_SumPrice - r.F_Takhfif) AS NetTotal,
      r.F_Naghd, r.F_Pos, r.F_Nesieh, r.F_Comment
FROM dbo.Res_Facture r
LEFT JOIN dbo.USERDB u ON u.UserCodeInc = r.UserCode
OUTER APPLY (SELECT COUNT(*) AS ItemCount, SUM(fl.FL_Few*fl.FL_Price) AS LineSum FROM dbo.Res_Fac_List fl WHERE fl.F_Code=r.F_Code) ln
WHERE r.F_Type <> 3 ORDER BY r.F_Code DESC;
GO
PRINT '=== half 2 (archive) ===';
SELECT TOP 5 CAST('ARCHIVE' AS varchar(6)) AS Source, a.F_Code AS InvoiceId, a.F_Code_C AS DailyNo, a.F_Type, a.Fac_Type,
      a.FType_KF, dbo.MiladiToShamsi(a.F_Date) AS JalaliDate, a.F_Time, a.UserCode, u2.Name AS CashierName, a.Pos_Code,
      ln2.ItemCount, ln2.LineSum, a.F_SumPrice AS GrossTotal, a.F_Takhfif AS DiscountAmount, (a.F_SumPrice - a.F_Takhfif) AS NetTotal,
      a.F_Naghd, a.F_Pos, a.F_Nesieh, a.F_Comment
FROM dbo.Rs_Facture a
LEFT JOIN dbo.USERDB u2 ON u2.UserCodeInc = a.UserCode
OUTER APPLY (SELECT COUNT(*) AS ItemCount, SUM(fl.FL_Few*fl.FL_Price) AS LineSum FROM dbo.Rs_FacList fl WHERE fl.F_Code=a.F_Code) ln2
WHERE a.F_Type <> 3 ORDER BY a.F_Code DESC;
GO
PRINT '=== today total (live) ===';
SELECT COUNT(*) AS Invoices, SUM(r.F_SumPrice) AS Gross, SUM(r.F_Takhfif) AS Discount,
       SUM(r.F_SumPrice - r.F_Takhfif) AS Net, MIN(r.F_Time) AS From_, MAX(r.F_Time) AS To_
FROM dbo.Res_Facture r WHERE r.F_Type <> 3;
GO
