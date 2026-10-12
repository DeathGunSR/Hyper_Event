using System;
using System.IO;
using System.Text;

namespace PosDiscount
{
    /// <summary>لاگ محلی برنامه (CSV روزانه + فایل خطا). هیچ نوشتنی در دیتابیس هولو انجام نمی‌دهد.</summary>
    public static class CsvLog
    {
        private static readonly object Gate = new object();

        private static string FolderFor(AppSettings s)
        {
            string folder = s.LogFolderFull;
            try
            {
                if (!Directory.Exists(folder)) Directory.CreateDirectory(folder);
            }
            catch (Exception) { }
            return folder;
        }

        public static void Row(AppSettings s, string cashierName, InvoiceInfo inv, DiscountResult res, string action)
        {
            Row(s, cashierName, inv, res, action, "", 0);
        }

        /// <summary>یک ردیف لاگ برای هر فاکتور (شامل شماره موبایل و امتیاز ثبت‌شده در دیتابیس محلی).</summary>
        public static void Row(AppSettings s, string cashierName, InvoiceInfo inv, DiscountResult res, string action,
            string mobile, int points, int customerPoints)
        {
            try
            {
                string folder = FolderFor(s);
                string file = Path.Combine(folder, "pos-discount-" + DateTime.Now.ToString("yyyyMMdd") + ".csv");
                bool isNew = !File.Exists(file);
                StringBuilder sb = new StringBuilder();
                lock (Gate)
                {
                    if (isNew)
                        sb.AppendLine(string.Join(";", new string[] {
                            "زمان ثبت در برنامه", "صندوق‌دار", "کد صندوق‌دار", "شناسه فاکتور", "شماره فاکتور", "ساعت فاکتور",
                            "تعداد ردیف", "جمع کل", "درصد تخفیف", "مبلغ تخفیف", "قابل پرداخت", "قاعده", "وضعیت", "توضیح",
                            "شماره موبایل", "امتیاز این فاکتور", "امتیاز کل مشتری" }));

                    sb.AppendLine(string.Join(";", new string[] {
                        E(DateTime.Now.ToString("yyyy/MM/dd HH:mm:ss")),
                        E(cashierName),
                        inv == null ? "" : inv.UserCode.ToString(),
                        inv == null ? "" : inv.FCode.ToString(),
                        inv == null ? "" : inv.FCodeC.ToString(),
                        inv == null ? "" : E(inv.Time),
                        inv == null ? "" : inv.Items.ToString(),
                        inv == null ? "" : Num.F(inv.Gross),
                        inv == null || res == null ? "" : Num.P(res.Percent),
                        inv == null || res == null ? "" : Num.F(res.Discount),
                        inv == null || res == null ? "" : Num.F(res.Net),
                        res == null ? "" : E(res.RuleName),
                        E(action),
                        res == null ? "" : E(res.Reason),
                        E(mobile),
                        points.ToString(),
                        customerPoints.ToString()
                    }));
                    File.AppendAllText(file, sb.ToString(), new UTF8Encoding(true));
                }
            }
            catch (Exception) { /* لاگ نباید برنامه را متوقف کند */ }
        }

        public static void Row(AppSettings s, string cashierName, InvoiceInfo inv, DiscountResult res, string action, string mobile, int points)
        {
            Row(s, cashierName, inv, res, action, mobile, points, points);
        }

        public static void Info(string folder, string text)
        {
            try
            {
                string f = Path.Combine(folder, "app.log");
                if (!Directory.Exists(folder)) Directory.CreateDirectory(folder);
                lock (Gate)
                    File.AppendAllText(f, DateTime.Now.ToString("yyyy/MM/dd HH:mm:ss") + "\t" + text + Environment.NewLine, new UTF8Encoding(true));
            }
            catch (Exception) { }
        }

        public static void Info(AppSettings s, string text)
        {
            Info(s == null ? "." : s.LogFolderFull, text);
        }

        public static void Error(string folder, string text)
        {
            try
            {
                string f = Path.Combine(folder, "errors.log");
                if (!Directory.Exists(folder)) Directory.CreateDirectory(folder);
                lock (Gate)
                    File.AppendAllText(f, DateTime.Now.ToString("yyyy/MM/dd HH:mm:ss") + "\t" + text + Environment.NewLine, new UTF8Encoding(true));
            }
            catch (Exception) { }
        }

        public static void Error(AppSettings s, string text)
        {
            Error(s == null ? "." : s.LogFolderFull, text);
        }

        private static string E(string v)
        {
            if (string.IsNullOrEmpty(v)) return "";
            string t = v.Replace("\"", "\"\"");
            if (t.IndexOf(';') >= 0 || t.IndexOf('"') >= 0 || t.IndexOf('\n') >= 0 || t.IndexOf('\r') >= 0)
                t = "\"" + t + "\"";
            return t;
        }
    }
}
