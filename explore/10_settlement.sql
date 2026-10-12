SET NOCOUNT ON;
USE Holoo1;
GO
PRINT '=== Settlement state of current-day invoices ===';
SELECT Check_Tasvieh, COUNT(*) AS Cnt, MIN(F_Time) AS FirstTime, MAX(F_Time) AS LastTime,
       SUM(F_SumPrice) AS Gross, SUM(F_Naghd + F_Pos + F_Nesieh) AS Paid
FROM dbo.Res_Facture GROUP BY Check_Tasvieh;
GO
PRINT '=== Unsettled invoices (payment not yet recorded) ===';
SELECT F_Code, F_Code_C, F_Time, F_SumPrice, F_Takhfif, F_Naghd, F_Pos, F_Nesieh, Bon_Price, Check_Tasvieh, UserCode, FACTRANS
FROM dbo.Res_Facture WHERE Check_Tasvieh = 0 ORDER BY F_Code DESC;
GO
PRINT '=== Newest 20 invoices: full money picture ===';
SELECT TOP 20 F_Code, F_Code_C, F_Time, F_SumPrice, F_Takhfif, F_Naghd, F_Pos, F_Nesieh, Bon_Price,
       (F_SumPrice - F_Takhfif) AS Net, (F_Naghd + F_Pos + F_Nesieh) AS Paid, Check_Tasvieh,
       Sum_Levy, Sum_Scot, F_Service, FACTRANS, Pos_Code, UserCode
FROM dbo.Res_Facture ORDER BY F_Code DESC;
GO
PRINT '=== LIVE CANDIDATE VIEW (read-only, current day) ===';
SELECT TOP 10
       r.F_Code            AS InvoiceId,
       r.F_Code_C          AS DailyNo,
       dbo.MiladiToShamsi(r.F_Date) AS JalaliDate,
       r.F_Time            AS TimeOfSale,
       r.Pos_Code          AS PosTerminal,
       r.UserCode          AS Cashier,
       ln.LineCount        AS Items,
       ln.SumLines         AS LineSum,
       r.F_SumPrice        AS GrossTotal,
       r.F_Takhfif         AS DiscountAmount,
       (r.F_SumPrice - r.F_Takhfif) AS NetTotal,
       (r.F_Naghd + r.F_Pos + r.F_Nesieh) AS Paid,
       r.Check_Tasvieh     AS Settled
FROM dbo.Res_Facture r
OUTER APPLY (SELECT COUNT(*) AS LineCount, SUM(fl.FL_Few * fl.FL_Price) AS SumLines
             FROM dbo.Res_Fac_List fl WHERE fl.F_Code = r.F_Code) ln
ORDER BY r.F_Code DESC;
GO
PRINT '=== Same query against the archive (yesterday) to prove identical shape ===';
SELECT TOP 5
       r.F_Code AS InvoiceId, r.F_Code_C AS DailyNo, dbo.MiladiToShamsi(r.F_Date) AS JalaliDate, r.F_Time,
       ln.LineCount AS Items, r.F_SumPrice AS GrossTotal, r.F_Takhfif AS Discount, (r.F_SumPrice-r.F_Takhfif) AS NetTotal,
       (r.F_Naghd+r.F_Pos+r.F_Nesieh) AS Paid
FROM dbo.Rs_Facture r
OUTER APPLY (SELECT COUNT(*) AS LineCount FROM dbo.Rs_FacList fl WHERE fl.F_Code = r.F_Code) ln
WHERE r.F_Date = (SELECT MAX(F_Date) FROM dbo.Rs_Facture)
ORDER BY r.F_Code DESC;
GO
