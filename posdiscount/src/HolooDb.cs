using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Globalization;

namespace PosDiscount
{
    public class CashierInfo
    {
        public int Code;
        public string Name = "";
        public bool Active = true;
        public bool CanDiscount;
        public int DayCount;       // فاکتورهای امروز (روز کاری دیتابیس)
        public long LastFCode;     // آخرین کد فاکتور ثبت‌شده توسط این کاربر
    }

    /// <summary>یک فاکتور تک‌فروشی (صندوقی) از Res_Facture.</summary>
    public class InvoiceInfo
    {
        public long FCode;          // کلید داخلی/IDENTITY
        public long FCodeC;         // شماره فاکتور روزانه (همان که صندوق‌دار می‌بیند)
        public int FType;           // 2 = فروش ، 3 = برگشتی/ابطال
        public DateTime Date;
        public string Time = "";
        public string JalaliDate = "";
        public int UserCode;
        public string CashierName = "";
        public double Gross;        // F_SumPrice
        public double Discount;     // F_Takhfif فعلی
        public double Naghd, Pos, Nesieh, Bon;
        public string PosTerminal = "";
        public string CustomerCode = "";
        public string CustomerAlias = "";
        public string Mobile = "";        // Alias_Mobile ثبت‌شده روی سر فاکتور هولو (اگر باشد)
        public string Comment = "";
        public int Items;           // تعداد ردیف اقلام
        public double Few;          // مجموع تعداد کالا
        public double LineSum;      // جمع ردیف‌ها (برای کنترل صحت)

        public double Net { get { return Gross - Discount; } }
        public bool IsReturn { get { return FType == 3; } }

        public string PayText()
        {
            List<string> p = new List<string>();
            if (Naghd > 0) p.Add("نقد " + Num.F(Naghd));
            if (Pos > 0) p.Add("کارت " + Num.F(Pos));
            if (Nesieh > 0) p.Add("نسیه " + Num.F(Nesieh));
            if (Bon > 0) p.Add("بن " + Num.F(Bon));
            return p.Count == 0 ? "—" : string.Join(" + ", p.ToArray());
        }
    }

    /// <summary>وضعیت یک فاکتور در منبع هلو در بازهٔ تطبیق (جدول روز یا تاریخچه).</summary>
    public class SourceInvoiceState
    {
        public long FCode;
        public long FCodeC;
        public int FType;
        public DateTime Date;
        public int UserCode;
        public double Gross;
        public bool FromLive;
        public bool FromArchive;

        public string TableLabel
        {
            get
            {
                if (FromLive && FromArchive) return "both";
                return FromLive ? "live" : (FromArchive ? "archive" : "none");
            }
        }

        /// <summary>فروش معتبر: نوع ۲ و مبلغ مثبت. ابطال/برگشتی نوع ۳ است.</summary>
        public bool IsValidSale { get { return FType == 2 && Gross > 0; } }
        public bool IsVoided { get { return FType == 3; } }
    }

    /// <summary>عکس فوری فقط‌خواندنی از کدهای یک بازهٔ تاریخ در هلو.</summary>
    public class SourceSnapshot
    {
        public DateTime From;
        public DateTime To;
        public Dictionary<long, SourceInvoiceState> ByCode = new Dictionary<long, SourceInvoiceState>();
        public HashSet<string> DaysWithRows = new HashSet<string>();
        public int LiveRows;
        public int ArchiveRows;
    }

    public class DaySummary
    {
        public DateTime Day;
        public int Count;
        public double Gross;
        public double Discount;
        public double Net;
        public long MaxFCode;
    }

    public class DbStatus
    {
        public bool Ok;
        public string Message = "";
        public string ServerVersion = "";
        public string DatabaseName = "";
        public int Users;
        public int Invoices;
        public DateTime? Day;
        public string JalaliDay = "";
    }

    /// <summary>
    /// لایه دسترسی داده روی دیتابیس هولو (فقط SQL Server؛ بدون وابستگی به نرم‌افزار هولو).
    /// تمام کوئری‌های خواندن روی Res_Facture / Res_Fac_List / USERDB هستند.
    /// </summary>
    public class HolooDb
    {
        private readonly string _connectionString;

        public HolooDb(string connectionString)
        {
            _connectionString = connectionString;
        }

        public string ConnectionString { get { return _connectionString; } }

        private SqlConnection Open()
        {
            SqlConnection c = new SqlConnection(_connectionString);
            c.Open();
            return c;
        }

        public static string DescribeConnectionError(Exception ex)
        {
            string m = ex.Message;
            if (m.IndexOf("network-related", StringComparison.OrdinalIgnoreCase) >= 0 ||
                m.IndexOf("server was not found", StringComparison.OrdinalIgnoreCase) >= 0 ||
                m.IndexOf("error: 40", StringComparison.OrdinalIgnoreCase) >= 0)
                return "اتصال به SQL Server برقرار نشد. نام سرور/اینستنس و روش احراز هویت را در تنظیمات بررسی کنید.\r\nجزئیات: " + m;
            if (m.IndexOf("Login failed", StringComparison.OrdinalIgnoreCase) >= 0)
                return "ورود به SQL Server رد شد. برای اتصال ویندوزی مطمئن شوید کاربر ویندوزی به دیتابیس دسترسی دارد.\r\nجزئیات: " + m;
            if (m.IndexOf("Cannot open database", StringComparison.OrdinalIgnoreCase) >= 0)
                return "دیتابیس هولو باز نشد (نام دیتابیس یا دسترسی).\r\nجزئیات: " + m;
            return "خطای دیتابیس: " + m;
        }

        // ------------------------------------------------------------------
        //  تست اتصال
        // ------------------------------------------------------------------
        public DbStatus Test()
        {
            DbStatus st = new DbStatus();
            try
            {
                using (SqlConnection c = Open())
                {
                    using (SqlCommand cmd = new SqlCommand(
                        "SELECT CAST(SERVERPROPERTY('ProductVersion') AS varchar(30)) AS V, DB_NAME() AS D, " +
                        "(SELECT COUNT(*) FROM dbo.USERDB) AS U, (SELECT COUNT(*) FROM dbo.Res_Facture) AS F",
                        c))
                    using (SqlDataReader dr = cmd.ExecuteReader())
                    {
                        if (dr.Read())
                        {
                            st.ServerVersion = dr.IsDBNull(0) ? "" : dr.GetString(0);
                            st.DatabaseName = dr.IsDBNull(1) ? "" : dr.GetString(1);
                            st.Users = dr.IsDBNull(2) ? 0 : Convert.ToInt32(dr.GetValue(2));
                            st.Invoices = dr.IsDBNull(3) ? 0 : Convert.ToInt32(dr.GetValue(3));
                        }
                    }
                    using (SqlCommand cmd2 = new SqlCommand("SELECT MAX(F_Date) FROM dbo.Res_Facture", c))
                    {
                        object o = cmd2.ExecuteScalar();
                        if (o != null && o != DBNull.Value) st.Day = Convert.ToDateTime(o);
                    }
                    using (SqlCommand cmd3 = new SqlCommand(
                        "SELECT dbo.MiladiToShamsi((SELECT MAX(F_Date) FROM dbo.Res_Facture))", c))
                    {
                        object o3 = cmd3.ExecuteScalar();
                        if (o3 != null && o3 != DBNull.Value) st.JalaliDay = Convert.ToString(o3).Trim();
                    }
                }
                st.Ok = true;
                st.Message = "اتصال برقرار است — دیتابیس " + st.DatabaseName + " (نسخه SQL " + st.ServerVersion + ")";
            }
            catch (Exception ex)
            {
                st.Ok = false;
                st.Message = DescribeConnectionError(ex);
            }
            return st;
        }

        // ------------------------------------------------------------------
        //  روز کاری / آخرین کد فاکتور
        // ------------------------------------------------------------------
        /// <summary>روز کاری جاری دیتابیس = بزرگ‌ترین تاریخ فاکتور در Res_Facture.</summary>
        public DateTime GetWorkDay()
        {
            using (SqlConnection c = Open())
            using (SqlCommand cmd = new SqlCommand("SELECT ISNULL(MAX(F_Date), CONVERT(datetime, CONVERT(varchar(10), GETDATE(), 112), 112)) FROM dbo.Res_Facture", c))
            {
                object o = cmd.ExecuteScalar();
                if (o == null || o == DBNull.Value) return DateTime.Today;
                return Convert.ToDateTime(o);
            }
        }

        public long GetMaxFCode()
        {
            using (SqlConnection c = Open())
            using (SqlCommand cmd = new SqlCommand("SELECT ISNULL(MAX(F_Code), 0) FROM dbo.Res_Facture", c))
            {
                object o = cmd.ExecuteScalar();
                return (o == null || o == DBNull.Value) ? 0L : Convert.ToInt64(o);
            }
        }

        /// <summary>بزرگ‌ترین کد فاکتور قبل از یک روز مشخص (برای «نمایش فاکتورهای همان روز» هنگام شروع).</summary>
        public long GetLastFCodeBeforeDay(DateTime day)
        {
            using (SqlConnection c = Open())
            using (SqlCommand cmd = new SqlCommand("SELECT ISNULL(MAX(F_Code), 0) FROM dbo.Res_Facture WHERE F_Date < @day", c))
            {
                cmd.Parameters.Add("@day", SqlDbType.SmallDateTime).Value = day.Date;
                object o = cmd.ExecuteScalar();
                return (o == null || o == DBNull.Value) ? 0L : Convert.ToInt64(o);
            }
        }

        // ------------------------------------------------------------------
        //  کاربران (صندوق‌دارها)
        // ------------------------------------------------------------------
        public List<CashierInfo> GetCashiers()
        {
            List<CashierInfo> list = new List<CashierInfo>();
            const string sql = @"
DECLARE @day datetime;
SELECT @day = MAX(F_Date) FROM dbo.Res_Facture;
SELECT u.UserCodeInc, u.Name,
       CAST(ISNULL(u.DeActive,0) AS int) AS DeActive,
       CAST(ISNULL(u.ChangeTakhfif,0) AS int) AS CanDiscount,
       ISNULL((SELECT COUNT(*) FROM dbo.Res_Facture r
               WHERE r.UserCode = u.UserCodeInc AND r.F_Type <> 3 AND r.F_Date = @day), 0) AS DayCount,
       ISNULL((SELECT MAX(r2.F_Code) FROM dbo.Res_Facture r2
               WHERE r2.UserCode = u.UserCodeInc), 0) AS LastFCode
FROM dbo.USERDB u
ORDER BY u.Name";
            using (SqlConnection c = Open())
            using (SqlCommand cmd = new SqlCommand(sql, c))
            using (SqlDataReader dr = cmd.ExecuteReader())
            {
                while (dr.Read())
                {
                    CashierInfo ci = new CashierInfo();
                    ci.Code = dr.GetInt32(0);
                    ci.Name = dr.IsDBNull(1) ? "" : dr.GetString(1).Trim();
                    ci.Active = dr.GetInt32(2) == 0;
                    ci.CanDiscount = dr.GetInt32(3) == 1;
                    ci.DayCount = dr.GetInt32(4);
                    ci.LastFCode = Convert.ToInt64(dr.GetValue(5));
                    list.Add(ci);
                }
            }
            return list;
        }

        // ------------------------------------------------------------------
        //  فاکتورها
        // ------------------------------------------------------------------
        private const string InvoiceColumns = @"
SELECT TOP (@max)
       r.F_Code, r.F_Code_C, r.F_Type, r.F_Date, r.F_Time, r.UserCode,
       u.Name AS CashierName,
       r.F_SumPrice, r.F_Takhfif, r.F_Naghd, r.F_Pos, r.F_Nesieh, r.Bon_Price,
       r.Pos_Code, r.C_Code, r.Alias_Name, r.F_Comment,
       ISNULL(l.Items,0) AS Items, ISNULL(l.Few,0) AS Few, ISNULL(l.LineSum,0) AS LineSum,
       dbo.MiladiToShamsi(r.F_Date) AS JalaliDate,
       ISNULL(r.Alias_Mobile,'') AS AliasMobile
FROM dbo.Res_Facture r
LEFT JOIN dbo.USERDB u ON u.UserCodeInc = r.UserCode
OUTER APPLY (SELECT COUNT(*) AS Items, SUM(fl.FL_Few) AS Few, SUM(fl.FL_Few * fl.FL_Price) AS LineSum
             FROM dbo.Res_Fac_List fl WHERE fl.F_Code = r.F_Code) l
";

        /// <summary>فاکتورهای جدید یک کاربر با کد بزرگ‌تر از afterFCode (ترتیب صعودی).</summary>
        public List<InvoiceInfo> GetNewInvoices(long afterFCode, int userCode, int max)
        {
            string sql = InvoiceColumns + @"
WHERE r.F_Type <> 3 AND r.F_Code > @after AND r.UserCode = @user
ORDER BY r.F_Code ASC";
            return ReadInvoices(sql, userCode, afterFCode, max);
        }

        /// <summary>فاکتورهای امروز (روز کاری دیتابیس) برای نمایش در داشبورد.</summary>
        public List<InvoiceInfo> GetWorkDayInvoices(int userCode, int max)
        {
            string sql = InvoiceColumns + @"
WHERE r.F_Type <> 3 AND r.F_Date = (SELECT MAX(F_Date) FROM dbo.Res_Facture) AND r.UserCode = @user
ORDER BY r.F_Code DESC";
            return ReadInvoices(sql, userCode, 0, max);
        }

        /// <summary>همه فاکتورهای جدید (همهٔ صندوق‌دارها) از جدول روز + تاریخچه، برای عامل همگام‌سازی.</summary>
        public List<InvoiceInfo> GetNewInvoicesAll(long afterFCode, DateTime minDate, int max)
        {
            if (max <= 0) max = 100;
            const string filter = " WHERE r.F_Type <> 3 AND r.F_Code > @after AND r.F_Date >= @min ORDER BY r.F_Code ASC";
            List<InvoiceInfo> live = ReadInvoices(InvoiceColumns + filter, 0, afterFCode, max, minDate);

            const string archiveColumns = @"
SELECT TOP (@max)
       r.F_Code, r.F_Code_C, r.F_Type, r.F_Date, r.F_Time, r.UserCode,
       u.Name AS CashierName,
       r.F_SumPrice, r.F_Takhfif, r.F_Naghd, r.F_Pos, r.F_Nesieh, r.Bon_Price,
       r.Pos_Code, r.C_Code, r.Alias_Name, r.F_Comment,
       0 AS Items, 0 AS Few, 0 AS LineSum,
       dbo.MiladiToShamsi(r.F_Date) AS JalaliDate,
       ISNULL(r.Alias_Mobile,'') AS AliasMobile
FROM dbo.Rs_Facture r
LEFT JOIN dbo.USERDB u ON u.UserCodeInc = r.UserCode
";
            List<InvoiceInfo> archive = ReadInvoices(archiveColumns + filter, 0, afterFCode, max, minDate);

            Dictionary<long, InvoiceInfo> map = new Dictionary<long, InvoiceInfo>();
            for (int i = 0; i < archive.Count; i++) map[archive[i].FCode] = archive[i];
            for (int i = 0; i < live.Count; i++) map[live[i].FCode] = live[i];   // نسخهٔ روز، دقیق‌تر است

            List<InvoiceInfo> all = new List<InvoiceInfo>(map.Values);
            all.Sort(delegate (InvoiceInfo a, InvoiceInfo b) { return a.FCode.CompareTo(b.FCode); });
            if (all.Count > max) all = all.GetRange(0, max);
            return all;
        }

        /// <summary>کد همهٔ فاکتورهای معتبر یک روز (جدول روز + تاریخچه) برای تطبیق.</summary>
        public HashSet<long> GetInvoiceCodesForDay(DateTime day)
        {
            HashSet<long> set = new HashSet<long>();
            using (SqlConnection c = Open())
            using (SqlCommand cmd = new SqlCommand(@"
SELECT F_Code FROM dbo.Res_Facture WHERE F_Type <> 3 AND F_Date = @d
UNION
SELECT F_Code FROM dbo.Rs_Facture WHERE F_Type <> 3 AND F_Date = @d", c))
            {
                cmd.Parameters.Add("@d", SqlDbType.SmallDateTime).Value = day.Date;
                using (SqlDataReader dr = cmd.ExecuteReader())
                    while (dr.Read()) set.Add(Convert.ToInt64(dr.GetValue(0)));
            }
            return set;
        }

        /// <summary>
        /// خواندن همهٔ کدهای یک بازهٔ تاریخ از جدول روز و تاریخچه (فقط خواندنی).
        /// این متد برای تطبیق تاریخی استفاده می‌شود و برخلاف GetInvoiceCodesForDay فقط یک روز نیست.
        /// اگر کوئری خطا بدهد استثنا بالا می‌رود تا تطبیق، منبع ناقص را «قطعی» فرض نکند.
        /// </summary>
        public SourceSnapshot GetSourceSnapshot(DateTime from, DateTime to)
        {
            if (to < from) { DateTime t = from; from = to; to = t; }
            SourceSnapshot snap = new SourceSnapshot();
            snap.From = from.Date;
            snap.To = to.Date;

            using (SqlConnection c = Open())
            using (SqlCommand cmd = new SqlCommand(@"
SELECT 'live' AS Src, F_Code, F_Code_C, F_Type, F_Date, UserCode, F_SumPrice
FROM dbo.Res_Facture
WHERE F_Date >= @from AND F_Date <= @to
UNION ALL
SELECT 'archive', F_Code, F_Code_C, F_Type, F_Date, UserCode, F_SumPrice
FROM dbo.Rs_Facture
WHERE F_Date >= @from AND F_Date <= @to", c))
            {
                cmd.Parameters.Add("@from", SqlDbType.SmallDateTime).Value = snap.From;
                cmd.Parameters.Add("@to", SqlDbType.SmallDateTime).Value = snap.To;
                using (SqlDataReader r = cmd.ExecuteReader())
                {
                    while (r.Read())
                    {
                        bool live = string.Equals(Src(r, 0), "live", StringComparison.Ordinal);
                        long code = Convert.ToInt64(r.GetValue(1));
                        SourceInvoiceState st;
                        if (!snap.ByCode.TryGetValue(code, out st))
                        {
                            st = new SourceInvoiceState();
                            st.FCode = code;
                            snap.ByCode[code] = st;
                        }

                        // اگر همان کد در هر دو جدول بود، نسخهٔ جدول روز مرجع است.
                        if (live) st.FromLive = true; else st.FromArchive = true;
                        if (live || !st.FromLive)
                        {
                            st.FCodeC = r.IsDBNull(2) ? 0 : Convert.ToInt64(r.GetValue(2));
                            st.FType = r.IsDBNull(3) ? 0 : Convert.ToInt32(r.GetValue(3));
                            st.Date = r.IsDBNull(4) ? DateTime.MinValue : Convert.ToDateTime(r.GetValue(4));
                            st.UserCode = r.IsDBNull(5) ? 0 : Convert.ToInt32(r.GetValue(5));
                            st.Gross = r.IsDBNull(6) ? 0 : Convert.ToDouble(r.GetValue(6), CultureInfo.InvariantCulture);
                        }

                        if (live) snap.LiveRows++; else snap.ArchiveRows++;
                        if (!r.IsDBNull(4))
                            snap.DaysWithRows.Add(Convert.ToDateTime(r.GetValue(4)).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                    }
                }
            }
            return snap;
        }

        private static string Src(SqlDataReader r, int i)
        {
            if (r.IsDBNull(i)) return "";
            return Convert.ToString(r.GetValue(i), CultureInfo.InvariantCulture);
        }

        /// <summary>بزرگ‌ترین F_Code در جدول روز و تاریخچه.</summary>
        public long GetMaxFCodeAll()
        {
            using (SqlConnection c = Open())
            using (SqlCommand cmd = new SqlCommand(@"
SELECT ISNULL(MAX(F_Code),0) FROM (
    SELECT F_Code FROM dbo.Res_Facture
    UNION ALL
    SELECT F_Code FROM dbo.Rs_Facture
) t", c))
            {
                object o = cmd.ExecuteScalar();
                return (o == null || o == DBNull.Value) ? 0L : Convert.ToInt64(o);
            }
        }

        /// <summary>کمترین F_Code از یک تاریخ به بعد (برای شروع وااترمارک بدون ورود فاکتورهای قدیمی).</summary>
        public long GetMinFCodeSince(DateTime date)
        {
            using (SqlConnection c = Open())
            using (SqlCommand cmd = new SqlCommand(@"
SELECT ISNULL(MIN(F_Code),0) FROM (
    SELECT F_Code FROM dbo.Res_Facture WHERE F_Type <> 3 AND F_Date >= @d
    UNION ALL
    SELECT F_Code FROM dbo.Rs_Facture WHERE F_Type <> 3 AND F_Date >= @d
) t", c))
            {
                cmd.Parameters.Add("@d", SqlDbType.SmallDateTime).Value = date.Date;
                object o = cmd.ExecuteScalar();
                return (o == null || o == DBNull.Value) ? 0L : Convert.ToInt64(o);
            }
        }

        public InvoiceInfo GetInvoice(long fCode)
        {
            string sql = InvoiceColumns + @" WHERE r.F_Code = @after";
            List<InvoiceInfo> l = ReadInvoices(sql, 0, fCode, 1);
            return l.Count > 0 ? l[0] : null;
        }

        private List<InvoiceInfo> ReadInvoices(string sql, int userCode, long after, int max)
        {
            return ReadInvoices(sql, userCode, after, max, null);
        }

        private List<InvoiceInfo> ReadInvoices(string sql, int userCode, long after, int max, DateTime? minDate)
        {
            List<InvoiceInfo> list = new List<InvoiceInfo>();
            if (max <= 0) max = 1;
            using (SqlConnection c = Open())
            using (SqlCommand cmd = new SqlCommand(sql, c))
            {
                cmd.Parameters.Add("@max", SqlDbType.Int).Value = max;
                if (sql.IndexOf("@user") >= 0) cmd.Parameters.Add("@user", SqlDbType.Int).Value = userCode;
                if (sql.IndexOf("@after") >= 0) cmd.Parameters.Add("@after", SqlDbType.BigInt).Value = after;
                if (minDate.HasValue && sql.IndexOf("@min") >= 0)
                    cmd.Parameters.Add("@min", SqlDbType.SmallDateTime).Value = minDate.Value.Date;
                using (SqlDataReader dr = cmd.ExecuteReader())
                {
                    while (dr.Read())
                    {
                        InvoiceInfo v = new InvoiceInfo();
                        v.FCode = Convert.ToInt64(dr.GetValue(0));
                        v.FCodeC = dr.IsDBNull(1) ? 0 : Convert.ToInt64(dr.GetValue(1));
                        v.FType = dr.IsDBNull(2) ? 0 : Convert.ToInt32(dr.GetValue(2));
                        v.Date = dr.IsDBNull(3) ? DateTime.Today : Convert.ToDateTime(dr.GetValue(3));
                        v.Time = dr.IsDBNull(4) ? "" : dr.GetString(4).Trim();
                        v.UserCode = dr.IsDBNull(5) ? 0 : Convert.ToInt32(dr.GetValue(5));
                        v.CashierName = dr.IsDBNull(6) ? "" : dr.GetString(6).Trim();
                        v.Gross = dr.IsDBNull(7) ? 0 : Convert.ToDouble(dr.GetValue(7));
                        v.Discount = dr.IsDBNull(8) ? 0 : Convert.ToDouble(dr.GetValue(8));
                        v.Naghd = dr.IsDBNull(9) ? 0 : Convert.ToDouble(dr.GetValue(9));
                        v.Pos = dr.IsDBNull(10) ? 0 : Convert.ToDouble(dr.GetValue(10));
                        v.Nesieh = dr.IsDBNull(11) ? 0 : Convert.ToDouble(dr.GetValue(11));
                        v.Bon = dr.IsDBNull(12) ? 0 : Convert.ToDouble(dr.GetValue(12));
                        v.PosTerminal = dr.IsDBNull(13) ? "" : dr.GetString(13).Trim();
                        v.CustomerCode = dr.IsDBNull(14) ? "" : dr.GetString(14).Trim();
                        v.CustomerAlias = dr.IsDBNull(15) ? "" : dr.GetString(15).Trim();
                        v.Comment = dr.IsDBNull(16) ? "" : dr.GetString(16).Trim();
                        v.Items = dr.IsDBNull(17) ? 0 : Convert.ToInt32(dr.GetValue(17));
                        v.Few = dr.IsDBNull(18) ? 0 : Convert.ToDouble(dr.GetValue(18));
                        v.LineSum = dr.IsDBNull(19) ? 0 : Convert.ToDouble(dr.GetValue(19));
                        v.JalaliDate = dr.IsDBNull(20) ? "" : dr.GetString(20).Trim();
                        v.Mobile = dr.IsDBNull(21) ? "" : dr.GetString(21).Trim();
                        list.Add(v);
                    }
                }
            }
            return list;
        }

        // ------------------------------------------------------------------
        //  جمع روز
        // ------------------------------------------------------------------
        public DaySummary GetDaySummary(int userCode)
        {
            const string sql = @"
DECLARE @day datetime;
SELECT @day = MAX(F_Date) FROM dbo.Res_Facture;
SELECT @day AS Day, COUNT(*) AS Cnt, ISNULL(SUM(r.F_SumPrice),0) AS Gross,
       ISNULL(SUM(r.F_Takhfif),0) AS Disc, ISNULL(SUM(r.F_SumPrice - r.F_Takhfif),0) AS Net,
       ISNULL(MAX(r.F_Code),0) AS MaxF
FROM dbo.Res_Facture r
WHERE r.UserCode = @user AND r.F_Type <> 3 AND r.F_Date = @day";
            DaySummary s = new DaySummary();
            using (SqlConnection c = Open())
            using (SqlCommand cmd = new SqlCommand(sql, c))
            {
                cmd.Parameters.Add("@user", SqlDbType.Int).Value = userCode;
                using (SqlDataReader dr = cmd.ExecuteReader())
                {
                    if (dr.Read())
                    {
                        s.Day = dr.IsDBNull(0) ? DateTime.Today : Convert.ToDateTime(dr.GetValue(0));
                        s.Count = dr.IsDBNull(1) ? 0 : Convert.ToInt32(dr.GetValue(1));
                        s.Gross = dr.IsDBNull(2) ? 0 : Convert.ToDouble(dr.GetValue(2));
                        s.Discount = dr.IsDBNull(3) ? 0 : Convert.ToDouble(dr.GetValue(3));
                        s.Net = dr.IsDBNull(4) ? 0 : Convert.ToDouble(dr.GetValue(4));
                        s.MaxFCode = dr.IsDBNull(5) ? 0 : Convert.ToInt64(dr.GetValue(5));
                    }
                }
            }
            return s;
        }

        /// <summary>مجموع تخفیف‌های ثبت‌شده امروز برای یک کاربر (برای سقف روزانه).</summary>
        public double GetTodayDiscountSum(int userCode)
        {
            const string sql = @"
DECLARE @day datetime;
SELECT @day = MAX(F_Date) FROM dbo.Res_Facture;
SELECT ISNULL(SUM(F_Takhfif),0) FROM dbo.Res_Facture
WHERE UserCode = @user AND F_Type <> 3 AND F_Date = @day";
            using (SqlConnection c = Open())
            using (SqlCommand cmd = new SqlCommand(sql, c))
            {
                cmd.Parameters.Add("@user", SqlDbType.Int).Value = userCode;
                object o = cmd.ExecuteScalar();
                return (o == null || o == DBNull.Value) ? 0 : Convert.ToDouble(o);
            }
        }

        // ------------------------------------------------------------------
        //  فقط خواندن
        // ------------------------------------------------------------------
        // نکته مهم: این کلاس هیچ دستور INSERT / UPDATE / DELETE روی دیتابیس فروشگاه (هولو) ندارد.
        // تمام اطلاعات موبایل/امتیاز در دیتابیس محلی برنامه (SQLite) نگهداری می‌شود؛
        // دیتابیس هولو فقط خوانده می‌شود و هیچ‌چیز در آن درج یا تغییر داده نمی‌شود.

        /// <summary>اندازه واقعی جدول فاکتور جاری (برای نمایش وضعیت).</summary>
        public int GetLiveInvoiceCount()
        {
            using (SqlConnection c = Open())
            using (SqlCommand cmd = new SqlCommand("SELECT COUNT(*) FROM dbo.Res_Facture", c))
                return Convert.ToInt32(cmd.ExecuteScalar());
        }
    }
}
