using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Xml;

namespace PosDiscount
{
    /// <summary>یک پله تخفیف برای حالت «پله‌ای».</summary>
    public class DiscountTier
    {
        public double MinAmount;
        public double Percent;
        public double Amount;

        public DiscountTier() { }

        public DiscountTier(double minAmount, double percent, double amount)
        {
            MinAmount = minAmount;
            Percent = percent;
            Amount = amount;
        }
    }

    public class AppSettings
    {
        // ---------- اتصال به دیتابیس ----------
        public string Server = @".\SQLEXPRESS";
        public string Database = "Holoo1";
        public bool UseWindowsAuth = true;
        public string SqlUser = "sa";
        public string SqlPassword = "";
        public int ConnectTimeoutSeconds = 5;

        // ---------- پلینگ (لحظه‌ای) ----------
        public int PollIntervalMs = 1500;
        public bool SkipExistingOnStart = true;
        public int LastCashierCode = 0;

        // ---------- تخفیف ----------
        public bool DiscountEnabled = true;
        public string DiscountMode = "percent";     // percent | fixed | tiered
        public double Percent = 5;
        public double FixedAmount = 10000;
        public double MaxPercent = 10;              // سقف درصدی (۰ = بی‌سقف)
        public double MaxAmount = 100000;           // سقف ریالی هر فاکتور (۰ = بی‌سقف)
        public double MinInvoiceAmount = 500000;    // حداقل مبلغ فاکتور برای امکان تخفیف
        public double MaxDiscountPerDay = 0;        // سقف تخفیف روزانه این کاربر (۰ = بی‌سقف)
        public bool RequireDiscountPermission = false;  // فقط کاربران دارای مجوز ChangeTakhfif
        public double RoundTo = 1000;               // گرد کردن مبلغ قابل پرداخت (۰ = خاموش)

        // ---------- باشگاه مشتریان (امتیاز و موبایل) ----------
        // نکته: هیچ‌کدام از این اطلاعات در دیتابیس هولو نوشته نمی‌شود؛ فقط در دیتابیس محلی برنامه است.
        public bool PointsEnabled = true;
        public double PointsPerAmountToman = 500000;  // هر ۵۰۰٬۰۰۰ تومان خرید در یک فاکتور = ۱ امتیاز
        public string PointsBase = "gross";           // gross = جمع کل فاکتور | net = مبلغ قابل پرداخت
        public string LocalDbFile = "pos-discount.db";// فایل دیتابیس محلی (نسبی به پوشه برنامه)
        public bool CaptureMobileInPopup = true;      // کادر ثبت موبایل در پاپ‌آپ تخفیف
        public bool RoundInFavorOfCustomer = true;  // گرد به نفع مشتری (رو به پایین)
        public string StartTime = "00:00";          // بازه مجاز تخفیف (پیش‌فرض: تمام شبانه‌روز)
        public string EndTime = "23:59";
        public List<DiscountTier> Tiers = new List<DiscountTier>();

        // ---------- همگام‌سازی با سرور مرکزی کمپین ----------
        public bool SyncEnabled = false;
        public string ApiBaseUrl = "http://127.0.0.1:5080";
        public string ApiAgentId = "cashier-pc-1";
        public string ApiAgentSecret = "";
        public string CampaignStartDate = "2026-02-09";   // yyyy-MM-dd میلادی — باید با Campaign:StartDate سرور یکی باشد
        public int SyncIntervalSeconds = 30;
        public int SyncBatchSize = 100;
        public int ApiTimeoutSeconds = 20;

        // ---------- نمایش ----------
        public int AutoCloseSeconds = 30;
        public bool PlaySound = true;
        public string PopupCorner = "center";       // center | topRight | bottomRight | bottomLeft
        public bool AlwaysOnTop = true;
        public bool StartMinimizedToTray = false;
        public string LogFolder = "logs";

        /// <summary>مسیر فایل تنظیمات روی دیسک (بعد از Load پر می‌شود).</summary>
        public string FilePath = "";

        /// <summary>پوشه‌ای که فایل اجرایی و دیتابیس محلی در آن است.</summary>
        public string BaseDir
        {
            get
            {
                string dir = Path.GetDirectoryName(FilePath);
                return string.IsNullOrEmpty(dir) ? "." : dir;
            }
        }

        /// <summary>مسیر کامل فایل دیتابیس محلی (SQLite).</summary>
        public string LocalDbFullPath
        {
            get { return LocalStore.ResolveFile(BaseDir, LocalDbFile); }
        }

        public static AppSettings CreateDefault()
        {
            AppSettings s = new AppSettings();
            // نمونه پله‌های پیش‌فرض (قابل ویرایش در تنظیمات)
            s.Tiers.Add(new DiscountTier(500000, 2, 0));
            s.Tiers.Add(new DiscountTier(2000000, 3, 0));
            s.Tiers.Add(new DiscountTier(5000000, 5, 0));
            return s;
        }

        public string BuildConnectionString()
        {
            SqlConnectionStringBuilder b = new SqlConnectionStringBuilder();
            b.DataSource = Server;
            b.InitialCatalog = Database;
            b.IntegratedSecurity = UseWindowsAuth;
            if (!UseWindowsAuth)
            {
                b.UserID = SqlUser;
                b.Password = SqlPassword;
            }
            b.ConnectTimeout = Math.Max(2, ConnectTimeoutSeconds);
            b.ApplicationName = "PosDiscount";
            b.Pooling = true;
            TrySet(b, "Encrypt", "False");                 // سازگاری با SQL Serverهای قدیمی/ویندوز ۷
            TrySet(b, "TrustServerCertificate", "True");
            return b.ConnectionString;
        }

        private static void TrySet(SqlConnectionStringBuilder b, string key, string value)
        {
            try { b[key] = value; } catch (Exception) { /* کلید در این نسخه از SqlClient پشتیبانی نمی‌شود */ }
        }

        // ------------------------------------------------------------------
        //  XML
        // ------------------------------------------------------------------
        public static AppSettings Load(string path)
        {
            AppSettings s = CreateDefault();
            s.FilePath = path;
            if (!File.Exists(path))
            {
                s.Save();
                return s;
            }

            XmlDocument doc = new XmlDocument();
            try
            {
                doc.Load(path);
            }
            catch (Exception)
            {
                // فایل خراب → با نام دیگری کنار گذاشته می‌شود و پیش‌فرض ساخته می‌شود
                try { File.Move(path, path + ".bad"); } catch (Exception) { }
                s.Save();
                return s;
            }

            XmlNode root = doc.DocumentElement;
            if (root == null) { s.Save(); return s; }

            s.Server = GetS(root, "Server", s.Server);
            s.Database = GetS(root, "Database", s.Database);
            s.UseWindowsAuth = Num.ParseB(GetS(root, "UseWindowsAuth", s.UseWindowsAuth ? "true" : "false"), s.UseWindowsAuth);
            s.SqlUser = GetS(root, "SqlUser", s.SqlUser);
            s.SqlPassword = GetS(root, "SqlPassword", s.SqlPassword);
            s.ConnectTimeoutSeconds = Num.ParseI(GetS(root, "ConnectTimeoutSeconds", "5"), s.ConnectTimeoutSeconds);

            s.PollIntervalMs = Num.ParseI(GetS(root, "PollIntervalMs", "1500"), s.PollIntervalMs);
            s.SkipExistingOnStart = Num.ParseB(GetS(root, "SkipExistingOnStart", "true"), s.SkipExistingOnStart);
            s.LastCashierCode = Num.ParseI(GetS(root, "LastCashierCode", "0"), s.LastCashierCode);

            s.DiscountEnabled = Num.ParseB(GetS(root, "DiscountEnabled", "true"), s.DiscountEnabled);
            s.DiscountMode = GetS(root, "DiscountMode", s.DiscountMode).ToLowerInvariant();
            s.Percent = Num.ParseD(GetS(root, "Percent", "5"), s.Percent);
            s.FixedAmount = Num.ParseD(GetS(root, "FixedAmount", "10000"), s.FixedAmount);
            s.MaxPercent = Num.ParseD(GetS(root, "MaxPercent", "10"), s.MaxPercent);
            s.MaxAmount = Num.ParseD(GetS(root, "MaxAmount", "100000"), s.MaxAmount);
            s.MinInvoiceAmount = Num.ParseD(GetS(root, "MinInvoiceAmount", "500000"), s.MinInvoiceAmount);
            s.MaxDiscountPerDay = Num.ParseD(GetS(root, "MaxDiscountPerDay", "0"), s.MaxDiscountPerDay);
            s.RequireDiscountPermission = Num.ParseB(GetS(root, "RequireDiscountPermission", "false"), s.RequireDiscountPermission);
            s.RoundTo = Num.ParseD(GetS(root, "RoundTo", "1000"), s.RoundTo);

            s.PointsEnabled = Num.ParseB(GetS(root, "PointsEnabled", "true"), s.PointsEnabled);
            s.PointsPerAmountToman = Num.ParseD(GetS(root, "PointsPerAmountToman", "500000"), s.PointsPerAmountToman);
            s.PointsBase = GetS(root, "PointsBase", s.PointsBase).ToLowerInvariant();
            s.LocalDbFile = GetS(root, "LocalDbFile", s.LocalDbFile);
            s.CaptureMobileInPopup = Num.ParseB(GetS(root, "CaptureMobileInPopup", "true"), s.CaptureMobileInPopup);
            s.RoundInFavorOfCustomer = Num.ParseB(GetS(root, "RoundInFavorOfCustomer", "true"), s.RoundInFavorOfCustomer);
            s.StartTime = GetS(root, "StartTime", s.StartTime);
            s.EndTime = GetS(root, "EndTime", s.EndTime);

            s.Tiers = new List<DiscountTier>();
            XmlNode tiers = root.SelectSingleNode("Tiers");
            if (tiers != null)
            {
                foreach (XmlNode n in tiers.SelectNodes("Tier"))
                {
                    XmlElement e = n as XmlElement;
                    if (e == null) continue;
                    DiscountTier t = new DiscountTier();
                    t.MinAmount = Num.ParseD(e.GetAttribute("MinAmount"), 0);
                    t.Percent = Num.ParseD(e.GetAttribute("Percent"), 0);
                    t.Amount = Num.ParseD(e.GetAttribute("Amount"), 0);
                    s.Tiers.Add(t);
                }
            }

            s.SyncEnabled = Num.ParseB(GetS(root, "SyncEnabled", "false"), s.SyncEnabled);
            s.ApiBaseUrl = GetS(root, "ApiBaseUrl", s.ApiBaseUrl);
            s.ApiAgentId = GetS(root, "ApiAgentId", s.ApiAgentId);
            string protectedSecret = GetS(root, "ApiAgentSecretProtected", "");
            if (protectedSecret.Length > 0)
            {
                try { s.ApiAgentSecret = UnprotectSecret(protectedSecret); }
                catch (Exception) { s.ApiAgentSecret = GetS(root, "ApiAgentSecret", ""); }
            }
            else
            {
                s.ApiAgentSecret = GetS(root, "ApiAgentSecret", s.ApiAgentSecret);   // سازگاری با فایل قدیمی
            }
            s.CampaignStartDate = GetS(root, "CampaignStartDate", s.CampaignStartDate);
            s.SyncIntervalSeconds = Num.ParseI(GetS(root, "SyncIntervalSeconds", "30"), s.SyncIntervalSeconds);
            s.SyncBatchSize = Num.ParseI(GetS(root, "SyncBatchSize", "100"), s.SyncBatchSize);
            s.ApiTimeoutSeconds = Num.ParseI(GetS(root, "ApiTimeoutSeconds", "20"), s.ApiTimeoutSeconds);
            if (s.SyncIntervalSeconds < 5) s.SyncIntervalSeconds = 5;
            if (s.SyncBatchSize < 1) s.SyncBatchSize = 1;
            if (s.SyncBatchSize > 500) s.SyncBatchSize = 500;
            if (s.ApiTimeoutSeconds < 5) s.ApiTimeoutSeconds = 5;

            s.AutoCloseSeconds = Num.ParseI(GetS(root, "AutoCloseSeconds", "30"), s.AutoCloseSeconds);
            s.PlaySound = Num.ParseB(GetS(root, "PlaySound", "true"), s.PlaySound);
            s.PopupCorner = GetS(root, "PopupCorner", s.PopupCorner).ToLowerInvariant();
            s.AlwaysOnTop = Num.ParseB(GetS(root, "AlwaysOnTop", "true"), s.AlwaysOnTop);
            s.StartMinimizedToTray = Num.ParseB(GetS(root, "StartMinimizedToTray", "false"), s.StartMinimizedToTray);
            s.LogFolder = GetS(root, "LogFolder", s.LogFolder);

            if (s.PollIntervalMs < 300) s.PollIntervalMs = 300;
            if (s.PointsPerAmountToman <= 0) s.PointsPerAmountToman = 500000;
            if (s.PointsBase != "net") s.PointsBase = "gross";
            return s;
        }

        /// <summary>رمزنگاری کلید عامل با DPAPI (کاربر جاری). فقط روی ویندوز.</summary>
        public static string ProtectSecret(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            byte[] raw = Encoding.UTF8.GetBytes(value);
            byte[] enc = ProtectedData.Protect(raw, null, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(enc);
        }

        public static string UnprotectSecret(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            byte[] enc = Convert.FromBase64String(value);
            byte[] raw = ProtectedData.Unprotect(enc, null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(raw);
        }

        private static string GetS(XmlNode root, string name, string def)
        {
            XmlNode n = root.SelectSingleNode(name);
            if (n == null || n.InnerText == null) return def;
            string t = n.InnerText.Trim();
            return t.Length == 0 ? def : t;
        }

        public void Save()
        {
            if (string.IsNullOrEmpty(FilePath)) return;

            XmlDocument doc = new XmlDocument();
            XmlDeclaration decl = doc.CreateXmlDeclaration("1.0", "utf-8", null);
            doc.AppendChild(decl);
            XmlElement root = doc.CreateElement("PosDiscount");
            doc.AppendChild(root);

            doc.AppendChild(doc.CreateComment(" فایل تنظیمات برنامه «تخفیف لحظه‌ای صندوق هولو» — قابل ویرایش دستی هم هست "));

            Add(doc, root, "Server", Server);
            Add(doc, root, "Database", Database);
            Add(doc, root, "UseWindowsAuth", UseWindowsAuth ? "true" : "false");
            Add(doc, root, "SqlUser", SqlUser);
            Add(doc, root, "SqlPassword", SqlPassword);
            Add(doc, root, "ConnectTimeoutSeconds", ConnectTimeoutSeconds.ToString(CultureInfo.InvariantCulture));

            Add(doc, root, "PollIntervalMs", PollIntervalMs.ToString(CultureInfo.InvariantCulture));
            Add(doc, root, "SkipExistingOnStart", SkipExistingOnStart ? "true" : "false");
            Add(doc, root, "LastCashierCode", LastCashierCode.ToString(CultureInfo.InvariantCulture));

            Add(doc, root, "DiscountEnabled", DiscountEnabled ? "true" : "false");
            Add(doc, root, "DiscountMode", DiscountMode);
            Add(doc, root, "Percent", Percent.ToString(CultureInfo.InvariantCulture));
            Add(doc, root, "FixedAmount", FixedAmount.ToString(CultureInfo.InvariantCulture));
            Add(doc, root, "MaxPercent", MaxPercent.ToString(CultureInfo.InvariantCulture));
            Add(doc, root, "MaxAmount", MaxAmount.ToString(CultureInfo.InvariantCulture));
            Add(doc, root, "MinInvoiceAmount", MinInvoiceAmount.ToString(CultureInfo.InvariantCulture));
            Add(doc, root, "MaxDiscountPerDay", MaxDiscountPerDay.ToString(CultureInfo.InvariantCulture));
            Add(doc, root, "RequireDiscountPermission", RequireDiscountPermission ? "true" : "false");
            Add(doc, root, "RoundTo", RoundTo.ToString(CultureInfo.InvariantCulture));

            Add(doc, root, "PointsEnabled", PointsEnabled ? "true" : "false");
            Add(doc, root, "PointsPerAmountToman", PointsPerAmountToman.ToString(CultureInfo.InvariantCulture));
            Add(doc, root, "PointsBase", PointsBase);
            Add(doc, root, "LocalDbFile", LocalDbFile);
            Add(doc, root, "CaptureMobileInPopup", CaptureMobileInPopup ? "true" : "false");
            Add(doc, root, "RoundInFavorOfCustomer", RoundInFavorOfCustomer ? "true" : "false");
            Add(doc, root, "StartTime", StartTime);
            Add(doc, root, "EndTime", EndTime);

            XmlElement tierRoot = doc.CreateElement("Tiers");
            root.AppendChild(tierRoot);
            for (int i = 0; i < Tiers.Count; i++)
            {
                DiscountTier t = Tiers[i];
                XmlElement e = doc.CreateElement("Tier");
                e.SetAttribute("MinAmount", t.MinAmount.ToString(CultureInfo.InvariantCulture));
                e.SetAttribute("Percent", t.Percent.ToString(CultureInfo.InvariantCulture));
                e.SetAttribute("Amount", t.Amount.ToString(CultureInfo.InvariantCulture));
                tierRoot.AppendChild(e);
            }

            Add(doc, root, "SyncEnabled", SyncEnabled ? "true" : "false");
            Add(doc, root, "ApiBaseUrl", ApiBaseUrl);
            Add(doc, root, "ApiAgentId", ApiAgentId);
            // کلید عامل با DPAPI همین کاربر ویندوزی رمز می‌شود؛ متن ساده در فایل نوشته نمی‌شود.
            Add(doc, root, "ApiAgentSecretProtected", ProtectSecret(ApiAgentSecret));
            Add(doc, root, "CampaignStartDate", CampaignStartDate);
            Add(doc, root, "SyncIntervalSeconds", SyncIntervalSeconds.ToString(CultureInfo.InvariantCulture));
            Add(doc, root, "SyncBatchSize", SyncBatchSize.ToString(CultureInfo.InvariantCulture));
            Add(doc, root, "ApiTimeoutSeconds", ApiTimeoutSeconds.ToString(CultureInfo.InvariantCulture));

            Add(doc, root, "AutoCloseSeconds", AutoCloseSeconds.ToString(CultureInfo.InvariantCulture));
            Add(doc, root, "PlaySound", PlaySound ? "true" : "false");
            Add(doc, root, "PopupCorner", PopupCorner);
            Add(doc, root, "AlwaysOnTop", AlwaysOnTop ? "true" : "false");
            Add(doc, root, "StartMinimizedToTray", StartMinimizedToTray ? "true" : "false");
            Add(doc, root, "LogFolder", LogFolder);

            string dir = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            doc.Save(FilePath);
        }

        private static void Add(XmlDocument doc, XmlElement root, string name, string value)
        {
            XmlElement e = doc.CreateElement(name);
            e.InnerText = value == null ? "" : value;
            root.AppendChild(e);
        }

        public AppSettings Clone()
        {
            AppSettings c = (AppSettings)MemberwiseClone();
            c.SyncEnabled = SyncEnabled;
            c.ApiBaseUrl = ApiBaseUrl;
            c.ApiAgentId = ApiAgentId;
            c.ApiAgentSecret = ApiAgentSecret;
            c.CampaignStartDate = CampaignStartDate;
            c.SyncIntervalSeconds = SyncIntervalSeconds;
            c.SyncBatchSize = SyncBatchSize;
            c.ApiTimeoutSeconds = ApiTimeoutSeconds;
            c.Tiers = new List<DiscountTier>();
            for (int i = 0; i < Tiers.Count; i++)
                c.Tiers.Add(new DiscountTier(Tiers[i].MinAmount, Tiers[i].Percent, Tiers[i].Amount));
            return c;
        }

        public string LogFolderFull
        {
            get
            {
                string dir = Path.GetDirectoryName(FilePath);
                if (string.IsNullOrEmpty(dir)) dir = ".";
                string lf = string.IsNullOrEmpty(LogFolder) ? "logs" : LogFolder;
                return Path.IsPathRooted(lf) ? lf : Path.Combine(dir, lf);
            }
        }
    }
}
