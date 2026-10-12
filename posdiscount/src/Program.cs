using System;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace PosDiscount
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += new ThreadExceptionEventHandler(OnThreadException);
            AppDomain.CurrentDomain.UnhandledException += new UnhandledExceptionEventHandler(OnDomainException);

            string dir = AppDomain.CurrentDomain.BaseDirectory;
            string settingsPath = Path.Combine(dir, "settings.xml");

            if (args != null)
            {
                for (int i = 0; i < args.Length; i++)
                {
                    string a = (args[i] ?? "").ToLowerInvariant();
                    if (a == "--reset-settings")
                    {
                        try { if (File.Exists(settingsPath)) File.Delete(settingsPath); }
                        catch (Exception) { }
                    }
                    else if (a == "--settings" && i + 1 < args.Length)
                    {
                        settingsPath = args[i + 1];
                    }
                }
            }

            bool createdNew;
            using (Mutex mutex = new Mutex(true, "PosDiscount_Holoo_SingleInstance", out createdNew))
            {
                if (!createdNew)
                {
                    MessageBox.Show("برنامه «تخفیف لحظه‌ای صندوق هولو» از قبل روی این رایانه در حال اجراست.",
                        "اجرای تکراری", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                AppSettings s = AppSettings.Load(settingsPath);

                // اگر نام سرور در فایل تنظیمات خراب باشد (مثلاً شناسه اینستنس به‌جای نام اینستنس)
                // یا سرور محلی جواب ندهد، همین‌جا خودکار اصلاح می‌شود تا برنامه با یک مقدار اشتباه
                // در settings.xml «قفل» نشود. نسخه قبلی فایل به settings.xml.bak بکاپ می‌شود.
                string connectionFix = "";
                try { connectionFix = ConnectionFixer.Heal(s); }
                catch (Exception ex) { CsvLog.Error(s, "auto heal: " + ex.ToString()); }

                CsvLog.Info(s, "شروع برنامه — نسخه " + typeof(Program).Assembly.GetName().Version
                    + " | تنظیمات: " + settingsPath);
                if (!string.IsNullOrEmpty(connectionFix))
                {
                    CsvLog.Info(s, "اصلاح خودکار اتصال: " + connectionFix);
                    MessageBox.Show("تنظیمات اتصال به‌صورت خودکار اصلاح شد:\r\n\r\n" + connectionFix
                        + "\r\n\r\nسرور فعلی: " + s.Server
                        + "\r\n(نسخه قبلی فایل تنظیمات در settings.xml.bak نگه داشته شد.)",
                        "اصلاح خودکار اتصال", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }

                while (true)
                {
                    CashierInfo cashier = null;
                    using (CashierSelectForm f = new CashierSelectForm(s))
                    {
                        if (f.ShowDialog() != DialogResult.OK) return;
                        cashier = f.Selected;
                    }
                    if (cashier == null) return;

                    s.LastCashierCode = cashier.Code;
                    try { s.Save(); } catch (Exception ex) { CsvLog.Error(s, "save settings: " + ex.ToString()); }
                    CsvLog.Info(s, "ورود صندوق‌دار: " + cashier.Name + " (کد " + cashier.Code + ")");

                    bool changeUser = false;
                    using (DashboardForm d = new DashboardForm(s, cashier))
                    {
                        Application.Run(d);
                        changeUser = d.ChangeUserRequested;
                        if (!changeUser && !d.IsDisposed) Application.ExitThread();
                    }
                    CsvLog.Info(s, changeUser ? "تغییر صندوق‌دار" : "خروج از برنامه");
                    if (!changeUser) return;
                }
            }
        }

        private static void OnThreadException(object sender, ThreadExceptionEventArgs e)
        {
            ShowError(e.Exception);
        }

        private static void OnDomainException(object sender, UnhandledExceptionEventArgs e)
        {
            ShowError(e.ExceptionObject as Exception);
        }

        private static void ShowError(Exception ex)
        {
            string text = ex == null ? "خطای نامشخص" : ex.ToString();
            try
            {
                string dir = AppDomain.CurrentDomain.BaseDirectory;
                CsvLog.Error(Path.Combine(dir, "logs"), "unhandled: " + text);
            }
            catch (Exception) { }
            MessageBox.Show("خطای پیش‌بینی‌نشده:\r\n" + text, "خطا", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
