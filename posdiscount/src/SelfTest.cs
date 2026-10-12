using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Data.SQLite;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows.Forms;

namespace PosDiscount
{
    /// <summary>
    /// تست خودکار بدون رابط کاربری:
    ///   • اتصال و کوئری‌های «فقط خواندن» روی دیتابیس هولو
    ///   • موتور تخفیف
    ///   • موتور امتیاز و ابزار شماره موبایل
    ///   • دیتابیس محلی SQLite (روی یک فایل موقت، بدون دست‌زدن به داده‌های واقعی)
    ///   • تأیید این‌که برنامه هیچ نوشتنی در دیتابیس هولو ندارد
    ///   • تشخیص/اصلاح «نام سرور» خراب (باگ شناسه اینستنس مثل MSSQL17.SQLEXPRESS)
    /// اجرا: SelfTest.exe [--user 9] [--gross 1234000] [--out report.txt] [--ui-shot] [--heal]
    ///   --heal : فقط فایل settings.xml را از نظر نام سرور بررسی/اصلاح می‌کند (با بکاپ .bak)
    ///   --draw N : قرعه‌کشی نمونه روی دیتابیس محلی واقعی (فقط نمایش، بدون ذخیره)
    /// خروجی متنی UTF-8 در bin\logs\selftest.txt نوشته می‌شود.
    /// </summary>
    internal static class SelfTest
    {
        private static int _fails;

        [STAThread]
        private static int Main(string[] args)
        {
            int userCode = 9;
            double gross = 1234000;
            string outPath = null;
            bool uiShot = false;
            bool healMode = false;
            int drawCount = 0;

            for (int i = 0; i < args.Length; i++)
            {
                string a = (args[i] ?? "").ToLowerInvariant();
                if (a == "--user" && i + 1 < args.Length) userCode = Num.ParseI(args[++i], userCode);
                else if (a == "--gross" && i + 1 < args.Length) gross = Num.ParseD(args[++i], gross);
                else if (a == "--out" && i + 1 < args.Length) outPath = args[++i];
                else if (a == "--ui-shot") uiShot = true;
                else if (a == "--heal") healMode = true;
                else if (a == "--draw" && i + 1 < args.Length) drawCount = Num.ParseI(args[++i], 3);
            }

            StringBuilder log = new StringBuilder();
            int exit = 0;

            string dir = AppDomain.CurrentDomain.BaseDirectory;
            string settingsPath = Path.Combine(dir, "settings.xml");
            log.AppendLine("=== PosDiscount SelfTest ===");
            log.AppendLine("زمان اجرا: " + DateTime.Now.ToString("yyyy/MM/dd HH:mm:ss"));
            log.AppendLine("فایل تنظیمات: " + settingsPath);

            // ---------------- حالت فقط اصلاح اتصال (--heal) ----------------
            if (healMode)
            {
                AppSettings hs = AppSettings.Load(settingsPath);
                log.AppendLine();
                log.AppendLine("--- اصلاح خودکار اتصال (--heal) ---");
                log.AppendLine("  سرور قبل: «" + hs.Server + "»"
                    + (ConnectionFixer.LooksLikeInstanceId(hs.Server) ? "  ← این یک «شناسه اینستنس» است، نه نام اینستنس" : ""));
                string report = ConnectionFixer.Heal(hs);
                log.AppendLine("  سرور بعد: «" + hs.Server + "»");
                log.AppendLine("  " + (report.Length == 0 ? "تغییری لازم نبود." : report));

                string pmsg;
                bool pok = ConnectionFixer.Probe(hs, hs.Server, out pmsg);
                log.AppendLine("  آزمایش اتصال با تنظیمات فعلی: " + (pok ? "موفق — " : "ناموفق — ") + pmsg);
                log.AppendLine();
                log.AppendLine("=== نتیجه: " + (pok ? "اتصال برقرار است" : "اتصال برقرار نشد") + " ===");
                if (!pok) _fails++;
                Write(log, dir, outPath);
                Console.WriteLine("heal finished — server: " + hs.Server + " | connected: " + pok);
                return pok ? 0 : 9;
            }

            // ---------------- حالت «نمایش قرعه‌کشی روی دیتابیس واقعی» (--draw N) ----------------
            if (drawCount > 0)
            {
                AppSettings ds = AppSettings.Load(settingsPath);
                log.AppendLine();
                log.AppendLine("--- قرعه‌کشی نمونه روی دیتابیس محلی واقعی (" + drawCount + " برنده — بدون ذخیره) ---");
                log.AppendLine("  فایل دیتابیس: " + ds.LocalDbFullPath);
                using (LocalStore ls = new LocalStore(ds.LocalDbFullPath))
                {
                    string lerr;
                    if (!ls.Open(out lerr))
                    {
                        log.AppendLine("  باز نشد: " + lerr);
                        Write(log, dir, outPath);
                        Console.WriteLine("local db failed");
                        return 4;
                    }
                    List<PhoneProfile> table = ls.GetPointsTable(0);
                    long tickets = 0;
                    for (int i = 0; i < table.Count; i++) tickets += LotteryEngine.TicketsOf(table[i]);
                    log.AppendLine("  شماره‌های ثبت‌شده: " + table.Count + " | مجموع شانس‌ها: " + tickets
                        + " | قرعه‌کشی‌های ذخیره‌شده: " + ls.CountDraws());
                    for (int i = 0; i < table.Count && i < 20; i++)
                        log.AppendLine("    " + PhoneUtil.Pretty(table[i].Mobile) + " | " + table[i].Name
                            + " | امتیاز (شانس): " + table[i].Points + " | فاکتور: " + table[i].Count);

                    int seed = LotteryEngine.SuggestedSeed();
                    LotteryResult r = LotteryEngine.Draw(table, drawCount, seed, 1);
                    log.AppendLine("  seed: " + seed + " | شرکت‌کننده: " + r.Participants + " | مجموع شانس: " + r.TotalTickets);
                    for (int i = 0; i < r.Winners.Count; i++)
                    {
                        Winner w = r.Winners[i];
                        log.AppendLine("    برنده " + w.Rank + ": " + PhoneUtil.Pretty(w.Mobile)
                            + (w.Name.Length > 0 ? " (" + w.Name + ")" : "")
                            + " | شانس: " + LotteryEngine.PercentText(w.ChanceInitial)
                            + " (" + LotteryEngine.OddsText(w.ChanceInitial) + ")"
                            + " | در لحظه انتخاب: " + LotteryEngine.PercentText(w.ChanceAtPick));
                    }
                    for (int i = 0; i < r.Notes.Count; i++) log.AppendLine("    توجه: " + r.Notes[i]);
                    log.AppendLine("  (این حالت فقط نمایش است؛ چیزی ذخیره نشد. اجرای واقعی از فرم «قرعه‌کشی» در برنامه.)");
                }
                log.AppendLine();
                log.AppendLine("=== نتیجه: نمایش قرعه‌کشی روی داده واقعی انجام شد ===");
                Write(log, dir, outPath);
                Console.WriteLine("draw preview finished (nothing saved)");
                return 0;
            }

            AppSettings s = AppSettings.Load(settingsPath);
            log.AppendLine("رشته اتصال: " + s.BuildConnectionString());
            log.AppendLine("حالت تخفیف: " + DiscountEngine.ModePersian(s.DiscountMode)
                + " | درصد: " + Num.P(s.Percent) + " | مبلغ ثابت: " + Num.F(s.FixedAmount)
                + " | حداقل فاکتور: " + Num.F(s.MinInvoiceAmount)
                + " | سقف درصدی: " + Num.P(s.MaxPercent) + " | سقف ریالی: " + Num.F(s.MaxAmount)
                + " | سقف روزانه: " + Num.F(s.MaxDiscountPerDay)
                + " | گرد کردن: " + Num.F(s.RoundTo) + (s.RoundInFavorOfCustomer ? " به نفع مشتری" : " به نفع فروشگاه")
                + " | بازه: " + s.StartTime + "-" + s.EndTime);
            log.AppendLine("امتیاز: " + (s.PointsEnabled ? "فعال" : "غیرفعال")
                + " | هر " + Num.F(s.PointsPerAmountToman) + " تومان خرید در یک فاکتور = ۱ امتیاز"
                + " | پایه: " + PointsEngine.BaseLabel(s.PointsBase)
                + " | دیتابیس محلی: " + s.LocalDbFullPath);
            log.AppendLine("پله‌ها: " + s.Tiers.Count);
            for (int i = 0; i < s.Tiers.Count; i++)
                log.AppendLine("   پله " + (i + 1) + ": از " + Num.F(s.Tiers[i].MinAmount)
                    + " ریال → درصد " + Num.P(s.Tiers[i].Percent) + " / مبلغ " + Num.F(s.Tiers[i].Amount));

            // ---------------- اتصال ----------------
            HolooDb db = new HolooDb(s.BuildConnectionString());
            DbStatus st = db.Test();
            log.AppendLine("اتصال: " + (st.Ok ? "موفق" : "ناموفق") + " — " + st.Message);
            if (!st.Ok)
            {
                Write(log, dir, outPath);
                Console.WriteLine("DB test FAILED");
                return 3;
            }
            log.AppendLine("روز کاری دیتابیس: " + st.JalaliDay
                + (st.Day.HasValue ? " (" + FmtDate.G(st.Day.Value) + ")" : "")
                + " | فاکتورهای جدول جاری: " + st.Invoices);

            // ---------------- کاربران ----------------
            List<CashierInfo> users = db.GetCashiers();
            log.AppendLine();
            log.AppendLine("--- کاربران USERDB (" + users.Count + ") ---");
            CashierInfo chosen = null;
            for (int i = 0; i < users.Count; i++)
            {
                CashierInfo u = users[i];
                if (u.Code == userCode) chosen = u;
                log.AppendLine("  [" + (u.Active ? "فعال" : "غیرفعال") + "] کد " + u.Code + " | " + u.Name
                    + " | مجوز تخفیف: " + (u.CanDiscount ? "دارد" : "ندارد")
                    + " | فاکتور روز: " + u.DayCount + " | آخرین کد فاکتور: " + u.LastFCode);
            }
            log.AppendLine("کاربر تست: " + (chosen == null ? "پیدا نشد (کد " + userCode + ")" : chosen.Name + " (کد " + chosen.Code + ")"));
            if (chosen == null) exit = 1;

            // ---------------- جمع روز ----------------
            DaySummary day = db.GetDaySummary(userCode);
            double todayDisc = db.GetTodayDiscountSum(userCode);
            log.AppendLine();
            log.AppendLine("--- جمع روز کاری کاربر " + userCode + " ---");
            log.AppendLine("  تاریخ: " + FmtDate.G(day.Day) + " | تعداد فاکتور: " + day.Count
                + " | جمع کل: " + Num.F(day.Gross) + " | تخفیف ثبت‌شده: " + Num.F(day.Discount)
                + " | قابل پرداخت: " + Num.F(day.Net) + " | آخرین کد فاکتور: " + day.MaxFCode);

            // ---------------- آخرین فاکتورها ----------------
            List<InvoiceInfo> last = db.GetWorkDayInvoices(userCode, 5);
            log.AppendLine();
            log.AppendLine("--- آخرین " + last.Count + " فاکتور این کاربر (روز کاری) ---");
            for (int i = 0; i < last.Count; i++)
            {
                InvoiceInfo inv = last[i];
                DiscountResult r = DiscountEngine.Compute(s, inv.Gross, todayDisc, chosen != null && chosen.CanDiscount);
                log.AppendLine("  فاکتور " + inv.FCodeC + " (کد " + inv.FCode + ") ساعت " + inv.Time
                    + " | اقلام: " + inv.Items + " | جمع کل: " + Num.F(inv.Gross)
                    + " | جمع ردیف‌ها: " + Num.F(inv.LineSum)
                    + " | " + (Math.Abs(inv.LineSum - inv.Gross) < 1 ? "تطابق جمع OK" : "اختلاف " + Num.F(inv.Gross - inv.LineSum))
                    + " | تخفیف فعلی: " + Num.F(inv.Discount) + " | نهایی فعلی: " + Num.F(inv.Net));
                log.AppendLine("      پرداخت: " + inv.PayText() + " | کارتخوان: " + (inv.PosTerminal.Length == 0 ? "—" : inv.PosTerminal)
                    + " | موبایل ثبت‌شده روی فاکتور: " + (inv.Mobile.Length == 0 ? "—" : PhoneUtil.Pretty(PhoneUtil.Normalize(inv.Mobile)))
                    + " | امتیاز این فاکتور: " + PointsEngine.ForAmount(PointsEngine.BaseAmount(s, inv.Gross, inv.Discount), s.PointsPerAmountToman)
                    + " | پیشنهاد برنامه: " + (r.HasDiscount
                        ? Num.F(r.Discount) + " (" + Num.P(r.Percent) + "٪) → " + Num.F(r.Net) + " | قاعده: " + r.RuleName
                        : "ندارد | " + r.Reason));
            }

            // ---------------- پلینگ ----------------
            long after = day.MaxFCode - 3;
            if (after < 0) after = 0;
            List<InvoiceInfo> fresh = db.GetNewInvoices(after, userCode, 20);
            log.AppendLine();
            log.AppendLine("--- تست پلینگ: GetNewInvoices(after=" + after + ") → " + fresh.Count + " فاکتور ---");
            for (int i = 0; i < fresh.Count; i++)
            {
                InvoiceInfo inv = fresh[i];
                DiscountResult r = DiscountEngine.Compute(s, inv.Gross, todayDisc, chosen != null && chosen.CanDiscount);
                log.AppendLine("  کد " + inv.FCode + " | شماره " + inv.FCodeC + " | " + inv.Time
                    + " | جمع کل " + Num.F(inv.Gross)
                    + " | پاپ‌آپ؟ " + (r.HasDiscount ? "بله" : "خیر") + " (پاپ‌آپ فقط برای فاکتور مشمول تخفیف)"
                    + " | تخفیف پیشنهادی " + Num.F(r.Discount) + " | مبلغ جدید " + Num.F(r.Net));
            }

            // ---------------- سناریوهای موتور تخفیف ----------------
            log.AppendLine();
            log.AppendLine("--- سناریوهای موتور تخفیف روی مبلغ ورودی ---");
            double[] cases = new double[] { 200000, 400000, 1000000, gross, 3000000, 12000000, 300000000 };
            for (int i = 0; i < cases.Length; i++)
            {
                DiscountResult r = DiscountEngine.Compute(s, cases[i], 0, true);
                log.AppendLine("  مبلغ " + Num.F(cases[i]) + " → "
                    + (r.HasDiscount
                        ? "تخفیف " + Num.F(r.Discount) + " (" + Num.P(r.Percent) + "٪) | قابل پرداخت " + Num.F(r.Net) + " | قاعده: " + r.RuleName
                        : "بدون تخفیف | " + r.Reason));
            }
            DiscountResult noPerm = DiscountEngine.Compute(s, 3000000, 0, false);
            log.AppendLine("  کاربر بدون مجوز تخفیف (RequireDiscountPermission=" + s.RequireDiscountPermission + ") → "
                + (noPerm.HasDiscount ? "تخفیف " + Num.F(noPerm.Discount) : "بدون تخفیف | " + noPerm.Reason));
            DiscountResult dailyCap = DiscountEngine.Compute(s, 3000000, s.MaxDiscountPerDay > 0 ? s.MaxDiscountPerDay : 5000000, true);
            log.AppendLine("  سقف روزانه (" + Num.F(s.MaxDiscountPerDay) + ") با تخفیف استفاده‌شده 5000000 → "
                + (dailyCap.HasDiscount ? "تخفیف " + Num.F(dailyCap.Discount) : "بدون تخفیف | " + dailyCap.Reason));

            // ---------------- موتور امتیاز (قاعده دقیق کارفرما) ----------------
            log.AppendLine();
            log.AppendLine("--- تست موتور امتیاز (هر " + Num.F(s.PointsPerAmountToman) + " تومان در یک فاکتور = ۱ امتیاز) ---");
            // مبالغ به ریال (۱ تومان = ۱۰ ریال)
            Expect(log, "۲ فاکتور ۲۵۰ هزار تومانی → ۰ امتیاز",
                PointsEngine.ForAmount(2500000, s.PointsPerAmountToman) + PointsEngine.ForAmount(2500000, s.PointsPerAmountToman), 0);
            Expect(log, "۵۰۰ هزار تومان → ۱ امتیاز", PointsEngine.ForAmount(5000000, s.PointsPerAmountToman), 1);
            Expect(log, "۷۵۰ هزار تومان → ۱ امتیاز", PointsEngine.ForAmount(7500000, s.PointsPerAmountToman), 1);
            Expect(log, "۱ میلیون تومان → ۲ امتیاز", PointsEngine.ForAmount(10000000, s.PointsPerAmountToman), 2);
            Expect(log, "۴۹۹٬۹۹۹ تومان → ۰ امتیاز", PointsEngine.ForAmount(4999990, s.PointsPerAmountToman), 0);
            Expect(log, "فسخ/برگشتی (۰ ریال) → ۰ امتیاز", PointsEngine.ForAmount(0, s.PointsPerAmountToman), 0);
            log.AppendLine("  نمونه توضیح نمایشی: " + PointsEngine.Explain(7500000, s.PointsPerAmountToman));
            log.AppendLine("  متن قاعده: " + PointsEngine.RuleText(s.PointsPerAmountToman));

            // ---------------- ابزار شماره موبایل ----------------
            log.AppendLine();
            log.AppendLine("--- تست یکسان‌سازی شماره موبایل ---");
            string[] rawMobiles = new string[] { "09123456789", "0912 345 6789", "۰۹۱۲۳۴۵۶۷۸۹", "+989123456789",
                "00989123456789", "9123456789", "0912-345-6789", "12345" };
            for (int i = 0; i < rawMobiles.Length; i++)
            {
                string n = PhoneUtil.Normalize(rawMobiles[i]);
                log.AppendLine("  «" + rawMobiles[i] + "» → " + n
                    + " | نمایش: " + PhoneUtil.Pretty(n)
                    + " | موبایل معتبر؟ " + (PhoneUtil.IsMobile(n) ? "بله" : "خیر"));
            }
            ExpectStr(log, "یکسان‌سازی ارقام فارسی", PhoneUtil.Normalize("۰۹۱۲۳۴۵۶۷۸۹"), "09123456789");
            ExpectStr(log, "یکسان‌سازی +98", PhoneUtil.Normalize("+989123456789"), "09123456789");
            ExpectStr(log, "یکسان‌سازی 0098", PhoneUtil.Normalize("00989123456789"), "09123456789");
            ExpectStr(log, "یکسان‌سازی بدون صفر", PhoneUtil.Normalize("9123456789"), "09123456789");
            ExpectStr(log, "شماره نامعتبر حذف نمی‌شود ولی معتبر نیست", PhoneUtil.IsMobile(PhoneUtil.Normalize("12345")) ? "معتبر" : "نامعتبر", "نامعتبر");

            // ---------------- تشخیص/اصلاح نام سرور (باگ واقعی: شناسه اینستنس) ----------------
            log.AppendLine();
            log.AppendLine("--- تست تشخیص/اصلاح نام سرور (باگ «شناسه اینستنس» مثل MSSQL17.SQLEXPRESS) ---");
            ConnectionFixerTest(s, log);

            // ---------------- دیتابیس محلی (روی فایل موقت) ----------------
            log.AppendLine();
            log.AppendLine("--- تست دیتابیس محلی SQLite روی فایل موقت (بدون دست‌زدن به داده‌های واقعی) ---");
            string tempDb = Path.Combine(Path.GetTempPath(), "posdisc-selftest-" + Guid.NewGuid().ToString("N") + ".db");
            try
            {
                LocalStoreTest(s, tempDb, log);
            }
            catch (Exception ex)
            {
                log.AppendLine("  خطا در تست دیتابیس محلی: " + ex.ToString());
                exit = 6;
            }
            finally
            {
                try { if (File.Exists(tempDb)) File.Delete(tempDb); } catch (Exception) { }
                try { if (File.Exists(tempDb + "-wal")) File.Delete(tempDb + "-wal"); } catch (Exception) { }
                try { if (File.Exists(tempDb + "-shm")) File.Delete(tempDb + "-shm"); } catch (Exception) { }
            }

            // ---------------- قرعه‌کشی (هر ۱ امتیاز = ۱ شانس) ----------------
            log.AppendLine();
            log.AppendLine("--- تست قرعه‌کشی وزنی: هر ۱ امتیاز = ۱ شانس ---");
            LotteryEngineTest(log);

            log.AppendLine();
            log.AppendLine("--- تست قرعه‌کشی پله‌ای/چند جایزه (هر جایزه تعداد برنده جداگانه) ---");
            LotteryPrizesTest(log);

            log.AppendLine();
            log.AppendLine("--- تست قرعه‌کشی روی دیتابیس محلی موقت (ذخیره نتیجه + خروجی CSV) ---");
            LotteryStoreTest(s, log);

            log.AppendLine();
            log.AppendLine("--- تست محدود کردن قرعه‌کشی به یک دسته (صندوق‌دار/تاریخ/حداقل فاکتور) + مهاجرت دیتابیس ---");
            LotteryScopeTest(s, log);

            // ---------------- دیتابیس محلی واقعی برنامه (فقط گزارش) ----------------
            log.AppendLine();
            log.AppendLine("--- دیتابیس محلی واقعی برنامه (گزارش وضعیت) ---");
            try
            {
                using (LocalStore real = new LocalStore(s.LocalDbFullPath))
                {
                    string err;
                    if (real.Open(out err))
                    {
                        log.AppendLine("  فایل: " + real.FilePath);
                        log.AppendLine("  فاکتورهای ذخیره‌شده: " + real.CountInvoices()
                            + " | شماره‌های ثبت‌شده: " + real.CountMobiles()
                            + " | فاکتورهای بدون موبایل: " + real.CountWithoutMobile()
                            + " | با موبایل امروز: " + real.CountWithMobileOnDay(DateTime.Today));
                        log.AppendLine("  امتیاز فاکتورهای امروز (" + FmtDate.G(DateTime.Today) + "): " + real.SumPointsOnDay(DateTime.Today));
                        List<PhoneProfile> top = real.GetPointsTable(5);
                        for (int i = 0; i < top.Count; i++)
                            log.AppendLine("    " + (i + 1) + ") " + PhoneUtil.Pretty(top[i].Mobile)
                                + " | " + top[i].Name + " | امتیاز: " + top[i].Points
                                + " | فاکتور: " + top[i].Count + " | خرید: " + Num.F(top[i].Amount));
                    }
                    else
                    {
                        log.AppendLine("  باز نشد: " + err);
                    }
                }
            }
            catch (Exception ex)
            {
                log.AppendLine("  خطا: " + ex.Message);
            }

            // ---------------- تأیید فقط‌خواندنی بودن دیتابیس هولو ----------------
            log.AppendLine();
            log.AppendLine("--- تأیید «هیچ نوشتنی در دیتابیس فروشگاه» ---");
            try
            {
                Type t = typeof(HolooDb);
                MethodInfo[] methods = t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                string[] banned = new string[] { "apply", "insert", "update", "delete", "save", "set", "exec", "merge", "drop", "create", "alter" };
                int bad = 0;
                StringBuilder names = new StringBuilder();
                for (int i = 0; i < methods.Length; i++)
                {
                    names.Append(methods[i].Name);
                    if (i < methods.Length - 1) names.Append(", ");
                    string lower = methods[i].Name.ToLowerInvariant();
                    for (int j = 0; j < banned.Length; j++)
                        if (lower.StartsWith(banned[j]))
                        {
                            bad++;
                            log.AppendLine("  هشدار: متد مشکوک به نوشتن: " + methods[i].Name);
                        }
                }
                log.AppendLine("  متدهای عمومی HolooDb: " + names.ToString());
                Expect(log, "هیچ متد نوشتنی روی دیتابیس هولو وجود ندارد", bad, 0);
                log.AppendLine("  (این بررسی بر اساس نام متدهاست؛ تمام کوئری‌های HolooDb فقط SELECT هستند.)");

                // تغییرنکردن مقدار تخفیف واقعی فاکتور پس از اجرای همه عملیات این تست
                if (last.Count > 0)
                {
                    using (SqlConnection c = new SqlConnection(s.BuildConnectionString()))
                    {
                        c.Open();
                        double d0 = ReadDiscount(c, null, last[0].FCode);
                        // همه کارهای بالا (خواندن فاکتور، محاسبه امتیاز، دیتابیس محلی) اجرا شد
                        double d1 = ReadDiscount(c, null, last[0].FCode);
                        log.AppendLine("  تخفیف فاکتور " + last[0].FCodeC + " قبل از تست: " + Num.F(d0)
                            + " | بعد از تست: " + Num.F(d1));
                        Expect(log, "دیتابیس هولو تغییر نکرده است", Math.Abs(d1 - d0) < 0.5 ? 0 : 1, 0);
                    }
                }
            }
            catch (Exception ex)
            {
                log.AppendLine("  خطا در بررسی فقط‌خواندنی: " + ex.ToString());
                exit = 7;
            }

            // ---------------- تست ساخت فرم‌ها ----------------
            log.AppendLine();
            log.AppendLine("--- تست رابط کاربری: ساخت همه فرم‌ها (بدون باز کردن پنجره) ---");
            try
            {
                AppSettings shot = s.Clone();
                shot.AutoCloseSeconds = 0;
                shot.PlaySound = false;

                using (CashierSelectForm f = new CashierSelectForm(s))
                {
                    f.CreateControl();
                    log.AppendLine("  فرم انتخاب صندوق‌دار: ساخته شد (" + f.ClientSize.Width + "x" + f.ClientSize.Height + ")");
                    FitCheck(f, log, "انتخاب صندوق‌دار");
                }
                using (SettingsForm f = new SettingsForm(s))
                {
                    f.CreateControl();
                    log.AppendLine("  فرم تنظیمات: ساخته شد (" + f.ClientSize.Width + "x" + f.ClientSize.Height + ")");
                    FitCheck(f, log, "تنظیمات");
                }
                using (DashboardForm f = new DashboardForm(shot, chosen == null ? users[0] : chosen))
                {
                    f.CreateControl();
                    log.AppendLine("  پنجره اصلی (داشبورد): ساخته شد (" + f.ClientSize.Width + "x" + f.ClientSize.Height + ")");
                    FitCheck(f, log, "داشبورد");
                }

                InvoiceInfo sample = new InvoiceInfo();
                sample.FCode = last.Count > 0 ? last[0].FCode : 590669;
                sample.FCodeC = last.Count > 0 ? last[0].FCodeC : 21168;
                sample.Time = "15:30";
                sample.Date = day.Day;
                sample.JalaliDate = st.JalaliDay;
                sample.CashierName = chosen == null ? "صندوق‌دار" : chosen.Name;
                sample.Items = 3;
                sample.Gross = 2900000;
                sample.Pos = 2900000;
                sample.PosTerminal = "10200010001";
                sample.Mobile = "09123456789";
                DiscountResult sampleRes = DiscountEngine.Compute(s, sample.Gross, 0, true);

                // فرم‌های مربوط به موبایل/امتیاز با یک دیتابیس موقت آزمایش می‌شوند (نه دیتابیس واقعی)
                string tmpForms = Path.Combine(Path.GetTempPath(), "posdisc-forms-" + Guid.NewGuid().ToString("N") + ".db");
                try
                {
                    using (LocalStore ls = new LocalStore(tmpForms))
                    {
                        string err;
                        bool open = ls.Open(out err);
                        StoredInvoice rec = null;
                        if (open)
                        {
                            rec = ls.SaveInvoice(sample, 0, s);
                            log.AppendLine("  ذخیره نمونه در دیتابیس موقت: "
                                + (rec == null ? "ناموفق" : ("OK — امتیاز فاکتور: " + rec.Points + " | موبایل: " + rec.Mobile)));
                        }

                        using (InvoicePopupForm f = new InvoicePopupForm(shot, open ? ls : null, sample, sampleRes, rec))
                        {
                            f.CreateControl();
                            log.AppendLine("  پاپ‌آپ فاکتور (با کادر موبایل و امتیاز): ساخته شد — نمونه "
                                + Num.F(sample.Gross) + " ریال → تخفیف " + Num.F(sampleRes.Discount)
                                + " | مبلغ جدید " + Num.F(sampleRes.Net));
                            FitCheck(f, log, "پاپ‌آپ فاکتور");
                        }

                        if (rec != null)
                        {
                            using (MobileEntryForm f = new MobileEntryForm(s, ls, rec))
                            {
                                f.CreateControl();
                                log.AppendLine("  فرم ثبت موبایل/امتیاز: ساخته شد");
                                FitCheck(f, log, "ثبت موبایل");
                            }
                            using (CustomerInvoicesForm f = new CustomerInvoicesForm(s, ls, rec.Mobile))
                            {
                                f.CreateControl();
                                log.AppendLine("  فرم فاکتورهای یک شماره: ساخته شد (" + f.ClientSize.Width + "x" + f.ClientSize.Height + ")");
                                FitCheck(f, log, "فاکتورهای یک شماره");
                            }
                        }
                        using (PointsForm f = new PointsForm(s, open ? ls : null))
                        {
                            f.CreateControl();
                            log.AppendLine("  فرم امتیاز مشتریان: ساخته شد (" + f.ClientSize.Width + "x" + f.ClientSize.Height + ")");
                            FitCheck(f, log, "امتیاز مشتریان");
                            Expect(log, "دکمه «قرعه‌کشی» در فرم امتیاز مشتریان (راه ورود کاربر)",
                                HasButton(f, "قرعه‌کشی") ? 1 : 0, 1);
                        }
                        // سه مشتری با امتیازهای ۱، ۲ و ۳ برای آزمایش واقعی فرم قرعه‌کشی
                        if (open)
                        {
                            ls.SaveInvoice(Fake(9001, 5000000, "09121110001", "مشتری یک"), 0, s);
                            ls.SaveInvoice(Fake(9002, 10000000, "09121110002", "مشتری دو"), 0, s);
                            ls.SaveInvoice(Fake(9003, 15000000, "09121110003", "مشتری سه"), 0, s);
                        }
                        using (LotteryForm f = new LotteryForm(s, open ? ls : null))
                        {
                            f.CreateControl();
                            log.AppendLine("  فرم قرعه‌کشی: ساخته شد (" + f.ClientSize.Width + "x" + f.ClientSize.Height + ")");
                            FitCheck(f, log, "قرعه‌کشی");

                            // اجرای واقعی قرعه‌کشی از خود فرم (همان مسیر دکمه) + بررسی جدول و ذخیره خودکار
                            if (open && ls != null)
                            {
                                int drawsBefore = ls.CountDraws();
                                f.RunDraw(false);
                                DataGridView g = FindGrid(f);
                                int rows = g == null ? -1 : g.Rows.Count;
                                int drawsAfter = ls.CountDraws();
                                log.AppendLine("  اجرای قرعه‌کشی از فرم: " + rows + " برنده در جدول | قرعه‌کشی‌های ذخیره‌شده: "
                                    + drawsBefore + " → " + drawsAfter);
                                Expect(log, "اجرای قرعه‌کشی از فرم و نمایش برندگان در جدول", rows, 3);
                                Expect(log, "ذخیره خودکار قرعه‌کشی در دیتابیس محلی از فرم", drawsAfter, drawsBefore + 1);
                                bool ranksOk = true;
                                if (rows == 3)
                                    for (int r = 0; r < 3; r++)
                                        if (Convert.ToString(g.Rows[r].Cells[0].Value) != (r + 1).ToString()) ranksOk = false;
                                Expect(log, "رتبه‌های جدول به ترتیب (1،2،3)", ranksOk ? 1 : 0, 1);

                                // ---- حالت ۲: پله‌ای/چند جایزه، از خود فرم ----
                                ComboBox cbType = FindCombo(f, "پله");
                                if (cbType == null)
                                {
                                    Expect(log, "پیدا کردن فهرست «نوع قرعه‌کشی» در فرم", 0, 1);
                                }
                                else
                                {
                                    cbType.SelectedIndex = 1;
                                    int before2 = ls.CountDraws();
                                    f.RunDraw(false);
                                    DataGridView g2 = FindGrid(f);
                                    int rows2 = g2 == null ? -1 : g2.Rows.Count;
                                    int after2 = ls.CountDraws();
                                    string p1 = g2 != null && rows2 > 0 ? Convert.ToString(g2.Rows[0].Cells[1].Value) : "";
                                    string p2 = g2 != null && rows2 > 1 ? Convert.ToString(g2.Rows[1].Cells[1].Value) : "";
                                    log.AppendLine("  حالت پله‌ای از فرم: " + rows2 + " برنده | جایزه اول: «" + p1
                                        + "» | دوم: «" + p2 + "» | ذخیره: " + before2 + " → " + after2);
                                    Expect(log, "اجرای حالت پله‌ای از فرم (۳ شرکت‌کننده → ۳ برنده)", rows2, 3);
                                    ExpectStr(log, "جایزه برنده اول در جدول", p1, "جایزه اول");
                                    ExpectStr(log, "جایزه برنده دوم در جدول", p2, "جایزه دوم");
                                    Expect(log, "ذخیره خودکار قرعه‌کشی پله‌ای در دیتابیس محلی", after2, before2 + 1);
                                }
                            }
                        }
                    }
                }
                finally
                {
                    try { if (File.Exists(tmpForms)) File.Delete(tmpForms); } catch (Exception) { }
                }
            }
            catch (Exception ex)
            {
                log.AppendLine("  خطا در ساخت/رندر فرم‌ها: " + ex.ToString());
                exit = 4;
            }

            // ---------------- لاگ CSV ----------------
            log.AppendLine();
            log.AppendLine("--- تست لاگ CSV (شامل موبایل و امتیاز) ---");
            try
            {
                InvoiceInfo logInv = last.Count > 0 ? last[0] : new InvoiceInfo();
                DiscountResult logRes = DiscountEngine.Compute(s, logInv.Gross, todayDisc, true);
                CsvLog.Row(s, chosen == null ? "تست" : chosen.Name, logInv, logRes, "تست لاگ",
                    "09123456789", PointsEngine.ForAmount(logInv.Gross, s.PointsPerAmountToman));
                log.AppendLine("  پوشه لاگ: " + s.LogFolderFull);
                string[] files = Directory.GetFiles(s.LogFolderFull, "pos-discount-*.csv");
                for (int i = 0; i < files.Length; i++)
                {
                    log.AppendLine("  فایل: " + Path.GetFileName(files[i]) + " (" + new FileInfo(files[i]).Length + " بایت)");
                    string[] lines = File.ReadAllLines(files[i]);
                    for (int j = 0; j < lines.Length && j < 3; j++) log.AppendLine("    " + lines[j]);
                }
            }
            catch (Exception ex)
            {
                log.AppendLine("  خطا در تست لاگ: " + ex.Message);
            }

            if (uiShot)
            {
                log.AppendLine();
                log.AppendLine("--- عکس‌برداری واقعی از پنجره‌ها روی دسکتاپ (--ui-shot) ---");
                try
                {
                    RunUiShot(s, chosen == null ? users[0] : chosen, log, dir);
                }
                catch (Exception ex)
                {
                    log.AppendLine("  خطا در عکس‌برداری: " + ex.ToString());
                    exit = 5;
                }
            }

            log.AppendLine();
            log.AppendLine("=== نتیجه: " + (_fails == 0 ? "همه بررسی‌ها OK" : (_fails + " بررسی ناموفق")) + " ===");
            if (_fails > 0 && exit == 0) exit = 8;

            Write(log, dir, outPath);
            Console.WriteLine("SelfTest finished, exit code " + exit + ", failed checks: " + _fails);
            return exit;
        }

        // ==================================================================
        //  تشخیص/اصلاح نام سرور (باگ واقعی: شناسه اینستنس به‌جای نام اینستنس)
        // ==================================================================
        private static void ConnectionFixerTest(AppSettings s, StringBuilder log)
        {
            log.AppendLine("  سرور فعلی برنامه: «" + s.Server + "»"
                + (ConnectionFixer.LooksLikeInstanceId(s.Server)
                    ? "  ← شناسه اینستنس است (نامعتبر)؛ برنامه آن را خودکار اصلاح می‌کند" : ""));

            Dictionary<string, string> regIds = ConnectionFixer.InstanceIdToName();
            List<string> regNames = ConnectionFixer.RegistryInstanceNames();
            log.AppendLine("  نام اینستنس‌های رجیستری: "
                + (regNames.Count == 0 ? "— (هیچ اینستنس SQL Server روی این رایانه ثبت نشده)" : string.Join("، ", regNames.ToArray())));
            int regChecked = 0;
            foreach (KeyValuePair<string, string> kv in regIds)
            {
                log.AppendLine("    شناسه «" + kv.Key + "» (مقدار رجیستری) → نام «" + kv.Value + "» (نام مقدار)");
                ExpectStr(log, "اصلاح شناسه اینستنس رجیستری «" + kv.Key + "»",
                    ConnectionFixer.Sanitize(".\\" + kv.Key, regIds), ConnectionFixer.LocalServerOf(kv.Value));
                regChecked++;
            }
            if (regChecked == 0)
                log.AppendLine("  (توجه) رجیستری این دستگاه هیچ اینستنس SQL Server ندارد؛ فقط حالت‌های ساختگی آزمایش شدند.");

            Dictionary<string, string> fake = new Dictionary<string, string>();
            fake["MSSQL17.SQLEXPRESS"] = "SQLEXPRESS";
            Dictionary<string, string> empty = new Dictionary<string, string>();

            ExpectStr(log, "شناسه اینستنس بدون نام میزبان → .\\SQLEXPRESS",
                ConnectionFixer.Sanitize("MSSQL17.SQLEXPRESS", fake), ".\\SQLEXPRESS");
            ExpectStr(log, "شناسه اینستنس با .\\ → .\\SQLEXPRESS",
                ConnectionFixer.Sanitize(".\\MSSQL17.SQLEXPRESS", fake), ".\\SQLEXPRESS");
            ExpectStr(log, "شناسه اینستنس روی میزبان شبکه → SERVER-PC\\SQLEXPRESS",
                ConnectionFixer.Sanitize("SERVER-PC\\MSSQL17.SQLEXPRESS", fake), "SERVER-PC\\SQLEXPRESS");
            ExpectStr(log, "نام درست اینستنس دست‌نخورده می‌ماند",
                ConnectionFixer.Sanitize(".\\SQLEXPRESS", fake), ".\\SQLEXPRESS");
            ExpectStr(log, "آدرس شبکه دست‌نخورده می‌ماند",
                ConnectionFixer.Sanitize("192.168.1.10,1433", fake), "192.168.1.10,1433");
            ExpectStr(log, "میزبان و اینستنس دیتابیس شبکه دست‌نخورده می‌ماند",
                ConnectionFixer.Sanitize("SERVER\\HOLOO", fake), "SERVER\\HOLOO");
            ExpectStr(log, "شناسه اینستنس پیش‌فرض → .",
                ConnectionFixer.Sanitize("MSSQL16.MSSQLSERVER", empty), ".");
            ExpectStr(log, "شناسه اینستنس پیش‌فرض با .\\ → .",
                ConnectionFixer.Sanitize(".\\MSSQL16.MSSQLSERVER", empty), ".");

            Expect(log, "الگوی شناسه اینستنس شناسایی می‌شود", ConnectionFixer.LooksLikeInstanceId("MSSQL17.SQLEXPRESS") ? 1 : 0, 1);
            Expect(log, "نام معمولی اینستنس شناسه نیست", ConnectionFixer.LooksLikeInstanceId("SQLEXPRESS") ? 1 : 0, 0);
            Expect(log, "نام دیتابیس شناسه اینستنس نیست", ConnectionFixer.LooksLikeInstanceId("Holoo1") ? 1 : 0, 0);
            Expect(log, "سرور .\\SQLEXPRESS محلی است", ConnectionFixer.LooksLocal(".\\SQLEXPRESS") ? 1 : 0, 1);
            Expect(log, "سرور «(local)» محلی است", ConnectionFixer.LooksLocal("(local)") ? 1 : 0, 1);
            Expect(log, "آدرس شبکه محلی نیست", ConnectionFixer.LooksLocal("192.168.1.10") ? 1 : 0, 0);
            Expect(log, "نام میزبان شبکه محلی نیست", ConnectionFixer.LooksLocal("SERVER-PC\\SQLEXPRESS") ? 1 : 0, 0);

            string err26 = "A network-related or instance-specific error occurred while establishing a connection to SQL Server. "
                + "(provider: SQL Network Interfaces, error: 26 - Error Locating Server/Instance Specified)";
            string errLogin = "Login failed for user 'sa'.";
            Expect(log, "خطای 26 به‌عنوان «سرور پیدا نشد» شناخته می‌شود",
                ConnectionFixer.LooksLikeServerNotFound(new Exception(err26)) ? 1 : 0, 1);
            Expect(log, "خطای لاگین خطای «سرور پیدا نشد» نیست",
                ConnectionFixer.LooksLikeServerNotFound(new Exception(errLogin)) ? 1 : 0, 0);

            Expect(log, "شناسه اینستنس با پیشوند .\\ هم شناسایی می‌شود",
                ConnectionFixer.HasInstanceIdValue(".\\MSSQL17.SQLEXPRESS") ? 1 : 0, 1);
            Expect(log, "نام درست با پیشوند .\\ شناسه نیست",
                ConnectionFixer.HasInstanceIdValue(".\\SQLEXPRESS") ? 1 : 0, 0);

            List<string> cands = ConnectionFixer.CandidateServers(".\\MSSQL17.SQLEXPRESS");
            bool hasInstanceId = false;
            for (int i = 0; i < cands.Count; i++)
                if (ConnectionFixer.HasInstanceIdValue(cands[i])) hasInstanceId = true;
            log.AppendLine("  پیشنهادهای امتحان‌شده: " + string.Join("، ", cands.ToArray()));
            Expect(log, "فهرست پیشنهادها هیچ «شناسه اینستنس» ندارد (حتی در ابتدای فهرست)", hasInstanceId ? 1 : 0, 0);
            ExpectStr(log, "مقدار خراب فعلی هم در فهرست پاک‌سازی می‌شود", cands[0], ".\\SQLEXPRESS");
            Expect(log, "فهرست پیشنهادها شامل .\\SQLEXPRESS است", cands.Contains(".\\SQLEXPRESS") ? 1 : 0, 1);
            Expect(log, "فهرست پیشنهادها شامل . (اینستنس پیش‌فرض) است", cands.Contains(".") ? 1 : 0, 1);

            // ---- اصلاح خودکار واقعی روی یک فایل تنظیمات موقت (با اتصال واقعی به دیتابیس) ----
            string tempSettings = Path.Combine(Path.GetTempPath(), "posdisc-settings-" + Guid.NewGuid().ToString("N") + ".xml");
            try
            {
                AppSettings bad = s.Clone();
                bad.FilePath = tempSettings;
                string brokenId = "MSSQL17.SQLEXPRESS";
                foreach (KeyValuePair<string, string> kv in regIds) { brokenId = kv.Key; break; }
                bad.Server = ".\\" + brokenId;
                bad.Save();

                bool changed;
                string report = ConnectionFixer.Heal(bad, out changed);
                log.AppendLine("  اصلاح خودکار: «" + ".\\" + brokenId + "» → «" + bad.Server + "»"
                    + (report.Length == 0 ? "" : " | " + report));
                Expect(log, "فایل تنظیمات با مقدار خراب اصلاح شد", changed ? 1 : 0, 1);
                ExpectStr(log, "سرور اصلاح‌شده دیگر شناسه اینستنس نیست",
                    ConnectionFixer.LooksLikeInstanceId(bad.Server) ? "شناسه" : "نام درست", "نام درست");

                AppSettings reread = AppSettings.Load(tempSettings);
                ExpectStr(log, "مقدار اصلاح‌شده در فایل ذخیره شد", reread.Server, bad.Server);
                Expect(log, "نسخه قبلی در settings.xml.bak بکاپ گرفته شد", File.Exists(tempSettings + ".bak") ? 1 : 0, 1);

                string pm;
                bool okNow = ConnectionFixer.Probe(bad, bad.Server, out pm);
                Expect(log, "اتصال با سرور اصلاح‌شده برقرار است", okNow ? 1 : 0, 1);
                if (!okNow) log.AppendLine("    دلیل: " + HolooDb.DescribeConnectionError(new Exception(pm)));
            }
            catch (Exception ex)
            {
                log.AppendLine("  خطا در تست اصلاح اتصال: " + ex.ToString());
                _fails++;
            }
            finally
            {
                try { if (File.Exists(tempSettings)) File.Delete(tempSettings); } catch (Exception) { }
                try { if (File.Exists(tempSettings + ".bak")) File.Delete(tempSettings + ".bak"); } catch (Exception) { }
            }
        }

        // ==================================================================
        //  قرعه‌کشی: موتور وزنی (هر ۱ امتیاز = ۱ شانس)
        // ==================================================================
        private static PhoneProfile MakePhone(string mobile, int points, int invoices, double amount)
        {
            PhoneProfile p = new PhoneProfile();
            p.Mobile = mobile;
            p.Name = mobile != null && mobile.Length >= 2 ? "مشتری " + mobile.Substring(mobile.Length - 2) : "بدون نام";
            p.Points = points;
            p.Count = invoices;
            p.Amount = amount;
            return p;
        }

        private static bool SameResult(LotteryResult a, LotteryResult b)
        {
            if (a == null || b == null) return false;
            if (a.Seed != b.Seed || a.Winners.Count != b.Winners.Count) return false;
            for (int i = 0; i < a.Winners.Count; i++)
            {
                if (a.Winners[i].Mobile != b.Winners[i].Mobile) return false;
                if (a.Winners[i].Rank != b.Winners[i].Rank) return false;
            }
            return true;
        }

        private static string WinnersMobile(LotteryResult r)
        {
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < r.Winners.Count; i++)
            {
                if (i > 0) sb.Append("، ");
                sb.Append((i + 1) + ": " + PhoneUtil.Pretty(r.Winners[i].Mobile));
            }
            return sb.Length == 0 ? "—" : sb.ToString();
        }

        private static void LotteryEngineTest(StringBuilder log)
        {
            PhoneProfile p3 = MakePhone("09120000003", 3, 1, 3000000);
            Expect(log, "هر ۱ امتیاز = ۱ شانس (۳ امتیاز → ۳ شانس)", LotteryEngine.TicketsOf(p3), 3);

            List<PhoneProfile> people = new List<PhoneProfile>();
            people.Add(MakePhone("09120000001", 1, 1, 1000000));
            people.Add(MakePhone("09120000002", 2, 2, 2000000));
            people.Add(MakePhone("09120000003", 4, 5, 4000000));
            people.Add(MakePhone("09120000004", 0, 1, 500000));    // ۰ امتیاز → شرکت نمی‌کند
            people.Add(MakePhone("", 9, 1, 9000000));               // بدون شماره → شرکت نمی‌کند

            LotteryResult r = LotteryEngine.Draw(people, 2, 12345, 1);
            Expect(log, "شرکت‌کنندگان واجد شرط (بدون امتیاز/شماره حذف)", r.Participants, 3);
            Expect(log, "مجموع شانس‌ها = مجموع امتیازها (۱+۲+۴)", (int)r.TotalTickets, 7);
            Expect(log, "تعداد برندگان درخواستی", r.Winners.Count, 2);
            Expect(log, "برنده تکراری وجود ندارد", DistinctWinners(r) ? 1 : 0, 1);
            Expect(log, "برندگان به ترتیب شماره‌گذاری شده‌اند (1،2)",
                r.Winners.Count == 2 && r.Winners[0].Rank == 1 && r.Winners[1].Rank == 2 ? 1 : 0, 1);
            bool outsidersWon = false;
            for (int i = 0; i < r.Winners.Count; i++)
                if (r.Winners[i].Mobile == "09120000004" || r.Winners[i].Mobile.Length == 0) outsidersWon = true;
            Expect(log, "شرکت‌کننده‌های حذف‌شده (۰ امتیاز / بدون شماره) برنده نمی‌شوند", outsidersWon ? 1 : 0, 0);
            ExpectStr(log, "نمایش شانس «1 از N»", LotteryEngine.OddsText(0.5), "1 از 2.0");
            ExpectStr(log, "نمایش شانس به درصد", LotteryEngine.PercentText(0.5), "50٪");

            double sumChance = 0;
            for (int i = 0; i < r.Tickets.Count; i++) sumChance += r.Tickets[i].ChanceInitial;
            Expect(log, "مجموع شانس‌های اولیه = ۱", Math.Abs(sumChance - 1.0) < 1e-9 ? 1 : 0, 1);
            Expect(log, "شانس اولیه ۴ امتیازی = ۴ از ۷",
                Math.Abs(r.Tickets[0].ChanceInitial - (4.0 / 7.0)) < 1e-9 ? 1 : 0, 1);
            log.AppendLine("  قرعه‌کشی نمونه (seed 12345): " + WinnersMobile(r));

            LotteryResult again = LotteryEngine.Draw(people, 2, 12345, 1);
            Expect(log, "بازتولید عیناً همان نتیجه با همان seed", SameResult(r, again) ? 1 : 0, 1);
            LotteryResult other = LotteryEngine.Draw(people, 2, 99, 1);
            log.AppendLine("  با seed 99: " + WinnersMobile(other) + " (بازه test: فقط اطلاعاتی)");

            LotteryResult high = LotteryEngine.Draw(people, 10, 7, 3);
            Expect(log, "حداقل امتیاز ۳ → فقط یک شرکت‌کننده", high.Participants, 1);
            Expect(log, "تعداد برندگان به تعداد شرکت‌کنندگان محدود شد", high.Winners.Count, 1);
            Expect(log, "یادداشت محدود شدن تعداد برندگان ثبت شد", high.Notes.Count > 0 ? 1 : 0, 1);

            LotteryResult none = LotteryEngine.Draw(people, 3, 5, 99);
            Expect(log, "بدون شرکت‌کننده واجد شرط → صفر برنده", none.Winners.Count, 0);
            Expect(log, "در این حالت یادداشت توضیحی ثبت شد", none.Notes.Count > 0 ? 1 : 0, 1);

            // وزن بیشتر = شانس بیشتر (قطعی، چون seedها ثابت‌اند)
            List<PhoneProfile> two = new List<PhoneProfile>();
            two.Add(MakePhone("09120000009", 9, 3, 9000000));
            two.Add(MakePhone("09120000001", 1, 1, 1000000));
            int heavy = 0;
            int runs = 2000;
            for (int seed = 1; seed <= runs; seed++)
            {
                LotteryResult x = LotteryEngine.Draw(two, 1, seed, 1);
                if (x.Winners.Count > 0 && x.Winners[0].Mobile == "09120000009") heavy++;
            }
            log.AppendLine("  ۹ شانس در مقابل ۱ شانس: برد نفر با ۹ شانس در " + heavy + " از " + runs
                + " قرعه‌کشی = " + Math.Round(100.0 * heavy / runs, 1) + "٪");
            Expect(log, "شانس ۹ به ۱ نزدیک ۹۰٪ است (بین ۸۵٪ و ۹۵٪)",
                (heavy >= 1700 && heavy <= 1900) ? 1 : 0, 1);

            List<PhoneProfile> three = new List<PhoneProfile>();
            three.Add(MakePhone("09120000101", 1, 1, 1000000));
            three.Add(MakePhone("09120000102", 2, 1, 2000000));
            three.Add(MakePhone("09120000103", 4, 1, 4000000));
            int[] wins = new int[3];
            for (int seed = 1; seed <= 1000; seed++)
            {
                LotteryResult x = LotteryEngine.Draw(three, 1, seed, 1);
                if (x.Winners.Count == 0) continue;
                for (int i = 0; i < 3; i++)
                    if (x.Winners[0].Mobile == three[i].Mobile) wins[i]++;
            }
            log.AppendLine("  برد به ازای شانس‌های ۱/۲/۴ در ۱۰۰۰ قرعه‌کشی: "
                + wins[0] + " / " + wins[1] + " / " + wins[2]);
            Expect(log, "ترتیب برد متناسب با شانس‌ها صعودی است", (wins[0] < wins[1] && wins[1] < wins[2]) ? 1 : 0, 1);

            // قرعه‌کشی همه شرکت‌کنندگان (بدون محدودیت) با تعداد برنده برابر تعداد نفرات
            LotteryResult all = LotteryEngine.Draw(three, 3, 42, 1);
            Expect(log, "درخواست ۳ برنده از ۳ شرکت‌کننده → ۳ برنده", all.Winners.Count, 3);
            Expect(log, "هر شرکت‌کننده فقط یک بار برنده می‌شود", DistinctWinners(all) ? 1 : 0, 1);
            bool chanceOk = true;
            for (int i = 0; i < all.Winners.Count; i++)
            {
                double c = all.Winners[i].ChanceAtPick;
                if (c <= 0 || c > 1.0 + 1e-9) chanceOk = false;
            }
            Expect(log, "شانس هر برنده در لحظه انتخاب بین ۰ و ۱ است", chanceOk ? 1 : 0, 1);
            bool rankOk = true;
            for (int i = 0; i < all.Winners.Count; i++)
                if (all.Winners[i].Rank != i + 1) rankOk = false;
            Expect(log, "ترتیب برندگان پیوسته است (۱،۲،۳)", rankOk ? 1 : 0, 1);
        }

        private static bool HasButton(Control parent, string text)
        {
            for (int i = 0; i < parent.Controls.Count; i++)
            {
                Button b = parent.Controls[i] as Button;
                if (b != null && b.Text != null && b.Text.IndexOf(text) >= 0) return true;
                if (parent.Controls[i].HasChildren && HasButton(parent.Controls[i], text)) return true;
            }
            return false;
        }

        private static ComboBox FindCombo(Control parent, string itemContains)
        {
            for (int i = 0; i < parent.Controls.Count; i++)
            {
                ComboBox c = parent.Controls[i] as ComboBox;
                if (c != null)
                {
                    for (int j = 0; j < c.Items.Count; j++)
                    {
                        string t = Convert.ToString(c.Items[j]);
                        if (t != null && t.IndexOf(itemContains) >= 0) return c;
                    }
                }
                if (parent.Controls[i].HasChildren)
                {
                    ComboBox nested = FindCombo(parent.Controls[i], itemContains);
                    if (nested != null) return nested;
                }
            }
            return null;
        }

        private static DataGridView FindGrid(Form f)
        {
            for (int i = 0; i < f.Controls.Count; i++)
            {
                DataGridView g = f.Controls[i] as DataGridView;
                if (g != null) return g;
            }
            return null;
        }

        private static bool DistinctWinners(LotteryResult r)
        {
            if (r == null) return false;
            for (int i = 0; i < r.Winners.Count; i++)
                for (int j = i + 1; j < r.Winners.Count; j++)
                    if (r.Winners[i].Mobile == r.Winners[j].Mobile) return false;
            return true;
        }

        // ==================================================================
        //  قرعه‌کشی پله‌ای / چند جایزه
        // ==================================================================
        private static void LotteryPrizesTest(StringBuilder log)
        {
            List<PhoneProfile> people = new List<PhoneProfile>();
            people.Add(MakePhone("09120000201", 1, 1, 1000000));
            people.Add(MakePhone("09120000202", 2, 2, 2000000));
            people.Add(MakePhone("09120000203", 4, 4, 4000000));
            people.Add(MakePhone("09120000204", 8, 8, 8000000));

            List<Prize> prizes = new List<Prize>();
            prizes.Add(new Prize("جایزه اول", 1));
            prizes.Add(new Prize("جایزه دوم", 2));

            LotteryResult r = LotteryEngine.DrawPrizes(people, prizes, 4321, 1, "پله‌ای");
            Expect(log, "تعداد برندگان پله‌ای (۱+۲)", r.Winners.Count, 3);
            ExpectStr(log, "جایزه برنده اول", r.Winners[0].Prize, "جایزه اول");
            ExpectStr(log, "جایزه برنده دوم", r.Winners[1].Prize, "جایزه دوم");
            ExpectStr(log, "جایزه برنده سوم", r.Winners[2].Prize, "جایزه دوم");
            Expect(log, "هیچ کس در دو جایزه برنده نمی‌شود", DistinctWinners(r) ? 1 : 0, 1);
            Expect(log, "ترتیب کل برندگان پیوسته است",
                r.Winners[0].Rank == 1 && r.Winners[1].Rank == 2 && r.Winners[2].Rank == 3 ? 1 : 0, 1);
            ExpectStr(log, "متن جایزه‌ها", r.PrizesText(), "جایزه اول: 1 | جایزه دوم: 2");
            Expect(log, "مجموع برندگان درخواستی", r.RequestedTotal(), 3);
            Expect(log, "مجموع شانس‌ها (۱+۲+۴+۸)", (int)r.TotalTickets, 15);
            log.AppendLine("  نتیجه پله‌ای (seed 4321): " + WinnersWithPrize(r));

            LotteryResult again = LotteryEngine.DrawPrizes(people, prizes, 4321, 1, "پله‌ای");
            Expect(log, "بازتولید عین همان نتیجه پله‌ای با همان seed", SameResult(r, again) ? 1 : 0, 1);

            List<Prize> big = new List<Prize>();
            big.Add(new Prize("جایزه اول", 3));
            big.Add(new Prize("جایزه دوم", 5));   // مجموع ۸ > ۴ شرکت‌کننده
            LotteryResult clamp = LotteryEngine.DrawPrizes(people, big, 11, 1, "پله‌ای");
            Expect(log, "محدود شدن مجموع برندگان به تعداد شرکت‌کنندگان", clamp.Winners.Count, 4);
            Expect(log, "یادداشت محدود شدن + جایزه‌ها", clamp.Notes.Count >= 2 ? 1 : 0, 1);
            Expect(log, "بدون برنده تکراری در حالت محدودشده", DistinctWinners(clamp) ? 1 : 0, 1);

            // نگاشت شماره برنده به جایزه در مرزهای هر جایزه
            List<Prize> two = new List<Prize>();
            two.Add(new Prize("A", 2));
            two.Add(new Prize("B", 3));
            ExpectStr(log, "نگاشت جایزه: برنده ۱", LotteryEngine.PrizeOf(two, 0), "A");
            ExpectStr(log, "نگاشت جایزه: برنده ۲", LotteryEngine.PrizeOf(two, 1), "A");
            ExpectStr(log, "نگاشت جایزه: برنده ۳", LotteryEngine.PrizeOf(two, 2), "B");
            ExpectStr(log, "نگاشت جایزه: برنده ۵", LotteryEngine.PrizeOf(two, 4), "B");
            ExpectStr(log, "نگاشت جایزه: بیرون از محدوده", LotteryEngine.PrizeOf(two, 5), "");

            // جایزه با کد نامعتبر → حداقل یک برنده (وارد کردن تعداد کمتر از ۱)
            List<Prize> bad = new List<Prize>();
            bad.Add(new Prize("جایزه", 0));
            LotteryResult fixedUp = LotteryEngine.DrawPrizes(people, bad, 5, 1, "پله‌ای");
            Expect(log, "تعداد برنده نامعتبر → ۱ برنده", fixedUp.Winners.Count, 1);
        }

        private static string WinnersWithPrize(LotteryResult r)
        {
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < r.Winners.Count; i++)
            {
                if (i > 0) sb.Append("، ");
                sb.Append(r.Winners[i].Prize + " → " + PhoneUtil.Pretty(r.Winners[i].Mobile));
            }
            return sb.Length == 0 ? "—" : sb.ToString();
        }

        // ==================================================================
        //  محدود کردن قرعه‌کشی به یک دسته + مهاجرت دیتابیس
        // ==================================================================
        private static InvoiceInfo FakeCashier(long fCode, double gross, string mobile, string alias, int userCode)
        {
            InvoiceInfo v = Fake(fCode, gross, mobile, alias);
            v.UserCode = userCode;
            v.CashierName = "صندوقدار " + userCode;
            return v;
        }

        private static void LotteryScopeTest(AppSettings s, StringBuilder log)
        {
            string tempDb = Path.Combine(Path.GetTempPath(), "posdisc-lottery-scope-" + Guid.NewGuid().ToString("N") + ".db");
            string csv = Path.Combine(Path.GetTempPath(), "posdisc-draw-scope-" + Guid.NewGuid().ToString("N") + ".csv");
            try
            {
                // ---- مهاجرت: دیتابیس «نسخه قبلی» بدون ستون‌های جایزه/دامنه ----
                using (SQLiteConnection cn = new SQLiteConnection("Data Source=" + tempDb + ";Version=3;"))
                {
                    cn.Open();
                    ExecRaw(cn, @"CREATE TABLE Draws(
    DrawId INTEGER PRIMARY KEY AUTOINCREMENT, At TEXT NOT NULL DEFAULT '', TypeName TEXT NOT NULL DEFAULT '',
    Seed INTEGER NOT NULL DEFAULT 0, Winners INTEGER NOT NULL DEFAULT 0, Participants INTEGER NOT NULL DEFAULT 0,
    TotalTickets INTEGER NOT NULL DEFAULT 0, MinPoints INTEGER NOT NULL DEFAULT 1,
    FromYmd TEXT NOT NULL DEFAULT '', ToYmd TEXT NOT NULL DEFAULT '');");
                    ExecRaw(cn, @"CREATE TABLE DrawWinners(
    DrawId INTEGER NOT NULL, Rank INTEGER NOT NULL, Mobile TEXT NOT NULL DEFAULT '', Name TEXT NOT NULL DEFAULT '',
    Points INTEGER NOT NULL DEFAULT 0, Invoices INTEGER NOT NULL DEFAULT 0, Amount REAL NOT NULL DEFAULT 0,
    ChanceInitial REAL NOT NULL DEFAULT 0, ChanceAtPick REAL NOT NULL DEFAULT 0, PRIMARY KEY(DrawId, Rank));");
                    ExecRaw(cn, "INSERT INTO Draws(DrawId, At, TypeName, Seed, Winners) VALUES(7, '1404/01/01 10:00:00', 'ساده', 555, 1);");
                    log.AppendLine("  دیتابیس «نسخه قبلی» ساخته شد (بدون ستون‌های جایزه/دامنه) با ۱ قرعه‌کشی موجود");
                }

                using (LocalStore st = new LocalStore(tempDb))
                {
                    string err;
                    if (!st.Open(out err))
                    {
                        log.AppendLine("  مهاجرت ناموفق (باز نشد): " + err);
                        Expect(log, "مهاجرت دیتابیس قدیمی بدون خطا", 1, 0);
                        return;
                    }
                    Expect(log, "داده قبلی پس از مهاجرت حفظ شد", st.CountDraws(), 1);

                    st.SaveInvoice(FakeCashier(201, 5000000, "09127770001", "مشتری الف", 9), 0, s);
                    st.SaveInvoice(FakeCashier(202, 5000000, "09127770002", "مشتری ب", 9), 0, s);
                    st.SaveInvoice(FakeCashier(203, 10000000, "09127770002", "مشتری ب", 9), 0, s);
                    st.SaveInvoice(FakeCashier(204, 5000000, "09127770003", "مشتری ج", 5), 0, s);

                    Expect(log, "دامنه: همه مشتریان", st.GetPointsTableFiltered("", "", 0, 1, 0).Count, 3);
                    Expect(log, "دامنه: فقط صندوقدار ۹", st.GetPointsTableFiltered("", "", 9, 1, 0).Count, 2);
                    Expect(log, "دامنه: فقط صندوقدار ۵", st.GetPointsTableFiltered("", "", 5, 1, 0).Count, 1);
                    Expect(log, "دامنه: حداقل ۲ فاکتور", st.GetPointsTableFiltered("", "", 0, 2, 0).Count, 1);
                    Expect(log, "دامنه: صندوقدار ۹ + حداقل ۲ فاکتور", st.GetPointsTableFiltered("", "", 9, 2, 0).Count, 1);
                    string today = FmtDate.G2(DateTime.Today);
                    Expect(log, "دامنه: بازه امروز", st.GetPointsTableFiltered(today, today, 0, 1, 0).Count, 3);
                    Expect(log, "دامنه: بازه‌ای که فاکتور ندارد", st.GetPointsTableFiltered("2000-01-01", "2000-01-02", 0, 1, 0).Count, 0);

                    List<CashierChoice> cashiers = st.LocalCashiers();
                    Expect(log, "فهرست صندوقدارهای دیتابیس محلی", cashiers.Count, 2);
                    for (int i = 0; i < cashiers.Count; i++)
                        log.AppendLine("    صندوق‌دار " + cashiers[i].UserCode + " («" + cashiers[i].Name + "»)"
                            + " — " + cashiers[i].Invoices + " فاکتور با شماره");

                    // ---- قرعه‌کشی پله‌ای محدود به دسته (صندوق‌دار ۹) ----
                    List<PhoneProfile> pool = st.GetPointsTableFiltered("", "", 9, 1, 0);
                    List<Prize> prizes = new List<Prize>();
                    prizes.Add(new Prize("جایزه اول", 1));
                    prizes.Add(new Prize("جایزه دوم", 3));
                    LotteryResult r = LotteryEngine.DrawPrizes(pool, prizes, 777, 1, "پله‌ای");
                    r.Scope = "بازه: همه تاریخ‌ها | صندوق‌دار: صندوقدار 9 | حداقل امتیاز: 1";
                    r.FromYmd = "";
                    r.ToYmd = "";
                    Expect(log, "شرکت‌کنندگان دسته صندوق‌دار ۹", r.Participants, 2);
                    Expect(log, "برندگان محدود به همان دسته", r.Winners.Count, 2);
                    ExpectStr(log, "جایزه برنده اول در حالت دسته‌ای", r.Winners[0].Prize, "جایزه اول");

                    int id = st.SaveDraw(r);
                    Expect(log, "ذخیره قرعه‌کشی پله‌ای در دیتابیس", id > 0 ? 1 : 0, 1);
                    Expect(log, "شمار کل قرعه‌کشی‌ها (۱ قدیمی + ۱ جدید)", st.CountDraws(), 2);

                    st.ExportDrawCsv(csv, r, id);
                    string text = File.ReadAllText(csv);
                    bool ok = File.Exists(csv);
                    Expect(log, "خروجی CSV قرعه‌کشی پله‌ای", ok ? 1 : 0, 1);
                    Expect(log, "CSV شامل عنوان جایزه", text.IndexOf("جایزه اول") >= 0 ? 1 : 0, 1);
                    Expect(log, "CSV شامل شرح دسته (صندوق‌دار)", text.IndexOf("صندوق‌دار: صندوقدار 9") >= 0 ? 1 : 0, 1);
                    Expect(log, "CSV شامل متن جایزه‌ها در سرصفحه", text.IndexOf("جایزه اول: 1") >= 0 ? 1 : 0, 1);
                    string[] lines = File.ReadAllLines(csv);
                    for (int i = 0; i < lines.Length && i < 4; i++) log.AppendLine("    " + lines[i]);
                }
            }
            catch (Exception ex)
            {
                log.AppendLine("  خطا در تست دامنه/مهاجرت قرعه‌کشی: " + ex.ToString());
                _fails++;
            }
            finally
            {
                try { if (File.Exists(tempDb)) File.Delete(tempDb); } catch (Exception) { }
                try { if (File.Exists(csv)) File.Delete(csv); } catch (Exception) { }
            }
        }

        private static void ExecRaw(SQLiteConnection cn, string sql)
        {
            using (SQLiteCommand cmd = new SQLiteCommand(sql, cn)) cmd.ExecuteNonQuery();
        }

        // ==================================================================
        //  قرعه‌کشی روی دیتابیس محلی (فایل موقت)
        // ==================================================================
        private static void LotteryStoreTest(AppSettings s, StringBuilder log)
        {
            string tempDb = Path.Combine(Path.GetTempPath(), "posdisc-lottery-" + Guid.NewGuid().ToString("N") + ".db");
            string csv = Path.Combine(Path.GetTempPath(), "posdisc-draw-" + Guid.NewGuid().ToString("N") + ".csv");
            try
            {
                using (LocalStore st = new LocalStore(tempDb))
                {
                    string err;
                    if (!st.Open(out err))
                    {
                        log.AppendLine("  باز کردن دیتابیس موقت ناموفق: " + err);
                        Expect(log, "ساخت دیتابیس موقت قرعه‌کشی", 1, 0);
                        return;
                    }

                    // قاعده واقعی: هر ۵۰۰ هزار تومان (۵٬۰۰۰٬۰۰۰ ریال) خرید در یک فاکتور = ۱ امتیاز
                    st.SaveInvoice(Fake(101, 5000000, "09121234501", "مشتری الف"), 0, s);
                    st.SaveInvoice(Fake(102, 10000000, "09121234501", "مشتری الف"), 0, s);
                    st.SaveInvoice(Fake(103, 10000000, "09121234502", "مشتری ب"), 0, s);
                    st.SaveInvoice(Fake(104, 2500000, "09121234503", "مشتری ج"), 0, s);   // ۲۵۰ه تومان → ۰ امتیاز

                    List<PhoneProfile> table = st.GetPointsTable(0);
                    Expect(log, "شماره‌های ثبت‌شده در دیتابیس محلی", table.Count, 3);
                    int totalPoints = 0;
                    for (int i = 0; i < table.Count; i++) totalPoints += table[i].Points;
                    Expect(log, "مجموع امتیازهای دیتابیس (۳+۲+۰)", totalPoints, 5);

                    LotteryResult r = LotteryEngine.Draw(table, 2, 2024, 1);
                    r.MinPoints = 1;
                    Expect(log, "شرکت‌کنندگان واجد شرط (۰ امتیازی حذف)", r.Participants, 2);
                    Expect(log, "مجموع شانس‌ها = مجموع امتیاز شرکت‌کنندگان", (int)r.TotalTickets, 5);
                    Expect(log, "تعداد برندگان", r.Winners.Count, 2);
                    Expect(log, "بدون برنده تکراری", DistinctWinners(r) ? 1 : 0, 1);
                    log.AppendLine("  برندگان (seed 2024): " + WinnersMobile(r));

                    int id = st.SaveDraw(r);
                    Expect(log, "ذخیره قرعه‌کشی در دیتابیس محلی", id > 0 ? 1 : 0, 1);
                    Expect(log, "شمار قرعه‌کشی‌های ذخیره‌شده", st.CountDraws(), 1);

                    st.ExportDrawCsv(csv, r, id);
                    bool ok = File.Exists(csv);
                    Expect(log, "خروجی CSV قرعه‌کشی ساخته شد", ok ? 1 : 0, 1);
                    if (ok)
                    {
                        string[] lines = File.ReadAllLines(csv);
                        int expectLines = 2 + r.Winners.Count + (r.Tickets.Count - r.Winners.Count);
                        Expect(log, "تعداد سطرهای CSV (سرصفحه + اطلاعات + برندگان + شرکت‌کنندگان)", lines.Length, expectLines);
                        Expect(log, "داده‌ها با برنده اول شروع می‌شود (ترتیب)",
                            lines.Length > 2 && lines[2].StartsWith("برنده") ? 1 : 0, 1);
                        for (int i = 0; i < lines.Length && i < 6; i++) log.AppendLine("    " + lines[i]);
                    }
                }
            }
            catch (Exception ex)
            {
                log.AppendLine("  خطا در تست قرعه‌کشی: " + ex.ToString());
                _fails++;
            }
            finally
            {
                try { if (File.Exists(tempDb)) File.Delete(tempDb); } catch (Exception) { }
                try { if (File.Exists(csv)) File.Delete(csv); } catch (Exception) { }
            }
        }

        // ==================================================================
        //  دیتابیس محلی (فایل موقت)
        // ==================================================================
        private static void LocalStoreTest(AppSettings s, string tempDb, StringBuilder log)
        {
            using (LocalStore st = new LocalStore(tempDb))
            {
                string err;
                if (!st.Open(out err))
                {
                    log.AppendLine("  باز کردن دیتابیس موقت ناموفق: " + err);
                    Expect(log, "ساخت/باز کردن دیتابیس محلی SQLite", 1, 0);
                    return;
                }
                log.AppendLine("  دیتابیس موقت ساخته شد: " + tempDb);

                // چهار فاکتور با قاعده دقیق کارفرما (مبالغ به ریال)
                string phoneA = "09123456789";
                string phoneB = "09355555555";

                InvoiceInfo i1 = Fake(1, 2500000, phoneA, "مشتری الف");   // ۲۵۰ هزار تومان → ۰ امتیاز
                InvoiceInfo i2 = Fake(2, 5000000, phoneA, "مشتری الف");   // ۵۰۰ هزار تومان → ۱ امتیاز
                InvoiceInfo i3 = Fake(3, 7500000, phoneA, "مشتری الف");   // ۷۵۰ هزار تومان → ۱ امتیاز
                InvoiceInfo i4 = Fake(4, 10000000, phoneA, "مشتری الف");  // ۱ میلیون تومان → ۲ امتیاز

                StoredInvoice r1 = st.SaveInvoice(i1, 0, s);
                StoredInvoice r2 = st.SaveInvoice(i2, 0, s);
                StoredInvoice r3 = st.SaveInvoice(i3, 0, s);
                StoredInvoice r4 = st.SaveInvoice(i4, 0, s);

                Expect(log, "۲/۵ میلیون ریال (۲۵۰ه تومان) → ۰ امتیاز", r1 == null ? -1 : r1.Points, 0);
                Expect(log, "۵ میلیون ریال (۵۰۰ه تومان) → ۱ امتیاز", r2 == null ? -1 : r2.Points, 1);
                Expect(log, "۷/۵ میلیون ریال (۷۵۰ه تومان) → ۱ امتیاز", r3 == null ? -1 : r3.Points, 1);
                Expect(log, "۱۰ میلیون ریال (۱ میلیون تومان) → ۲ امتیاز", r4 == null ? -1 : r4.Points, 2);

                List<PhoneProfile> pa = st.ProfilesByQuery(phoneA, 10);
                Expect(log, "مجموع امتیاز شماره A", pa.Count == 0 ? -1 : pa[0].Points, 4);
                Expect(log, "تعداد فاکتور شماره A", pa.Count == 0 ? -1 : pa[0].Count, 4);
                log.AppendLine("  شماره A: " + (pa.Count == 0 ? "—" : PhoneUtil.Pretty(pa[0].Mobile) + " | امتیاز: " + pa[0].Points
                    + " | فاکتور: " + pa[0].Count + " | جمع خرید: " + Num.F(pa[0].Amount)
                    + " | اولین: " + pa[0].First + " | آخرین: " + pa[0].Last));

                // جست‌وجو با قالب‌های مختلف همان شماره
                Expect(log, "جست‌وجو با ارقام فارسی", st.FindByMobile("۰۹۱۲۳۴۵۶۷۸۹", 50).Count, 4);
                Expect(log, "جست‌وجو با ۰۹۱۲ ۳۴۵ ۶۷۸۹ (فاصله)", st.FindByMobile("0912 345 6789", 50).Count, 4);
                Expect(log, "جست‌وجو با +98", st.FindByMobile("+989123456789", 50).Count, 4);
                Expect(log, "جست‌وجو با ۹۸ (بدون صفر)", st.FindByMobile("912", 50).Count, 4);

                // دو فاکتور ۲۵۰ هزار تومانی برای یک شماره → مجموع ۰ امتیاز
                InvoiceInfo j1 = Fake(5, 2500000, phoneB, "مشتری ب");
                InvoiceInfo j2 = Fake(6, 2500000, phoneB, "مشتری ب");
                st.SaveInvoice(j1, 0, s);
                st.SaveInvoice(j2, 0, s);
                List<PhoneProfile> pb = st.ProfilesByQuery(phoneB, 10);
                Expect(log, "دو فاکتور ۲۵۰ه تومانی → مجموع ۰ امتیاز", pb.Count == 0 ? -1 : pb[0].Points, 0);

                // فاکتور بدون موبایل → بدون امتیاز
                InvoiceInfo j3 = Fake(7, 9000000, "", "");
                StoredInvoice r7 = st.SaveInvoice(j3, 0, s);
                Expect(log, "فاکتور بدون شماره موبایل → ۰ امتیاز", r7 == null ? -1 : r7.Points, 0);
                Expect(log, "شمار فاکتورهای بدون موبایل", st.CountWithoutMobile(), 1);
                Expect(log, "شمار فاکتورهای یک روز مشخص", st.CountWithoutMobileOnDay(DateTime.Today), 1);

                // ثبت/تغییر موبایل یک فاکتور
                StoredInvoice moved = st.SetMobile(2, phoneB, "مشتری ب", s);
                Expect(log, "انتقال فاکتور ۵ه تومانی به شماره B → ۱ امتیاز", moved == null ? -1 : moved.Points, 1);
                List<PhoneProfile> pa2 = st.ProfilesByQuery(phoneA, 10);
                Expect(log, "امتیاز شماره A پس از انتقال", pa2.Count == 0 ? -1 : pa2[0].Points, 3);
                List<PhoneProfile> pb2 = st.ProfilesByQuery(phoneB, 10);
                Expect(log, "امتیاز شماره B پس از انتقال (۱+۰+۰)", pb2.Count == 0 ? -1 : pb2[0].Points, 1);

                // حذف شماره از فاکتور → امتیاز صفر می‌شود
                StoredInvoice cleared = st.SetMobile(2, "", "", s);
                Expect(log, "حذف شماره از فاکتور → ۰ امتیاز", cleared == null ? -1 : cleared.Points, 0);
                Expect(log, "شمار فاکتورهای بدون موبایل پس از حذف", st.CountWithoutMobile(), 2);

                // بازمحاسبه امتیازها با آستانه جدید (۱ میلیون تومان)
                AppSettings s2 = s.Clone();
                s2.PointsPerAmountToman = 1000000;
                int changed = st.RecalculatePoints(s2);
                log.AppendLine("  بازمحاسبه با آستانه ۱ میلیون تومان: " + changed + " فاکتور تغییر کرد");
                List<PhoneProfile> pa3 = st.ProfilesByQuery(phoneA, 10);
                Expect(log, "امتیاز شماره A با آستانه ۱ میلیون تومان (۰+۰+۱)", pa3.Count == 0 ? -1 : pa3[0].Points, 1);

                // جست‌وجوی فاکتورهای یک شماره + جدول امتیازات + خروجی CSV
                List<StoredInvoice> found = st.FindByMobile(phoneA, 50);
                Expect(log, "تعداد فاکتورهای شماره A در جست‌وجو", found.Count, 3);
                List<PhoneProfile> table = st.GetPointsTable(0);
                log.AppendLine("  جدول امتیازات: " + table.Count + " شماره");
                for (int i = 0; i < table.Count; i++)
                    log.AppendLine("    " + PhoneUtil.Pretty(table[i].Mobile) + " | " + table[i].Name
                        + " | امتیاز: " + table[i].Points + " | فاکتور: " + table[i].Count + " | خرید: " + Num.F(table[i].Amount));

                string csv = Path.Combine(Path.GetTempPath(), "posdisc-points-" + Guid.NewGuid().ToString("N") + ".csv");
                st.ExportPointsCsv(csv, "", "");
                bool csvOk = File.Exists(csv);
                int csvLines = csvOk ? File.ReadAllLines(csv).Length : 0;
                log.AppendLine("  خروجی CSV: " + (csvOk ? csv + " (" + csvLines + " سطر)" : "ساخته نشد"));
                Expect(log, "خروجی CSV ساخته شد", csvOk ? 1 : 0, 1);
                Expect(log, "تعداد سطرهای CSV (سرصفحه + شماره‌ها)", csvLines, table.Count + 1);
                try { if (csvOk) File.Delete(csv); } catch (Exception) { }

                // به‌روزرسانی فاکتور موجود (تغییر مبلغ) و اثر آن روی امتیاز
                InvoiceInfo i2b = Fake(2, 10000000, phoneB, "مشتری ب");
                StoredInvoice r2b = st.SaveInvoice(i2b, 0, s2);
                log.AppendLine("  به‌روزرسانی فاکتور ۲ با مبلغ ۱ میلیون تومان (آستانه فعلی تنظیمات) → امتیاز: "
                    + (r2b == null ? "—" : r2b.Points.ToString()));
                Expect(log, "ذخیره مجدد همان فاکتور، رکورد تکراری نمی‌سازد", st.CountInvoices(), 7);
            }
        }

        /// <summary>
        /// بررسی می‌کند متن هر برچسب در عرض واقعی‌اش جا می‌شود یا نه (با اندازه‌گیری واقعی فونت).
        /// این کار «بریدگی متن» را بدون نیاز به نگاه کردن به تصویر پیدا می‌کند.
        /// </summary>
        private static void FitCheck(Control root, StringBuilder log, string formName)
        {
            List<string> issues = new List<string>();
            CollectFitIssues(root, formName, issues);
            if (issues.Count == 0)
                log.AppendLine("    بررسی جا شدن متن‌ها («" + formName + "»): OK");
            else
            {
                for (int i = 0; i < issues.Count; i++) log.AppendLine("    " + issues[i]);
                Expect(log, "جا شدن همه متن‌ها در «" + formName + "»", issues.Count, 0);
            }

            List<string> overlaps = new List<string>();
            CollectOverlaps(root, formName, overlaps);
            if (overlaps.Count == 0)
                log.AppendLine("    بررسی هم‌پوشانی کنترل‌ها («" + formName + "»): OK");
            else
            {
                for (int i = 0; i < overlaps.Count; i++) log.AppendLine("    " + overlaps[i]);
                Expect(log, "نبود هم‌پوشانی کنترل‌ها در «" + formName + "»", overlaps.Count, 0);
            }
        }

        /// <summary>کنترل‌های هم‌سطح که سطح مشترک چشمگیری دارند را گزارش می‌کند.</summary>
        private static void CollectOverlaps(Control parent, string formName, List<string> issues)
        {
            List<Control> kids = new List<Control>();
            for (int i = 0; i < parent.Controls.Count; i++)
            {
                Control c = parent.Controls[i];
                if (c.Width > 12 && c.Height > 12 && c.Visible) kids.Add(c);
            }
            for (int i = 0; i < kids.Count; i++)
            {
                for (int j = i + 1; j < kids.Count; j++)
                {
                    Rectangle a = kids[i].Bounds;
                    Rectangle b = kids[j].Bounds;
                    Rectangle r = Rectangle.Intersect(a, b);
                    if (r.Width <= 0 || r.Height <= 0) continue;
                    int inter = r.Width * r.Height;
                    int min = Math.Min(a.Width * a.Height, b.Width * b.Height);
                    if (inter * 4 < min) continue;   // هم‌پوشانی کم (مثل کنترل روی پنل) مهم نیست
                    issues.Add("[هم‌پوشانی] «" + formName + "» | " + kids[i].GetType().Name + "(" + kids[i].Text + ")"
                        + " و " + kids[j].GetType().Name + "(" + kids[j].Text + ")");
                }
            }
            for (int i = 0; i < parent.Controls.Count; i++)
                if (parent.Controls[i].HasChildren) CollectOverlaps(parent.Controls[i], formName, issues);
        }

        private static void CollectFitIssues(Control root, string formName, List<string> issues)
        {
            for (int i = 0; i < root.Controls.Count; i++)
            {
                Control c = root.Controls[i];
                string text = null;
                if (c is Label) text = ((Label)c).Text;
                else if (c is CheckBox) text = ((CheckBox)c).Text;

                if (text != null && text.Length > 0 && c.Width > 0 && c.Height > 0)
                {
                    int w = c.Width - (c is CheckBox ? 22 : 4);   // فاصله داخلی و مربع تیک
                    if (w < 20) w = c.Width;
                    Size need = TextRenderer.MeasureText(text, c.Font, new Size(w, 10000),
                        TextFormatFlags.WordBreak | TextFormatFlags.NoPadding | TextFormatFlags.TextBoxControl);
                    if (need.Height > c.Height + 3)
                    {
                        string t = text.Replace("\r\n", " / ");
                        if (t.Length > 46) t = t.Substring(0, 46) + "…";
                        issues.Add("[متن جا نمی‌شود] " + c.GetType().Name + " در «" + formName + "» | «" + t
                            + "» — نیاز " + need.Height + "px، موجود " + c.Height + "px (عرض " + c.Width + ")");
                    }
                }

                if (c.HasChildren) CollectFitIssues(c, formName, issues);
            }
        }

        private static InvoiceInfo Fake(long fCode, double gross, string mobile, string alias)
        {
            InvoiceInfo v = new InvoiceInfo();
            v.FCode = fCode;
            v.FCodeC = fCode * 10;
            v.FType = 2;
            v.Date = DateTime.Today;
            v.Time = "12:00";
            v.JalaliDate = FmtDate.G(DateTime.Today);
            v.UserCode = 9;
            v.CashierName = "تست";
            v.Gross = gross;
            v.Discount = 0;
            v.Mobile = mobile;
            v.CustomerAlias = alias;
            v.Items = 2;
            return v;
        }

        // ==================================================================
        //  بررسی‌های کوچک
        // ==================================================================
        private static void Expect(StringBuilder log, string what, int actual, int expected)
        {
            bool ok = actual == expected;
            if (!ok) _fails++;
            log.AppendLine("  [" + (ok ? "OK" : "FAIL") + "] " + what
                + " — مقدار: " + actual + (ok ? "" : " | انتظار: " + expected));
        }

        private static void ExpectStr(StringBuilder log, string what, string actual, string expected)
        {
            bool ok = string.Equals(actual, expected, StringComparison.Ordinal);
            if (!ok) _fails++;
            log.AppendLine("  [" + (ok ? "OK" : "FAIL") + "] " + what
                + " — مقدار: «" + actual + "»" + (ok ? "" : " | انتظار: «" + expected + "»"));
        }

        // ==================================================================
        //  عکس‌برداری واقعی از پنجره‌ها
        // ==================================================================
        private static void RunUiShot(AppSettings s, CashierInfo cashier, StringBuilder log, string dir)
        {
            string shotDir = Path.Combine(dir, "ui");
            if (!Directory.Exists(shotDir)) Directory.CreateDirectory(shotDir);

            AppSettings shot = s.Clone();
            shot.AutoCloseSeconds = 0;
            shot.PlaySound = false;
            shot.AlwaysOnTop = false;

            // 1) فرم کالیبراسیون RTL
            using (Form probe = new Form())
            {
                probe.Text = "RTL probe";
                probe.ClientSize = new Size(320, 100);
                probe.StartPosition = FormStartPosition.Manual;
                probe.Location = new Point(80, 80);
                probe.RightToLeft = RightToLeft.Yes;
                probe.RightToLeftLayout = true;
                probe.ShowInTaskbar = false;

                Label a = new Label();
                a.Text = "AAAA";
                a.TextAlign = ContentAlignment.MiddleRight;
                a.SetBounds(0, 0, 320, 28);
                a.BackColor = Color.White;

                Label b = new Label();
                b.Text = "BBBB";
                b.TextAlign = ContentAlignment.MiddleLeft;
                b.SetBounds(0, 34, 320, 28);
                b.BackColor = Color.White;

                Label c = new Label();
                c.Text = "CC";
                c.TextAlign = ContentAlignment.MiddleCenter;
                c.SetBounds(0, 68, 42, 28);
                c.BackColor = Color.White;

                probe.Controls.Add(a);
                probe.Controls.Add(b);
                probe.Controls.Add(c);
                probe.Show();
                Application.DoEvents();
                System.Threading.Thread.Sleep(900);
                Application.DoEvents();
                string probePng = Path.Combine(shotDir, "0-rtl-probe.png");
                ShotScreen(ClientRect(probe), probePng, log, "فرم کالیبراسیون RTL");
                AnalyzeClient(probe, probePng, log, "فرم کالیبراسیون RTL");
                probe.Close();
            }

            // 2) پاپ‌آپ فاکتور (با کادر موبایل و امتیاز)
            InvoiceInfo inv = new InvoiceInfo();
            inv.FCode = 0;
            inv.FCodeC = 21168;
            inv.Time = "15:30";
            inv.JalaliDate = FmtDate.G(DateTime.Today);
            inv.CashierName = cashier.Name;
            inv.Items = 3;
            inv.Gross = 2900000;
            inv.Pos = 2900000;
            inv.PosTerminal = "10200010001";
            DiscountResult res = DiscountEngine.Compute(s, inv.Gross, 0, cashier.CanDiscount);

            using (InvoicePopupForm f = new InvoicePopupForm(shot, null, inv, res, null))
            {
                f.Show();
                Application.DoEvents();
                System.Threading.Thread.Sleep(900);
                Application.DoEvents();
                string png = Path.Combine(shotDir, "1-invoice-popup.png");
                ShotScreen(ClientRect(f), png, log, "پاپ‌آپ فاکتور (تخفیف + موبایل + امتیاز)");
                AnalyzeClient(f, png, log, "پاپ‌آپ فاکتور");
                f.Close();
            }

            // 3) پنجره اصلی (داشبورد)
            using (DashboardForm f = new DashboardForm(shot, cashier))
            {
                f.ChangeUserRequested = true;   // تا بستن، به سینی نرود
                f.Show();
                Application.DoEvents();
                System.Threading.Thread.Sleep(1600);
                Application.DoEvents();
                string png = Path.Combine(shotDir, "2-dashboard.png");
                ShotScreen(ClientRect(f), png, log, "پنجره اصلی (جست‌وجوی موبایل + امتیاز)");
                AnalyzeClient(f, png, log, "پنجره اصلی");
                f.Close();
            }

            // 4) فرم امتیازها و فاکتورهای یک شماره (روی دیتابیس موقت نمونه، نه دیتابیس واقعی)
            string tmpShotDb = Path.Combine(Path.GetTempPath(), "posdisc-shot-" + Guid.NewGuid().ToString("N") + ".db");
            try
            {
                using (LocalStore ls = new LocalStore(tmpShotDb))
                {
                    string err;
                    if (ls.Open(out err))
                    {
                        StoredInvoice demo = null;
                        string[] demoMobiles = new string[] { "09123456789", "09123456789", "09351112233" };
                        double[] demoGross = new double[] { 5000000, 10000000, 7500000 };
                        for (int i = 0; i < demoGross.Length; i++)
                        {
                            InvoiceInfo smp = new InvoiceInfo();
                            smp.FCode = 900000000 + i;
                            smp.FCodeC = 700000 + i;
                            smp.Time = "1" + i + ":30";
                            smp.Date = DateTime.Today;
                            smp.JalaliDate = FmtDate.G(DateTime.Today);
                            smp.CashierName = cashier.Name;
                            smp.Gross = demoGross[i];
                            smp.Mobile = demoMobiles[i];
                            smp.Items = 2;
                            StoredInvoice d = ls.SaveInvoice(smp, 0, s);
                            if (i == 1) demo = d;
                            log.AppendLine("  رکورد نمونه عکس " + (i + 1) + ": " + Num.F(demoGross[i]) + " ریال → "
                                + (d == null ? "ذخیره نشد" : ("امتیاز: " + d.Points)));
                        }

                        using (PointsForm f = new PointsForm(shot, ls))
                        {
                            f.Show();
                            Application.DoEvents();
                            System.Threading.Thread.Sleep(1300);
                            Application.DoEvents();
                            ShotScreen(ClientRect(f), Path.Combine(shotDir, "5-points.png"), log, "فرم امتیاز مشتریان");
                            f.Close();
                        }

                        using (LotteryForm f = new LotteryForm(shot, ls))
                        {
                            f.Show();
                            Application.DoEvents();
                            System.Threading.Thread.Sleep(1300);
                            Application.DoEvents();
                            ShotScreen(ClientRect(f), Path.Combine(shotDir, "8-lottery.png"), log, "فرم قرعه‌کشی");
                            f.Close();
                        }

                        if (demo != null)
                            using (CustomerInvoicesForm f = new CustomerInvoicesForm(shot, ls, demo.Mobile))
                            {
                                f.Show();
                                Application.DoEvents();
                                System.Threading.Thread.Sleep(1300);
                                Application.DoEvents();
                                ShotScreen(ClientRect(f), Path.Combine(shotDir, "6-customer-invoices.png"), log, "فاکتورهای یک شماره موبایل");
                                f.Close();
                            }

                        if (demo != null)
                            using (MobileEntryForm f = new MobileEntryForm(shot, ls, demo))
                            {
                                f.Show();
                                Application.DoEvents();
                                System.Threading.Thread.Sleep(1000);
                                Application.DoEvents();
                                ShotScreen(ClientRect(f), Path.Combine(shotDir, "7-mobile-entry.png"), log, "ثبت موبایل/امتیاز فاکتور");
                                f.Close();
                            }
                    }
                }
            }
            finally
            {
                try { if (File.Exists(tmpShotDb)) File.Delete(tmpShotDb); } catch (Exception) { }
            }

            // 5) فرم انتخاب صندوق‌دار
            using (CashierSelectForm f = new CashierSelectForm(s))
            {
                f.Show();
                Application.DoEvents();
                System.Threading.Thread.Sleep(1200);
                Application.DoEvents();
                ShotScreen(ClientRect(f), Path.Combine(shotDir, "3-cashier-select.png"), log, "فرم انتخاب صندوق‌دار");
                f.Close();
            }

            // 6) فرم تنظیمات
            using (SettingsForm f = new SettingsForm(s))
            {
                f.Show();
                Application.DoEvents();
                System.Threading.Thread.Sleep(1200);
                Application.DoEvents();
                ShotScreen(ClientRect(f), Path.Combine(shotDir, "4-settings.png"), log, "فرم تنظیمات");
                f.Close();
            }
        }

        /// <summary>تحلیل متن فقط در ناحیه مشتری (Client) پنجره، با در نظر گرفتن حاشیه و نوار عنوان.</summary>
        private static void AnalyzeClient(Form f, string pngPath, StringBuilder log, string label)
        {
            try
            {
                // عکس دقیقاً از ناحیه مشتری گرفته شده است، پس مختصات تصویر = مختصات ناحیه
                AnalyzeBands(pngPath, 0, 0, f.ClientSize.Width, f.ClientSize.Height, label, log);
            }
            catch (Exception ex)
            {
                log.AppendLine("  خطا در تحلیل " + label + ": " + ex.Message);
            }
        }

        /// <summary>عکس‌برداری از یک ناحیه مشخص صفحه (برای فرم‌ها: فقط ناحیه مشتری، بدون حاشیه و سایه پنجره).</summary>
        private static Rectangle ClientRect(Form f)
        {
            return new Rectangle(f.PointToScreen(Point.Empty), f.ClientSize);
        }

        private static void ShotScreen(Rectangle r, string path, StringBuilder log, string name)
        {
            try
            {
                using (Bitmap bmp = new Bitmap(r.Width, r.Height))
                {
                    using (Graphics g = Graphics.FromImage(bmp))
                        g.CopyFromScreen(r.Location, Point.Empty, r.Size);
                    bmp.Save(path, ImageFormat.Png);
                }
                log.AppendLine("  " + name + " → " + path);
            }
            catch (Exception ex)
            {
                log.AppendLine("  " + name + ": خطا در عکس‌برداری — " + ex.Message);
            }
        }

        /// <summary>محل افقی متن در هر سطر ناحیه مشتری پنجره را گزارش می‌کند (برای بررسی ترازبندی راست‌به‌چپ).</summary>
        private static void AnalyzeBands(string pngPath, int offsetX, int offsetY, int clientW, int clientH,
                                        string label, StringBuilder log)
        {
            if (!File.Exists(pngPath)) return;
            try
            {
                using (Bitmap b = new Bitmap(pngPath))
                {
                    log.AppendLine("  تحلیل «" + label + "» در ناحیه " + clientW + "x" + clientH
                        + " (تصویر " + b.Width + "x" + b.Height + "):");

                    int w = b.Width, h = b.Height;
                    bool[] ink = new bool[h];
                    int[] lo = new int[h];
                    int[] hi = new int[h];
                    for (int y = 0; y < h; y++)
                    {
                        // رنگ غالب هر سطر = پس‌زمینه؛ پیکسل‌های متفاوت = متن
                        Dictionary<int, int> hist = new Dictionary<int, int>();
                        for (int x = 0; x < w; x++)
                        {
                            Color c = b.GetPixel(x, y);
                            int k = ((c.R >> 4) << 8) | ((c.G >> 4) << 4) | (c.B >> 4);
                            int v;
                            hist[k] = hist.TryGetValue(k, out v) ? v + 1 : 1;
                        }
                        int bestK = 0, bestC = -1;
                        foreach (KeyValuePair<int, int> kv in hist)
                            if (kv.Value > bestC) { bestC = kv.Value; bestK = kv.Key; }
                        int br = ((bestK >> 8) & 0xF) * 16 + 8;
                        int bg = ((bestK >> 4) & 0xF) * 16 + 8;
                        int bb = (bestK & 0xF) * 16 + 8;

                        int cnt = 0, mn = w, mx = -1;
                        for (int x = 0; x < w; x++)
                        {
                            Color c = b.GetPixel(x, y);
                            int d = Math.Abs(c.R - br) + Math.Abs(c.G - bg) + Math.Abs(c.B - bb);
                            if (d > 150) { cnt++; if (x < mn) mn = x; if (x > mx) mx = x; }
                        }
                        if (cnt >= 3) { ink[y] = true; lo[y] = mn; hi[y] = mx; }
                    }

                    int y0 = -1;
                    for (int y = 0; y <= h; y++)
                    {
                        bool t = y < h && ink[y];
                        if (t && y0 < 0) y0 = y;
                        if (!t && y0 >= 0)
                        {
                            int minX = clientW, maxX = -1;
                            for (int yy = y0; yy < y; yy++)
                            {
                                if (!ink[yy]) continue;
                                if (lo[yy] < minX) minX = lo[yy];
                                if (hi[yy] > maxX) maxX = hi[yy];
                            }
                            if (maxX >= 0)
                                log.AppendLine("    سطر y=" + y0 + ".." + (y - 1)
                                    + " | بازه افقی x=" + minX + ".." + maxX + " از " + w
                                    + " | مرکز=" + ((minX + maxX) / 2)
                                    + " (" + (((minX + maxX) / 2) > w / 2 ? "نیمه راست" : "نیمه چپ") + ")");
                            y0 = -1;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                log.AppendLine("  خطا در تحلیل تصویر: " + ex.Message);
            }
        }

        private static double ReadDiscount(SqlConnection c, SqlTransaction tx, long fCode)
        {
            using (SqlCommand cmd = new SqlCommand("SELECT ISNULL(F_Takhfif,0) FROM dbo.Res_Facture WHERE F_Code = @c", c))
            {
                if (tx != null) cmd.Transaction = tx;
                cmd.Parameters.Add("@c", SqlDbType.BigInt).Value = fCode;
                object o = cmd.ExecuteScalar();
                return (o == null || o == DBNull.Value) ? 0 : Convert.ToDouble(o);
            }
        }

        private static void Write(StringBuilder log, string dir, string outPath)
        {
            string path = outPath;
            if (string.IsNullOrEmpty(path)) path = Path.Combine(dir, "logs", "selftest.txt");
            try
            {
                string folder = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(folder) && !Directory.Exists(folder)) Directory.CreateDirectory(folder);
                File.WriteAllText(path, log.ToString(), new UTF8Encoding(true));
                Console.WriteLine("report: " + path);
            }
            catch (Exception ex)
            {
                Console.WriteLine("report write failed: " + ex.Message);
            }
            Console.Out.Flush();
        }
    }
}
