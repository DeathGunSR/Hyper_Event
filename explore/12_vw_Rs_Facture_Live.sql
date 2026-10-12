/* =====================================================================
   پیشنهادی – اجرا نشده است (این فایل فقط آماده‌سازی شده)
   ویوی یکپارچه «فاکتورهای تک‌فروشی در لحظه» روی Holoo1
   ---------------------------------------------------------------------
   نکته مهم: قبل از اجرا در دیتابیس عملیاتی هولو، با پشتیبان‌گیری و در
   ساعت غیرکاری تست شود. هیچ جدول/داده‌ای تغییر نمی‌کند؛ فقط یک VIEW
   خواندنی ساخته می‌شود.

   اجرا:
     sqlcmd -S "localhost\SQLEXPRESS" -E -C -I -d Holoo1 -i "explore\12_vw_Rs_Facture_Live.sql"
   ===================================================================== */

USE Holoo1;
GO

IF OBJECT_ID('dbo.VW_Facture_TakForoushi_Live', 'V') IS NOT NULL
    DROP VIEW dbo.VW_Facture_TakForoushi_Live;
GO

CREATE VIEW dbo.VW_Facture_TakForoushi_Live
AS
/* ---------- فاکتورهای امروز (در جریان، لحظه‌ای) ---------- */
SELECT
      CAST('LIVE' AS varchar(6))                     AS Source,
      r.F_Code                                       AS InvoiceId,
      r.F_Code_C                                     AS DailyNo,
      r.F_Type,
      r.Fac_Type,
      r.FType_KF,
      r.F_Date                                       AS GregorianDate,
      dbo.MiladiToShamsi(r.F_Date)                  AS JalaliDate,
      r.F_Time                                       AS TimeOfSale,
      r.UserCode                                     AS CashierCode,
      u.Name                                         AS CashierName,
      r.Pos_Code                                     AS PosTerminal,
      r.C_Code                                       AS CustomerCode,
      r.Alias_Name                                   AS CustomerAlias,
      ln.ItemCount                                   AS ItemCount,
      ln.LineSum                                     AS LineSum,
      r.F_SumPrice                                   AS GrossTotal,
      r.F_Takhfif                                    AS DiscountAmount,      -- مبلغ تخفیف روی جمع فاکتور
      r.DarsadTakhfif                                AS DiscountPercent,
      (r.F_SumPrice - r.F_Takhfif)                   AS NetTotal,            -- جمع نهایی قابل پرداخت
      r.Bon_Price                                    AS BonAmount,
      r.F_Naghd                                      AS PaidCash,
      r.F_Pos                                        AS PaidCard,
      r.F_Nesieh                                     AS PaidCredit,
      r.Sum_Levy                                     AS Levy,
      r.Sum_Scot                                     AS Scot,
      r.F_Comment
FROM dbo.Res_Facture r
LEFT JOIN dbo.USERDB u ON u.UserCodeInc = r.UserCode
OUTER APPLY (
      SELECT COUNT(*) AS ItemCount, SUM(fl.FL_Few * fl.FL_Price) AS LineSum
      FROM dbo.Res_Fac_List fl WHERE fl.F_Code = r.F_Code
) ln
WHERE r.F_Type <> 3           -- 3 = برگشتی

UNION ALL

/* ---------- آرشیو روزهای قبل ---------- */
SELECT
      CAST('ARCHIVE' AS varchar(6)),
      a.F_Code,
      a.F_Code_C,
      a.F_Type,
      a.Fac_Type,
      a.FType_KF,
      a.F_Date,
      dbo.MiladiToShamsi(a.F_Date),
      a.F_Time,
      a.UserCode,
      u2.Name,
      a.Pos_Code,
      a.C_Code,
      a.Alias_Name,
      ln2.ItemCount,
      ln2.LineSum,
      a.F_SumPrice,
      a.F_Takhfif,
      a.DarsadTakhfif,
      (a.F_SumPrice - a.F_Takhfif),
      a.Bon_Price,
      a.F_Naghd,
      a.F_Pos,
      a.F_Nesieh,
      a.Sum_Levy,
      a.Sum_Scot,
      a.F_Comment
FROM dbo.Rs_Facture a
LEFT JOIN dbo.USERDB u2 ON u2.UserCodeInc = a.UserCode
OUTER APPLY (
      SELECT COUNT(*) AS ItemCount, SUM(fl.FL_Few * fl.FL_Price) AS LineSum
      FROM dbo.Rs_FacList fl WHERE fl.F_Code = a.F_Code
) ln2
WHERE a.F_Type <> 3;
GO

/* ---- نمونه استفاده: آخرین ۲۰ فاکتور تک‌فروشی ---- */
-- SELECT TOP 20 * FROM dbo.VW_Facture_TakForoushi_Live
-- WHERE Source = 'LIVE' ORDER BY InvoiceId DESC;
GO

/* ---- نمونه استفاده: جمع فروش امروز ---- */
-- SELECT COUNT(*) AS Invoices, SUM(GrossTotal) AS Gross, SUM(DiscountAmount) AS Discount, SUM(NetTotal) AS Net
-- FROM dbo.VW_Facture_TakForoushi_Live WHERE Source = 'LIVE';
GO
