using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Globalization;
using System.IO;
using System.Text;

namespace PosDiscount
{
    /// <summary>یک فاکتور ذخیره‌شده در دیتابیس محلی (شامل موبایل و امتیاز).</summary>
    public class StoredInvoice
    {
        public long FCode;
        public long FCodeC;
        public string Date = "";             // میلادی yyyy-MM-dd
        public string Jalali = "";           // شمسی yyyy/MM/dd
        public string Time = "";
        public int UserCode;
        public string Cashier = "";
        public double Gross;
        public double HolooDiscount;
        public double Net;
        public string Mobile = "";
        public string CustomerCode = "";
        public string CustomerAlias = "";
        public int Points;
        public double OfferedDiscount;
        public string FirstSeen = "";
        public string UpdatedAt = "";
    }

    /// <summary>خلاصه یک شماره موبایل (برای جست‌وجو و فهرست امتیازات).</summary>
    /// <summary>یک صندوق‌دار در دیتابیس محلی (برای محدود کردن قرعه‌کشی به مشتریان یک صندوقدار).</summary>
    public class CashierChoice
    {
        public int UserCode;
        public string Name = "";
        public int Invoices;
    }

    public class PhoneProfile
    {
        public string Mobile = "";
        public string Name = "";
        public int Points;
        public int Count;
        public double Amount;
        public double Discount;
        public string First = "";
        public string Last = "";
    }

    /// <summary>
    /// دیتابیس محلی برنامه (SQLite) — «هیچ داده‌ای» در دیتابیس فروشگاه (هولو) نوشته نمی‌شود.
    /// نگه‌داری: فاکتورهای ثبت‌شده، شماره‌های موبایل مشتریان و امتیاز هر شماره.
    /// فایل: pos-discount.db کنار فایل اجرایی (قابل تغییر در تنظیمات).
    /// </summary>
    public class LocalStore : IDisposable
    {
        private readonly object _gate = new object();
        private readonly string _path;
        private SQLiteConnection _cn;

        public string LastError = "";

        public LocalStore(string filePath)
        {
            _path = filePath;
        }

        public string FilePath { get { return _path; } }

        /// <summary>مسیر فایل دیتابیس محلی: مسیر مطلق یا نسبی به پوشه برنامه.</summary>
        public static string ResolveFile(string baseDir, string file)
        {
            string f = string.IsNullOrEmpty(file) ? "pos-discount.db" : file.Trim();
            if (Path.IsPathRooted(f)) return f;
            return Path.Combine(baseDir, f);
        }

        // ==================================================================
        //  باز کردن و ساخت جدول‌ها
        // ==================================================================
        public bool Open(out string error)
        {
            error = "";
            lock (_gate)
            {
                try
                {
                    if (_cn != null) { error = ""; return true; }

                    string dir = Path.GetDirectoryName(_path);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

                    _cn = new SQLiteConnection("Data Source=" + _path + ";Version=3;Pooling=True;Max Pool Size=4;");
                    _cn.Open();

                    Exec("PRAGMA journal_mode=WAL;");
                    Exec("PRAGMA synchronous=NORMAL;");
                    Exec("PRAGMA busy_timeout=5000;");
                    EnsureSchema();
                    return true;
                }
                catch (Exception ex)
                {
                    _cn = null;
                    error = Describe(ex);
                    LastError = error;
                    return false;
                }
            }
        }

        /// <summary>خطای قابل فهم برای کاربر (مثلاً نبود کتابخانه SQLite کنار فایل اجرایی).</summary>
        public static string Describe(Exception ex)
        {
            if (ex == null) return "خطای نامشخص";
            string m = ex.Message ?? "";
            if (ex is TypeLoadException || ex is FileNotFoundException || ex is DllNotFoundException ||
                m.IndexOf("SQLite", StringComparison.OrdinalIgnoreCase) >= 0 && ex is BadImageFormatException)
                return "کتابخانه دیتابیس محلی (System.Data.SQLite.dll و پوشه‌های x86/x64) کنار فایل اجرایی پیدا نشد. "
                    + "این فایل‌ها همراه بیلد در پوشه bin هستند؛ مطمئن شوید همه فایل‌های پوشه bin را کپی کرده‌اید.\r\nجزئیات: " + m;
            if (m.IndexOf("locked", StringComparison.OrdinalIgnoreCase) >= 0 || m.IndexOf("busy", StringComparison.OrdinalIgnoreCase) >= 0)
                return "فایل دیتابیس محلی در حال استفاده است. اگر برنامه باز است، همان نمونه فعلی را استفاده کنید.\r\nجزئیات: " + m;
            return "خطای دیتابیس محلی: " + m;
        }

        private void EnsureSchema()
        {
            Exec(@"
CREATE TABLE IF NOT EXISTS Invoices(
    FCode           INTEGER PRIMARY KEY,
    FCodeC          INTEGER NOT NULL DEFAULT 0,
    FDate           TEXT    NOT NULL DEFAULT '',
    Jalali          TEXT    NOT NULL DEFAULT '',
    FTime           TEXT    NOT NULL DEFAULT '',
    UserCode        INTEGER NOT NULL DEFAULT 0,
    Cashier         TEXT    NOT NULL DEFAULT '',
    Gross           REAL    NOT NULL DEFAULT 0,
    HolooDiscount   REAL    NOT NULL DEFAULT 0,
    Net             REAL    NOT NULL DEFAULT 0,
    Mobile          TEXT    NOT NULL DEFAULT '',
    CustomerCode    TEXT    NOT NULL DEFAULT '',
    CustomerAlias   TEXT    NOT NULL DEFAULT '',
    Points          INTEGER NOT NULL DEFAULT 0,
    OfferedDiscount REAL    NOT NULL DEFAULT 0,
    FirstSeen       TEXT    NOT NULL DEFAULT '',
    UpdatedAt       TEXT    NOT NULL DEFAULT ''
);");
            Exec("CREATE INDEX IF NOT EXISTS IX_Invoices_Mobile ON Invoices(Mobile);");
            Exec("CREATE INDEX IF NOT EXISTS IX_Invoices_FDate ON Invoices(FDate);");
            Exec("CREATE INDEX IF NOT EXISTS IX_Invoices_User ON Invoices(UserCode);");

            Exec(@"
CREATE TABLE IF NOT EXISTS Mobiles(
    Mobile    TEXT PRIMARY KEY,
    Name      TEXT NOT NULL DEFAULT '',
    Note      TEXT NOT NULL DEFAULT '',
    FirstSeen TEXT NOT NULL DEFAULT '',
    LastSeen  TEXT NOT NULL DEFAULT ''
);");

            Exec(@"
CREATE TABLE IF NOT EXISTS Meta(
    Key   TEXT PRIMARY KEY,
    Value TEXT NOT NULL DEFAULT ''
);");

            // قرعه‌کشی‌های انجام‌شده و برندگان آن‌ها (برای مستندسازی و بازتولید با همان seed)
            Exec(@"
CREATE TABLE IF NOT EXISTS Draws(
    DrawId       INTEGER PRIMARY KEY AUTOINCREMENT,
    At           TEXT    NOT NULL DEFAULT '',
    TypeName     TEXT    NOT NULL DEFAULT '',
    Seed         INTEGER NOT NULL DEFAULT 0,
    Winners      INTEGER NOT NULL DEFAULT 0,
    Participants INTEGER NOT NULL DEFAULT 0,
    TotalTickets INTEGER NOT NULL DEFAULT 0,
    MinPoints    INTEGER NOT NULL DEFAULT 1,
    FromYmd      TEXT    NOT NULL DEFAULT '',
    ToYmd        TEXT    NOT NULL DEFAULT '',
    Prizes       TEXT    NOT NULL DEFAULT '',
    Scope        TEXT    NOT NULL DEFAULT ''
);");
            Exec(@"
CREATE TABLE IF NOT EXISTS DrawWinners(
    DrawId   INTEGER NOT NULL,
    Rank     INTEGER NOT NULL,
    Mobile   TEXT    NOT NULL DEFAULT '',
    Name     TEXT    NOT NULL DEFAULT '',
    Points   INTEGER NOT NULL DEFAULT 0,
    Invoices INTEGER NOT NULL DEFAULT 0,
    Amount   REAL    NOT NULL DEFAULT 0,
    ChanceInitial REAL NOT NULL DEFAULT 0,
    ChanceAtPick  REAL NOT NULL DEFAULT 0,
    Prize         TEXT NOT NULL DEFAULT '',
    PRIMARY KEY(DrawId, Rank)
);");

            Exec("INSERT OR IGNORE INTO Meta(Key, Value) VALUES('SchemaVersion', '1');");

            // مهاجرت نرم: ستون‌های جدید به دیتابیس‌های قبلی اضافه می‌شوند (بدون از دست رفتن داده)
            EnsureColumn("DrawWinners", "Prize", "TEXT NOT NULL DEFAULT ''");
            EnsureColumn("Draws", "Prizes", "TEXT NOT NULL DEFAULT ''");
            EnsureColumn("Draws", "Scope", "TEXT NOT NULL DEFAULT ''");

            Exec("UPDATE Meta SET Value='3' WHERE Key='SchemaVersion' AND CAST(Value AS INTEGER) < 3;");
        }

        /// <summary>افزودن ستون در صورت نبودن (برای ارتقای دیتابیس‌های قبلی).</summary>
        private void EnsureColumn(string table, string column, string definition)
        {
            bool exists = false;
            using (SQLiteCommand cmd = new SQLiteCommand("PRAGMA table_info(" + table + ")", _cn))
            using (SQLiteDataReader r = cmd.ExecuteReader())
            {
                while (r.Read())
                {
                    if (string.Equals(S(r, 1), column, StringComparison.OrdinalIgnoreCase)) { exists = true; break; }
                }
            }
            if (!exists) Exec("ALTER TABLE " + table + " ADD COLUMN " + column + " " + definition);
        }

        // ==================================================================
        //  ذخیره و به‌روزرسانی فاکتور
        // ==================================================================
        /// <summary>
        /// ثبت/به‌روزرسانی فاکتور در دیتابیس محلی و محاسبه امتیاز آن.
        /// موبایل اگر در هولو (Alias_Mobile) باشد استفاده می‌شود، وگرنه موبایلی که قبلاً در برنامه ثبت شده حفظ می‌شود.
        /// </summary>
        public StoredInvoice SaveInvoice(InvoiceInfo inv, double offeredDiscount, AppSettings s)
        {
            if (inv == null || inv.FCode <= 0) return null;
            lock (_gate)
            {
                try
                {
                    if (!EnsureOpen()) return null;

                    StoredInvoice old = Read(inv.FCode);
                    string mobile = PhoneUtil.Normalize(inv.Mobile);
                    if (mobile.Length == 0 && old != null) mobile = old.Mobile;

                    double holooDiscount = inv.Discount;
                    double net = inv.Gross - holooDiscount;
                    if (net < 0) net = 0;
                    bool enabled = s == null || s.PointsEnabled;
                    double baseAmount = PointsEngine.BaseAmount(s, inv.Gross, holooDiscount);
                    int points = (enabled && mobile.Length > 0)
                        ? PointsEngine.ForAmount(baseAmount, s == null ? 500000 : s.PointsPerAmountToman)
                        : 0;

                    string now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
                    string alias = inv.CustomerAlias == null ? "" : inv.CustomerAlias.Trim();

                    if (old == null)
                    {
                        using (SQLiteCommand cmd = new SQLiteCommand(@"
INSERT INTO Invoices(FCode, FCodeC, FDate, Jalali, FTime, UserCode, Cashier, Gross, HolooDiscount, Net,
                     Mobile, CustomerCode, CustomerAlias, Points, OfferedDiscount, FirstSeen, UpdatedAt)
VALUES(@f, @fc, @d, @j, @t, @u, @c, @g, @hd, @n, @m, @cc, @an, @p, @od, @now, @now)", _cn))
                        {
                            cmd.Parameters.AddWithValue("@f", inv.FCode);
                            cmd.Parameters.AddWithValue("@fc", inv.FCodeC);
                            cmd.Parameters.AddWithValue("@d", inv.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                            cmd.Parameters.AddWithValue("@j", inv.JalaliDate == null ? "" : inv.JalaliDate.Trim());
                            cmd.Parameters.AddWithValue("@t", inv.Time == null ? "" : inv.Time.Trim());
                            cmd.Parameters.AddWithValue("@u", inv.UserCode);
                            cmd.Parameters.AddWithValue("@c", inv.CashierName == null ? "" : inv.CashierName.Trim());
                            cmd.Parameters.AddWithValue("@g", inv.Gross);
                            cmd.Parameters.AddWithValue("@hd", holooDiscount);
                            cmd.Parameters.AddWithValue("@n", net);
                            cmd.Parameters.AddWithValue("@m", mobile);
                            cmd.Parameters.AddWithValue("@cc", inv.CustomerCode == null ? "" : inv.CustomerCode.Trim());
                            cmd.Parameters.AddWithValue("@an", alias);
                            cmd.Parameters.AddWithValue("@p", points);
                            cmd.Parameters.AddWithValue("@od", offeredDiscount);
                            cmd.Parameters.AddWithValue("@now", now);
                            cmd.ExecuteNonQuery();
                        }
                    }
                    else
                    {
                        using (SQLiteCommand cmd = new SQLiteCommand(@"
UPDATE Invoices SET FCodeC=@fc, FDate=@d, Jalali=@j, FTime=@t, UserCode=@u, Cashier=@c,
       Gross=@g, HolooDiscount=@hd, Net=@n, Mobile=@m, CustomerCode=@cc, CustomerAlias=@an,
       Points=@p, OfferedDiscount=@od, UpdatedAt=@now
WHERE FCode=@f", _cn))
                        {
                            cmd.Parameters.AddWithValue("@fc", inv.FCodeC);
                            cmd.Parameters.AddWithValue("@d", inv.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                            cmd.Parameters.AddWithValue("@j", inv.JalaliDate == null ? "" : inv.JalaliDate.Trim());
                            cmd.Parameters.AddWithValue("@t", inv.Time == null ? "" : inv.Time.Trim());
                            cmd.Parameters.AddWithValue("@u", inv.UserCode);
                            cmd.Parameters.AddWithValue("@c", inv.CashierName == null ? "" : inv.CashierName.Trim());
                            cmd.Parameters.AddWithValue("@g", inv.Gross);
                            cmd.Parameters.AddWithValue("@hd", holooDiscount);
                            cmd.Parameters.AddWithValue("@n", net);
                            cmd.Parameters.AddWithValue("@m", mobile);
                            cmd.Parameters.AddWithValue("@cc", inv.CustomerCode == null ? "" : inv.CustomerCode.Trim());
                            cmd.Parameters.AddWithValue("@an", alias);
                            cmd.Parameters.AddWithValue("@p", points);
                            cmd.Parameters.AddWithValue("@od", offeredDiscount);
                            cmd.Parameters.AddWithValue("@now", now);
                            cmd.Parameters.AddWithValue("@f", inv.FCode);
                            cmd.ExecuteNonQuery();
                        }
                    }

                    if (mobile.Length > 0) TouchMobile(mobile, alias, now);
                    return Read(inv.FCode);
                }
                catch (Exception ex)
                {
                    Fail("SaveInvoice", ex);
                    return null;
                }
            }
        }

        /// <summary>ثبت/تغییر شماره موبایل یک فاکتور و به‌روزرسانی امتیاز آن.</summary>
        public StoredInvoice SetMobile(long fCode, string mobileRaw, string name, AppSettings s)
        {
            lock (_gate)
            {
                try
                {
                    if (!EnsureOpen()) return null;
                    StoredInvoice inv = Read(fCode);
                    if (inv == null) return null;

                    string mobile = PhoneUtil.Normalize(mobileRaw);
                    bool enabled = s == null || s.PointsEnabled;
                    double baseAmount = PointsEngine.BaseAmount(s, inv.Gross, inv.HolooDiscount);
                    int points = (enabled && mobile.Length > 0)
                        ? PointsEngine.ForAmount(baseAmount, s == null ? 500000 : s.PointsPerAmountToman)
                        : 0;

                    string now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
                    using (SQLiteCommand cmd = new SQLiteCommand(
                        "UPDATE Invoices SET Mobile=@m, Points=@p, UpdatedAt=@now WHERE FCode=@f", _cn))
                    {
                        cmd.Parameters.AddWithValue("@m", mobile);
                        cmd.Parameters.AddWithValue("@p", points);
                        cmd.Parameters.AddWithValue("@now", now);
                        cmd.Parameters.AddWithValue("@f", fCode);
                        cmd.ExecuteNonQuery();
                    }
                    if (mobile.Length > 0) TouchMobile(mobile, name, now);
                    return Read(fCode);
                }
                catch (Exception ex)
                {
                    Fail("SetMobile", ex);
                    return null;
                }
            }
        }

        /// <summary>ثبت/به‌روزرسانی یک شماره در فهرست شماره‌ها (بدون خطا در صورت تکراری بودن).</summary>
        private void TouchMobile(string mobile, string name, string now)
        {
            using (SQLiteCommand cmd = new SQLiteCommand(@"
INSERT OR IGNORE INTO Mobiles(Mobile, Name, Note, FirstSeen, LastSeen) VALUES(@m, @n, '', @now, @now)", _cn))
            {
                cmd.Parameters.AddWithValue("@m", mobile);
                cmd.Parameters.AddWithValue("@n", name == null ? "" : name);
                cmd.Parameters.AddWithValue("@now", now);
                int n = cmd.ExecuteNonQuery();
                if (n > 0) return;
            }
            using (SQLiteCommand upd = new SQLiteCommand(@"
UPDATE Mobiles SET LastSeen=@now, Name=CASE WHEN @n <> '' THEN @n ELSE Name END WHERE Mobile=@m", _cn))
            {
                upd.Parameters.AddWithValue("@now", now);
                upd.Parameters.AddWithValue("@n", name == null ? "" : name);
                upd.Parameters.AddWithValue("@m", mobile);
                upd.ExecuteNonQuery();
            }
        }

        // ==================================================================
        //  خواندن
        // ==================================================================
        public StoredInvoice Read(long fCode)
        {
            lock (_gate)
            {
                try
                {
                    if (!EnsureOpen()) return null;
                    using (SQLiteCommand cmd = new SQLiteCommand(
                        "SELECT " + Cols + " FROM Invoices WHERE FCode=@f", _cn))
                    {
                        cmd.Parameters.AddWithValue("@f", fCode);
                        using (SQLiteDataReader r = cmd.ExecuteReader())
                            return r.Read() ? ReadRow(r) : null;
                    }
                }
                catch (Exception ex)
                {
                    Fail("Read", ex);
                    return null;
                }
            }
        }

        /// <summary>همه فاکتورهای محلی با شناسه بزرگ‌تر یا مساوی minFCode (کلید = FCode).</summary>
        public Dictionary<long, StoredInvoice> ReadFrom(long minFCode)
        {
            Dictionary<long, StoredInvoice> map = new Dictionary<long, StoredInvoice>();
            lock (_gate)
            {
                try
                {
                    if (!EnsureOpen()) return map;
                    using (SQLiteCommand cmd = new SQLiteCommand(
                        "SELECT " + Cols + " FROM Invoices WHERE FCode >= @f", _cn))
                    {
                        cmd.Parameters.AddWithValue("@f", minFCode);
                        using (SQLiteDataReader r = cmd.ExecuteReader())
                            while (r.Read())
                            {
                                StoredInvoice v = ReadRow(r);
                                map[v.FCode] = v;
                            }
                    }
                }
                catch (Exception ex) { Fail("ReadFrom", ex); }
            }
            return map;
        }

        /// <summary>مجموع امتیاز فاکتورهای یک روز (میلادی).</summary>
        public int SumPointsOnDay(DateTime day)
        {
            lock (_gate)
            {
                try
                {
                    if (!EnsureOpen()) return 0;
                    using (SQLiteCommand cmd = new SQLiteCommand(
                        "SELECT IFNULL(SUM(Points),0) FROM Invoices WHERE FDate=@d", _cn))
                    {
                        cmd.Parameters.AddWithValue("@d", day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                        return I(cmd.ExecuteScalar());
                    }
                }
                catch (Exception ex) { Fail("SumPointsOnDay", ex); return 0; }
            }
        }

        /// <summary>تعداد فاکتورهای یک روز که شماره موبایل دارند.</summary>
        public int CountWithMobileOnDay(DateTime day)
        {
            lock (_gate)
            {
                try
                {
                    if (!EnsureOpen()) return 0;
                    using (SQLiteCommand cmd = new SQLiteCommand(
                        "SELECT COUNT(*) FROM Invoices WHERE Mobile <> '' AND FDate=@d", _cn))
                    {
                        cmd.Parameters.AddWithValue("@d", day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                        return I(cmd.ExecuteScalar());
                    }
                }
                catch (Exception ex) { Fail("CountWithMobileOnDay", ex); return 0; }
            }
        }

        /// <summary>جست‌وجوی فاکتورها با شماره موبایل (ارقام فارسی، فاصله، +98 و 0098 هم پذیرفته می‌شود).</summary>
        public List<StoredInvoice> FindByMobile(string input, int max)
        {
            List<StoredInvoice> list = new List<StoredInvoice>();
            string digits = PhoneUtil.Digits(input);
            string norm = PhoneUtil.Normalize(input);
            if (digits.Length == 0 && norm.Length == 0) return list;
            lock (_gate)
            {
                try
                {
                    if (!EnsureOpen()) return list;
                    if (max <= 0) max = 500;
                    using (SQLiteCommand cmd = new SQLiteCommand(
                        "SELECT " + Cols + " FROM Invoices WHERE Mobile <> '' AND (Mobile LIKE @norm OR Mobile LIKE @digits) "
                        + "ORDER BY FCode DESC LIMIT @n", _cn))
                    {
                        cmd.Parameters.AddWithValue("@norm", "%" + norm + "%");
                        cmd.Parameters.AddWithValue("@digits", "%" + digits + "%");
                        cmd.Parameters.AddWithValue("@n", max);
                        using (SQLiteDataReader r = cmd.ExecuteReader())
                            while (r.Read()) list.Add(ReadRow(r));
                    }
                }
                catch (Exception ex) { Fail("FindByMobile", ex); }
            }
            return list;
        }

        /// <summary>خلاصه هر شماره‌ای که با عبارت جست‌وجو مطابقت دارد (امتیاز، تعداد فاکتور، جمع خرید).</summary>
        public List<PhoneProfile> ProfilesByQuery(string input, int max)
        {
            List<PhoneProfile> list = new List<PhoneProfile>();
            string digits = PhoneUtil.Digits(input);
            string norm = PhoneUtil.Normalize(input);
            if (digits.Length == 0 && norm.Length == 0) return list;
            lock (_gate)
            {
                try
                {
                    if (!EnsureOpen()) return list;
                    if (max <= 0) max = 50;
                    using (SQLiteCommand cmd = new SQLiteCommand(@"
SELECT i.Mobile, IFNULL(m.Name,'') AS Name, SUM(i.Points) AS P, COUNT(*) AS C, SUM(i.Net) AS A,
       SUM(i.HolooDiscount) AS D, MIN(i.Jalali) AS F, MAX(i.Jalali) AS L
FROM Invoices i LEFT JOIN Mobiles m ON m.Mobile = i.Mobile
WHERE (i.Mobile LIKE @norm OR i.Mobile LIKE @digits)
GROUP BY i.Mobile
ORDER BY SUM(i.Points) DESC, SUM(i.Net) DESC
LIMIT @n", _cn))
                    {
                        cmd.Parameters.AddWithValue("@norm", "%" + norm + "%");
                        cmd.Parameters.AddWithValue("@digits", "%" + digits + "%");
                        cmd.Parameters.AddWithValue("@n", max);
                        using (SQLiteDataReader r = cmd.ExecuteReader())
                            while (r.Read())
                            {
                                PhoneProfile p = new PhoneProfile();
                                p.Mobile = S(r, 0);
                                p.Name = S(r, 1);
                                p.Points = I(r, 2);
                                p.Count = I(r, 3);
                                p.Amount = D(r, 4);
                                p.Discount = D(r, 5);
                                p.First = S(r, 6);
                                p.Last = S(r, 7);
                                list.Add(p);
                            }
                    }
                }
                catch (Exception ex) { Fail("ProfilesByQuery", ex); }
            }
            return list;
        }

        /// <summary>فهرست همه شماره‌دارها به ترتیب امتیاز (برای قرعه‌کشی و گزارش).</summary>
        public List<PhoneProfile> GetPointsTable(int max)
        {
            return GetPointsTableOnDays("", "", max);
        }

        /// <summary>فهرست شماره‌دارها با محدودیت بازه تاریخ فاکتور (میلادی yyyy-MM-dd؛ خالی = بی‌محدودیت).</summary>
        public List<PhoneProfile> GetPointsTableOnDays(string fromYmd, string toYmd, int max)
        {
            List<PhoneProfile> list = new List<PhoneProfile>();
            lock (_gate)
            {
                try
                {
                    if (!EnsureOpen()) return list;
                    if (max <= 0) max = 100000;
                    using (SQLiteCommand cmd = new SQLiteCommand(@"
SELECT i.Mobile, IFNULL(m.Name,'') AS Name, SUM(i.Points) AS P, COUNT(*) AS C, SUM(i.Net) AS A,
       SUM(i.HolooDiscount) AS D, MIN(i.Jalali) AS F, MAX(i.Jalali) AS L
FROM Invoices i LEFT JOIN Mobiles m ON m.Mobile = i.Mobile
WHERE i.Mobile <> ''
  AND (@from = '' OR i.FDate >= @from)
  AND (@to   = '' OR i.FDate <= @to)
GROUP BY i.Mobile
ORDER BY SUM(i.Points) DESC, SUM(i.Net) DESC
LIMIT @n", _cn))
                    {
                        cmd.Parameters.AddWithValue("@from", fromYmd == null ? "" : fromYmd.Trim());
                        cmd.Parameters.AddWithValue("@to", toYmd == null ? "" : toYmd.Trim());
                        cmd.Parameters.AddWithValue("@n", max);
                        using (SQLiteDataReader r = cmd.ExecuteReader())
                            while (r.Read())
                            {
                                PhoneProfile p = new PhoneProfile();
                                p.Mobile = S(r, 0);
                                p.Name = S(r, 1);
                                p.Points = I(r, 2);
                                p.Count = I(r, 3);
                                p.Amount = D(r, 4);
                                p.Discount = D(r, 5);
                                p.First = S(r, 6);
                                p.Last = S(r, 7);
                                list.Add(p);
                            }
                    }
                }
                catch (Exception ex) { Fail("GetPointsTableOnDays", ex); }
            }
            return list;
        }

        /// <summary>
        /// فهرست امتیاز شماره‌داران با **فیلتر دسته** (برای قرعه‌کشی محدود):
        /// بازه تاریخ فاکتور، صندوق‌دار خاص (۰ = همه) و حداقل تعداد فاکتور.
        /// </summary>
        public List<PhoneProfile> GetPointsTableFiltered(string fromYmd, string toYmd, int userCode, int minInvoices, int max)
        {
            List<PhoneProfile> list = new List<PhoneProfile>();
            lock (_gate)
            {
                try
                {
                    if (!EnsureOpen()) return list;
                    if (max <= 0) max = 100000;
                    if (minInvoices < 1) minInvoices = 1;
                    using (SQLiteCommand cmd = new SQLiteCommand(@"
SELECT i.Mobile, IFNULL(m.Name,'') AS Name, SUM(i.Points) AS P, COUNT(*) AS C, SUM(i.Net) AS A,
       SUM(i.HolooDiscount) AS D, MIN(i.Jalali) AS F, MAX(i.Jalali) AS L
FROM Invoices i LEFT JOIN Mobiles m ON m.Mobile = i.Mobile
WHERE i.Mobile <> ''
  AND (@from = '' OR i.FDate >= @from)
  AND (@to   = '' OR i.FDate <= @to)
  AND (@user = 0  OR i.UserCode = @user)
GROUP BY i.Mobile
HAVING COUNT(*) >= @minc
ORDER BY SUM(i.Points) DESC, SUM(i.Net) DESC
LIMIT @n", _cn))
                    {
                        cmd.Parameters.AddWithValue("@from", fromYmd == null ? "" : fromYmd.Trim());
                        cmd.Parameters.AddWithValue("@to", toYmd == null ? "" : toYmd.Trim());
                        cmd.Parameters.AddWithValue("@user", userCode);
                        cmd.Parameters.AddWithValue("@minc", minInvoices);
                        cmd.Parameters.AddWithValue("@n", max);
                        using (SQLiteDataReader r = cmd.ExecuteReader())
                            while (r.Read())
                            {
                                PhoneProfile p = new PhoneProfile();
                                p.Mobile = S(r, 0);
                                p.Name = S(r, 1);
                                p.Points = I(r, 2);
                                p.Count = I(r, 3);
                                p.Amount = D(r, 4);
                                p.Discount = D(r, 5);
                                p.First = S(r, 6);
                                p.Last = S(r, 7);
                                list.Add(p);
                            }
                    }
                }
                catch (Exception ex) { Fail("GetPointsTableFiltered", ex); }
            }
            return list;
        }

        /// <summary>صندوق‌دارهای دیده‌شده در دیتابیس محلی (برای محدود کردن قرعه‌کشی).</summary>
        public List<CashierChoice> LocalCashiers()
        {
            List<CashierChoice> list = new List<CashierChoice>();
            lock (_gate)
            {
                try
                {
                    if (!EnsureOpen()) return list;
                    using (SQLiteCommand cmd = new SQLiteCommand(@"
SELECT UserCode, MAX(Cashier) AS N, COUNT(*) AS C
FROM Invoices
WHERE Mobile <> ''
GROUP BY UserCode
ORDER BY MAX(Cashier)", _cn))
                    using (SQLiteDataReader r = cmd.ExecuteReader())
                        while (r.Read())
                        {
                            CashierChoice c = new CashierChoice();
                            c.UserCode = I(r, 0);
                            c.Name = S(r, 1);
                            c.Invoices = I(r, 2);
                            list.Add(c);
                        }
                }
                catch (Exception ex) { Fail("LocalCashiers", ex); }
            }
            return list;
        }

        /// <summary>آخرین شماره موبایلی که برای این کد مشتری ثبت شده (پیشنهاد خودکار در پاپ‌آپ).</summary>
        public string SuggestMobileForCustomer(string customerCode)
        {
            string cc = customerCode == null ? "" : customerCode.Trim();
            if (cc.Length == 0 || cc == "0" || cc == "00000" || cc == "00001") return "";
            lock (_gate)
            {
                try
                {
                    if (!EnsureOpen()) return "";
                    using (SQLiteCommand cmd = new SQLiteCommand(
                        "SELECT Mobile FROM Invoices WHERE CustomerCode=@cc AND Mobile <> '' ORDER BY FCode DESC LIMIT 1", _cn))
                    {
                        cmd.Parameters.AddWithValue("@cc", cc);
                        object o = cmd.ExecuteScalar();
                        return o == null || o == DBNull.Value ? "" : Convert.ToString(o);
                    }
                }
                catch (Exception ex) { Fail("SuggestMobileForCustomer", ex); return ""; }
            }
        }

        /// <summary>فاکتورهای بدون شماره موبایل (برای تکمیل دستی).</summary>
        public List<StoredInvoice> GetWithoutMobile(int max)
        {
            List<StoredInvoice> list = new List<StoredInvoice>();
            lock (_gate)
            {
                try
                {
                    if (!EnsureOpen()) return list;
                    if (max <= 0) max = 200;
                    using (SQLiteCommand cmd = new SQLiteCommand(
                        "SELECT " + Cols + " FROM Invoices WHERE Mobile = '' ORDER BY FCode DESC LIMIT @n", _cn))
                    {
                        cmd.Parameters.AddWithValue("@n", max);
                        using (SQLiteDataReader r = cmd.ExecuteReader())
                            while (r.Read()) list.Add(ReadRow(r));
                    }
                }
                catch (Exception ex) { Fail("GetWithoutMobile", ex); }
            }
            return list;
        }

        public int CountWithoutMobile()
        {
            lock (_gate)
            {
                try
                {
                    if (!EnsureOpen()) return 0;
                    using (SQLiteCommand cmd = new SQLiteCommand("SELECT COUNT(*) FROM Invoices WHERE Mobile = ''", _cn))
                        return I(cmd.ExecuteScalar());
                }
                catch (Exception ex) { Fail("CountWithoutMobile", ex); return 0; }
            }
        }

        /// <summary>تعداد فاکتورهای یک روز مشخص (میلادی yyyy-MM-dd) که بدون موبایل مانده‌اند.</summary>
        public int CountWithoutMobileOnDay(DateTime day)
        {
            lock (_gate)
            {
                try
                {
                    if (!EnsureOpen()) return 0;
                    using (SQLiteCommand cmd = new SQLiteCommand(
                        "SELECT COUNT(*) FROM Invoices WHERE Mobile = '' AND FDate=@d", _cn))
                    {
                        cmd.Parameters.AddWithValue("@d", day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                        return I(cmd.ExecuteScalar());
                    }
                }
                catch (Exception ex) { Fail("CountWithoutMobileOnDay", ex); return 0; }
            }
        }

        public int CountInvoices()
        {
            lock (_gate)
            {
                try
                {
                    if (!EnsureOpen()) return 0;
                    using (SQLiteCommand cmd = new SQLiteCommand("SELECT COUNT(*) FROM Invoices", _cn))
                        return I(cmd.ExecuteScalar());
                }
                catch (Exception ex) { Fail("CountInvoices", ex); return 0; }
            }
        }

        public int CountMobiles()
        {
            lock (_gate)
            {
                try
                {
                    if (!EnsureOpen()) return 0;
                    using (SQLiteCommand cmd = new SQLiteCommand("SELECT COUNT(*) FROM Mobiles", _cn))
                        return I(cmd.ExecuteScalar());
                }
                catch (Exception ex) { Fail("CountMobiles", ex); return 0; }
            }
        }

        /// <summary>بازمحاسبه امتیاز همه فاکتورها با آستانه فعلی تنظیمات (برای وقتی آستانه تغییر می‌کند).</summary>
        public int RecalculatePoints(AppSettings s)
        {
            lock (_gate)
            {
                try
                {
                    if (!EnsureOpen()) return 0;
                    List<StoredInvoice> all = new List<StoredInvoice>();
                    using (SQLiteCommand cmd = new SQLiteCommand("SELECT " + Cols + " FROM Invoices", _cn))
                    using (SQLiteDataReader r = cmd.ExecuteReader())
                        while (r.Read()) all.Add(ReadRow(r));

                    int changed = 0;
                    using (SQLiteTransaction tx = _cn.BeginTransaction())
                    {
                        for (int i = 0; i < all.Count; i++)
                        {
                            StoredInvoice inv = all[i];
                            double baseAmount = PointsEngine.BaseAmount(s, inv.Gross, inv.HolooDiscount);
                            int p = (s.PointsEnabled && inv.Mobile.Length > 0)
                                ? PointsEngine.ForAmount(baseAmount, s.PointsPerAmountToman) : 0;
                            if (p == inv.Points) continue;
                            using (SQLiteCommand cmd = new SQLiteCommand(
                                "UPDATE Invoices SET Points=@p, UpdatedAt=@now WHERE FCode=@f", _cn, tx))
                            {
                                cmd.Parameters.AddWithValue("@p", p);
                                cmd.Parameters.AddWithValue("@now", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
                                cmd.Parameters.AddWithValue("@f", inv.FCode);
                                cmd.ExecuteNonQuery();
                            }
                            changed++;
                        }
                        tx.Commit();
                    }
                    return changed;
                }
                catch (Exception ex) { Fail("RecalculatePoints", ex); return 0; }
            }
        }

        // ==================================================================
        //  قرعه‌کشی: ذخیره نتیجه و خروجی
        // ==================================================================
        /// <summary>ذخیره یک قرعه‌کشی و برندگانش؛ خروجی: شناسه قرعه‌کشی (۰ = ناموفق).</summary>
        public int SaveDraw(LotteryResult r)
        {
            if (r == null) return 0;
            lock (_gate)
            {
                try
                {
                    if (!EnsureOpen()) return 0;
                    long id;
                    using (SQLiteCommand cmd = new SQLiteCommand(@"
INSERT INTO Draws(At, TypeName, Seed, Winners, Participants, TotalTickets, MinPoints, FromYmd, ToYmd, Prizes, Scope)
VALUES(@at, @t, @seed, @w, @p, @tt, @mp, @from, @to, @prizes, @scope)", _cn))
                    {
                        cmd.Parameters.AddWithValue("@prizes", r.PrizesText());
                        cmd.Parameters.AddWithValue("@scope", r.Scope == null ? "" : r.Scope);
                        cmd.Parameters.AddWithValue("@at", r.At.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
                        cmd.Parameters.AddWithValue("@t", r.TypeName == null ? "" : r.TypeName);
                        cmd.Parameters.AddWithValue("@seed", r.Seed);
                        cmd.Parameters.AddWithValue("@w", r.Winners.Count);
                        cmd.Parameters.AddWithValue("@p", r.Participants);
                        cmd.Parameters.AddWithValue("@tt", r.TotalTickets);
                        cmd.Parameters.AddWithValue("@mp", r.MinPoints);
                        cmd.Parameters.AddWithValue("@from", r.FromYmd == null ? "" : r.FromYmd);
                        cmd.Parameters.AddWithValue("@to", r.ToYmd == null ? "" : r.ToYmd);
                        cmd.ExecuteNonQuery();
                    }
                    using (SQLiteCommand cmd2 = new SQLiteCommand("SELECT last_insert_rowid()", _cn))
                        id = Convert.ToInt64(cmd2.ExecuteScalar(), CultureInfo.InvariantCulture);

                    for (int i = 0; i < r.Winners.Count; i++)
                    {
                        Winner w = r.Winners[i];
                        using (SQLiteCommand cmd3 = new SQLiteCommand(@"
INSERT OR REPLACE INTO DrawWinners(DrawId, Rank, Mobile, Name, Points, Invoices, Amount, ChanceInitial, ChanceAtPick, Prize)
VALUES(@id, @rank, @m, @n, @p, @c, @a, @ci, @cp, @pr)", _cn))
                        {
                            cmd3.Parameters.AddWithValue("@id", id);
                            cmd3.Parameters.AddWithValue("@pr", w.Prize == null ? "" : w.Prize);
                            cmd3.Parameters.AddWithValue("@rank", w.Rank);
                            cmd3.Parameters.AddWithValue("@m", w.Mobile == null ? "" : w.Mobile);
                            cmd3.Parameters.AddWithValue("@n", w.Name == null ? "" : w.Name);
                            cmd3.Parameters.AddWithValue("@p", w.Points);
                            cmd3.Parameters.AddWithValue("@c", w.Invoices);
                            cmd3.Parameters.AddWithValue("@a", w.Amount);
                            cmd3.Parameters.AddWithValue("@ci", w.ChanceInitial);
                            cmd3.Parameters.AddWithValue("@cp", w.ChanceAtPick);
                            cmd3.ExecuteNonQuery();
                        }
                    }
                    return (int)id;
                }
                catch (Exception ex) { Fail("SaveDraw", ex); return 0; }
            }
        }

        public int CountDraws()
        {
            lock (_gate)
            {
                try
                {
                    if (!EnsureOpen()) return 0;
                    using (SQLiteCommand cmd = new SQLiteCommand("SELECT COUNT(*) FROM Draws", _cn))
                        return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
                }
                catch (Exception ex) { Fail("CountDraws", ex); return 0; }
            }
        }

        /// <summary>
        /// خروجی CSV یک قرعه‌کشی: سطرهای برنده به ترتیب، سپس بقیه شرکت‌کنندگان (برای شفافیت و مستندسازی).
        /// ستون «شانس در قرعه‌کشی» = امتیاز/مجموع امتیازها و «شانس در لحظه انتخاب» = شانس در زمان برد.
        /// </summary>
        public string ExportDrawCsv(string path, LotteryResult r, int drawId)
        {
            if (r == null) throw new ArgumentNullException("r");
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("نوع;جایزه;رتبه;شماره موبایل;نام;امتیاز (شانس);شانس در قرعه‌کشی;شانس در لحظه انتخاب;تعداد فاکتور;جمع خرید (ریال)");
            sb.AppendLine("#;" + Q(r.PrizesText()) + ";" + Q("شناسه " + drawId) + ";" + Q("seed " + r.Seed) + ";" + r.Participants
                + ";مجموع شانس: " + r.TotalTickets + ";" + Q(FmtDate.G(r.At)) + ";" + r.Winners.Count + ";" + Q(r.TypeName)
                + ";" + Q(r.Scope == null ? "" : r.Scope));

            for (int i = 0; i < r.Winners.Count; i++)
            {
                Winner w = r.Winners[i];
                sb.AppendLine(Row("برنده", w.Prize, w.Rank, w.Mobile, w.Name, w.Points, w.ChanceInitial, w.ChanceAtPick, w.Invoices, w.Amount));
            }
            for (int i = 0; i < r.Tickets.Count; i++)
            {
                Ticket t = r.Tickets[i];
                bool isWinner = false;
                for (int j = 0; j < r.Winners.Count; j++)
                    if (r.Winners[j].Mobile == t.Mobile) { isWinner = true; break; }
                if (isWinner) continue;
                sb.AppendLine(Row("شرکت‌کننده", "", 0, t.Mobile, t.Name, t.Points, t.ChanceInitial, 0, t.Invoices, t.Amount));
            }

            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
            return path;
        }

        private static string Row(string kind, string prize, int rank, string mobile, string name, int points,
            double chanceInitial, double chanceAtPick, int invoices, double amount)
        {
            return string.Join(";", new string[] {
                kind,
                Q(prize),
                rank <= 0 ? "" : rank.ToString(CultureInfo.InvariantCulture),
                mobile == null ? "" : mobile,
                Q(name),
                points.ToString(CultureInfo.InvariantCulture),
                Pct(chanceInitial),
                chanceAtPick <= 0 ? "" : Pct(chanceAtPick),
                invoices.ToString(CultureInfo.InvariantCulture),
                Num.F(amount) });
        }

        private static string Pct(double v)
        {
            return (v * 100.0).ToString("0.###", CultureInfo.InvariantCulture) + "٪";
        }

        /// <summary>خروجی CSV فهرست امتیازات (سازگار با اکسل فارسی، UTF-8 با BOM).</summary>
        public string ExportPointsCsv(string path)
        {
            return ExportPointsCsv(path, "", "");
        }

        /// <summary>خروجی CSV فهرست امتیازات با محدودیت بازه تاریخ (میلادی yyyy-MM-dd).</summary>
        public string ExportPointsCsv(string path, string fromYmd, string toYmd)
        {
            List<PhoneProfile> rows = GetPointsTableOnDays(fromYmd, toYmd, 0);
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("شماره موبایل;نام;امتیاز;تعداد فاکتور;جمع خرید (ریال);جمع تخفیف (ریال);اولین خرید;آخرین خرید");
            for (int i = 0; i < rows.Count; i++)
            {
                PhoneProfile p = rows[i];
                sb.AppendLine(string.Join(";", new string[] {
                    p.Mobile, Q(p.Name), p.Points.ToString(CultureInfo.InvariantCulture),
                    p.Count.ToString(CultureInfo.InvariantCulture), Num.F(p.Amount), Num.F(p.Discount),
                    Q(p.First), Q(p.Last) }));
            }
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
            return path;
        }

        private static string Q(string v)
        {
            if (string.IsNullOrEmpty(v)) return "";
            string t = v.Replace("\"", "\"\"");
            if (t.IndexOf(';') >= 0 || t.IndexOf('"') >= 0 || t.IndexOf('\n') >= 0 || t.IndexOf('\r') >= 0)
                t = "\"" + t + "\"";
            return t;
        }

        // ==================================================================
        //  کمکی‌های داخلی
        // ==================================================================
        private const string Cols = "FCode, FCodeC, FDate, Jalali, FTime, UserCode, Cashier, Gross, "
            + "HolooDiscount, Net, Mobile, CustomerCode, CustomerAlias, Points, OfferedDiscount, FirstSeen, UpdatedAt";

        private static StoredInvoice ReadRow(SQLiteDataReader r)
        {
            StoredInvoice s = new StoredInvoice();
            s.FCode = L(r, 0);
            s.FCodeC = L(r, 1);
            s.Date = S(r, 2);
            s.Jalali = S(r, 3);
            s.Time = S(r, 4);
            s.UserCode = I(r, 5);
            s.Cashier = S(r, 6);
            s.Gross = D(r, 7);
            s.HolooDiscount = D(r, 8);
            s.Net = D(r, 9);
            s.Mobile = S(r, 10);
            s.CustomerCode = S(r, 11);
            s.CustomerAlias = S(r, 12);
            s.Points = I(r, 13);
            s.OfferedDiscount = D(r, 14);
            s.FirstSeen = S(r, 15);
            s.UpdatedAt = S(r, 16);
            return s;
        }

        private static string S(SQLiteDataReader r, int i)
        {
            if (r.IsDBNull(i)) return "";
            object o = r.GetValue(i);
            return o == null ? "" : Convert.ToString(o, CultureInfo.InvariantCulture).Trim();
        }

        private static double D(SQLiteDataReader r, int i)
        {
            if (r.IsDBNull(i)) return 0;
            return Convert.ToDouble(r.GetValue(i), CultureInfo.InvariantCulture);
        }

        private static long L(SQLiteDataReader r, int i)
        {
            if (r.IsDBNull(i)) return 0;
            return Convert.ToInt64(r.GetValue(i), CultureInfo.InvariantCulture);
        }

        private static int I(SQLiteDataReader r, int i) { return (int)L(r, i); }

        private static int I(object o)
        {
            if (o == null || o == DBNull.Value) return 0;
            return Convert.ToInt32(o, CultureInfo.InvariantCulture);
        }

        private void Exec(string sql)
        {
            using (SQLiteCommand cmd = new SQLiteCommand(sql, _cn)) cmd.ExecuteNonQuery();
        }

        private bool EnsureOpen()
        {
            if (_cn != null) return true;
            string err;
            // داخل قفل؛ Open هم قفل می‌گیرد اما C# lock بازگشتی است
            return Open(out err);
        }

        private void Fail(string where, Exception ex)
        {
            LastError = Describe(ex);
            try { CsvLog.Error(Path.GetDirectoryName(_path), where + ": " + ex.ToString()); }
            catch (Exception) { }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                try
                {
                    if (_cn != null)
                    {
                        _cn.Close();
                        _cn.Dispose();
                        _cn = null;
                    }
                }
                catch (Exception) { }
            }
        }
    }
}
