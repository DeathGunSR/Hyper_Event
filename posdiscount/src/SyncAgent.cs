using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;

namespace PosDiscount
{
    public class SyncRunResult
    {
        public int Pulled;
        public int Queued;
        public int Sent;
        public int Pending;
        public bool Ok;
        public string Message = "";
        public string ReconcileMessage = "";
    }

    /// <summary>خلاصهٔ یک دور تطبیق تاریخی (برای لاگ و نمایش در فرم همگام‌سازی).</summary>
    public class ReconcileReport
    {
        public DateTime At;
        public DateTime From;
        public DateTime To;
        public int Checked;
        public int Marked;
        public int Missing;
        public int Voided;
        public int Invalid;
        public int Confirmed;
        public int SkippedDays;
        public bool SourceEmpty;
        public string Message = "";

        public string ToMeta()
        {
            return "at=" + At.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
                + ";from=" + From.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                + ";to=" + To.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                + ";checked=" + Checked + ";marked=" + Marked + ";missing=" + Missing
                + ";voided=" + Voided + ";invalid=" + Invalid + ";confirmed=" + Confirmed
                + ";skippedDays=" + SkippedDays + ";sourceEmpty=" + (SourceEmpty ? 1 : 0);
        }
    }

    /// <summary>
    /// عامل همگام‌سازی: همهٔ فاکتورهای معتبر (مستقل از صندوق‌دار) را از هلو می‌خواند،
    /// در دیتابیس محلی می‌نویسد، در صف پایدار می‌گذارد و با امضای HMAC به سرور کمپین می‌فرستد.
    /// هیچ داده‌ای در هلو نوشته نمی‌شود.
    /// </summary>
    public class SyncAgent
    {
        private readonly AppSettings _s;
        private readonly HolooDb _db;
        private readonly LocalStore _store;
        private readonly ApiClient _api;
        private readonly object _runGate = new object();

        public string LastMessage = "";
        public bool LastOk;
        public DateTime LastRun;

        public SyncAgent(AppSettings s, HolooDb db, LocalStore store)
        {
            _s = s;
            _db = db;
            _store = store;
            _api = new ApiClient(s);
        }

        public SyncRunResult RunOnce()
        {
            SyncRunResult result = new SyncRunResult();
            if (!_s.SyncEnabled)
            {
                result.Message = "همگام‌سازی خاموش است.";
                LastMessage = result.Message;
                return result;
            }
            if (_store == null)
            {
                result.Message = "دیتابیس محلی باز نیست.";
                LastMessage = result.Message;
                return result;
            }

            // اگر چرخهٔ قبلی طول کشیده، این چرخه صبر می‌کند (نه اینکه دو بار ارسال کند)
            if (!Monitor.TryEnter(_runGate))
            {
                result.Message = "چرخهٔ همگام‌سازی قبلی در حال اجراست.";
                return result;
            }
            try
            {
                string pullError = "";
                try
                {
                    result.Pulled = PullNewInvoices();
                }
                catch (Exception ex)
                {
                    pullError = "خواندن فاکتورها از هلو ناموفق بود: " + ex.Message;
                    CsvLog.Error(_s, "sync pull: " + ex.ToString());
                }

                // تطبیق مستقل از ارسال است؛ خطای منبع، صف ارسال را متوقف نمی‌کند.
                result.ReconcileMessage = ReconcileIfDue();

                string pushError = "";
                try
                {
                    result.Sent = PushQueue(out result.Pending);
                }
                catch (Exception ex)
                {
                    pushError = "ارسال به سرور ناموفق بود: " + ex.Message;
                    SyncStats st = _store.GetSyncStats();
                    result.Pending = st.Pending;
                    CsvLog.Error(_s, "sync push: " + ex.ToString());
                }

                result.Ok = pullError.Length == 0 && pushError.Length == 0;
                if (pullError.Length > 0) result.Message = pullError;
                if (pushError.Length > 0)
                    result.Message = (result.Message.Length > 0 ? result.Message + " | " : "") + pushError;
                string normal = "دریافت " + result.Pulled + " فاکتور جدید | ارسال " + result.Sent
                    + " | در صف: " + result.Pending;
                if (result.Ok && result.Sent == 0 && result.Pulled == 0)
                    normal = "همه‌چیز به‌روز است (در صف: " + result.Pending + ")";
                result.Message = result.Message.Length > 0 ? result.Message + " | " + normal : normal;

                LastOk = result.Ok;
                LastMessage = result.Message;
                LastRun = DateTime.Now;
                return result;
            }
            finally
            {
                Monitor.Exit(_runGate);
            }
        }

        /// <summary>ارسال فقط صف (بدون خواندن هلو) — برای آزمون و دکمهٔ «تلاش مجدد».</summary>
        public int PushPendingOnce(out int pending)
        {
            return PushQueue(out pending);
        }

        // ------------------------------------------------------------------
        private int PullNewInvoices()
        {
            DateTime start;
            if (!DateTime.TryParseExact(_s.CampaignStartDate, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out start))
                start = DateTime.Today;

            long after;
            string stored = _store.GetMetaValue("SyncLastFCode");
            if (!long.TryParse(stored, out after) || after < 0) after = 0;

            if (after == 0)
            {
                long min = _db.GetMinFCodeSince(start);
                after = min > 0 ? min - 1 : _db.GetMaxFCodeAll();
                _store.SetMetaValue("SyncLastFCode", after.ToString(CultureInfo.InvariantCulture));
            }

            int batch = _s.SyncBatchSize;
            if (batch < 1) batch = 100;
            if (batch > 500) batch = 500;

            int pulled = 0;
            for (int guard = 0; guard < 50; guard++)
            {
                List<InvoiceInfo> list = _db.GetNewInvoicesAll(after, start, batch);
                if (list.Count == 0) break;

                long advanced = after;
                for (int i = 0; i < list.Count; i++)
                {
                    InvoiceInfo inv = list[i];
                    if (inv.FCode <= after) continue;

                    StoredInvoice rec = _store.SaveInvoice(inv, 0, _s);
                    if (rec == null)
                    {
                        // برای اینکه فاکتور خراب، بقیهٔ صف را قفل نکند، از آن عبور می‌کنیم
                        CsvLog.Info(_s, "sync: ذخیره نشد FCode=" + inv.FCode);
                        advanced = inv.FCode;
                        continue;
                    }
                    advanced = inv.FCode;
                    pulled++;

                    // حتی اگر این فاکتور از قبل در دیتابیس محلی باشد (نصب قدیمی)،
                    // یک بار در صف ارسال قرار می‌گیرد تا شمارش کمپین عقب نماند.
                    // صف/سرور هر دو ایدمپوتنت‌اند و ارسال دوباره شمارش را تکرار نمی‌کند.
                    _store.EnqueueSync(inv.FCode);
                }

                if (advanced == after) break;
                after = advanced;
                _store.SetMetaValue("SyncLastFCode", after.ToString(CultureInfo.InvariantCulture));
                if (list.Count < batch) break;
            }
            return pulled;
        }

        private int PushQueue(out int pending)
        {
            pending = 0;
            int sent = 0;
            for (int round = 0; round < 10; round++)
            {
                List<SyncItem> items = _store.GetPendingSync(100);
                if (items.Count == 0) break;

                try
                {
                    SyncPushResult r = _api.Push(items);
                    for (int i = 0; i < items.Count; i++)
                    {
                        long f = items[i].Invoice.FCode;
                        string state = "";
                        r.Statuses.TryGetValue(f, out state);
                        _store.MarkSyncSent(f, state);
                    }
                    sent += items.Count;
                }
                catch (Exception ex)
                {
                    for (int i = 0; i < items.Count; i++)
                    {
                        int retry = Math.Min(300, 10 * (items[i].Attempts + 1));
                        _store.MarkSyncFailed(items[i].Invoice.FCode, ex.Message, retry);
                    }
                    pending = _store.GetSyncStats().Pending;
                    throw;
                }

                if (items.Count < 100) break;
            }

            SyncStats st = _store.GetSyncStats();
            pending = st.Pending;
            return sent;
        }

        /// <summary>
        /// تطبیق تاریخی قابل پیکربندی. فقط در صورت موفقیت کامل منبع، وضعیت محلی را تغییر می‌دهد؛
        /// خطای اتصال یا بازهٔ خالی منبع به‌عنوان «ابطال قطعی» تفسیر نمی‌شود.
        /// </summary>
        private string ReconcileIfDue()
        {
            DateTime now = DateTime.Now;
            DateTime last;
            string raw = _store.GetMetaValue("LastReconcileAt");
            int interval = _s.ReconcileIntervalMinutes > 0 ? _s.ReconcileIntervalMinutes : 60;
            if (DateTime.TryParseExact(raw, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out last) && now - last < TimeSpan.FromMinutes(interval))
                return "";

            // در صورت خطا، حداکثر هر ۵ دقیقه تلاش مجدد؛ نه هر ۳۰ ثانیه.
            DateTime lastTry;
            string rawTry = _store.GetMetaValue("LastReconcileAttempt");
            if (DateTime.TryParseExact(rawTry, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out lastTry) && now - lastTry < TimeSpan.FromMinutes(5))
                return "";
            _store.SetMetaValue("LastReconcileAttempt", now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));

            int days = _s.ReconcileDays > 0 ? _s.ReconcileDays : 7;
            if (days > 30) days = 30;
            int confirmations = _s.ReconcileConfirmations > 0 ? _s.ReconcileConfirmations : 2;
            if (confirmations > 5) confirmations = 5;

            try
            {
                DateTime end = _db.GetWorkDay().Date;
                DateTime campaignStart;
                if (!DateTime.TryParseExact(_s.CampaignStartDate, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out campaignStart))
                    campaignStart = end.AddDays(-(days - 1));
                DateTime start = end.AddDays(-(days - 1));
                if (start < campaignStart) start = campaignStart;

                if (end < start)
                    return "تطبیق انجام نشد: بازهٔ تاریخ نامعتبر است.";

                SourceSnapshot snap = _db.GetSourceSnapshot(start, end);
                ReconcileReport rep = Reconcile(_store, start, end, snap, confirmations, now);
                _store.SetMetaValue("LastReconcileSummary", rep.ToMeta());

                if (rep.SourceEmpty)
                {
                    string emptyMsg = "منبع هلو برای بازهٔ " + Fmt(start) + " تا " + Fmt(end)
                        + " هیچ فاکتوری برنگرداند؛ هیچ تغییری اعمال نشد.";
                    _store.SetMetaValue("LastReconcileError", emptyMsg);
                    CsvLog.Info(_s, "sync reconcile: " + emptyMsg);
                    return emptyMsg;
                }

                _store.SetMetaValue("LastReconcileAt", now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
                _store.SetMetaValue("LastReconcileError", "");
                CsvLog.Info(_s, "sync reconcile: " + rep.Message);
                return rep.Message;
            }
            catch (Exception ex)
            {
                string msg = "تطبیق تاریخی ناموفق بود (هیچ تغییری اعمال نشد): " + ex.Message;
                _store.SetMetaValue("LastReconcileError", msg);
                CsvLog.Error(_s, "sync reconcile: " + ex.ToString());
                return msg;
            }
        }

        private static string Fmt(DateTime d)
        {
            return d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// مقایسهٔ محلی با عکس فوری منبع. «غایب بودن» فقط با چند مشاهدهٔ پیوسته بررسی می‌شود؛
        /// نوع ۳ ابطال قطعی و نوع نامعتبر/مبلغ غیرمثبت «نامعتبر» است. هیچ حذفی انجام نمی‌شود.
        /// </summary>
        public static ReconcileReport Reconcile(LocalStore store, DateTime from, DateTime to,
            SourceSnapshot snap, int confirmations, DateTime now)
        {
            ReconcileReport rep = new ReconcileReport();
            rep.At = now;
            rep.From = from.Date;
            rep.To = to.Date;
            if (store == null) { rep.Message = "دیتابیس محلی باز نیست."; return rep; }
            if (confirmations < 1) confirmations = 1;
            if (confirmations > 5) confirmations = 5;

            List<ReconcileRow> local = store.GetReconcileRows(Fmt(from), Fmt(to));
            HashSet<string> skippedDays = new HashSet<string>();
            string window = Fmt(from) + ".." + Fmt(to);

            for (int i = 0; i < local.Count; i++)
            {
                ReconcileRow row = local[i];
                rep.Checked++;

                SourceInvoiceState src = null;
                bool found = snap != null && snap.ByCode.TryGetValue(row.FCode, out src);
                string baseEvidence = "source=Holoo1;window=" + window + ";day=" + row.FDate
                    + ";at=" + Fmt(now) + " " + now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);

                if (found && src.IsValidSale)
                {
                    if (string.Equals(row.Status, "review", StringComparison.OrdinalIgnoreCase))
                    {
                        rep.Confirmed++;
                        store.SetReviewEvidence(row.FCode, baseEvidence + ";state=confirmed-valid;table=" + src.TableLabel);
                    }
                    else if (row.MissingChecks > 0)
                    {
                        store.ClearMissingProbe(row.FCode);
                    }
                    continue;
                }

                if (found && src.IsVoided)
                {
                    rep.Voided++;
                    if (store.SetReview(row.FCode, "voided",
                        baseEvidence + ";state=voided;type=3;table=" + src.TableLabel))
                    {
                        store.EnqueueSync(row.FCode);
                        rep.Marked++;
                    }
                    continue;
                }

                if (found)
                {
                    rep.Invalid++;
                    if (store.SetReview(row.FCode, "invalid",
                        baseEvidence + ";state=invalid;type=" + src.FType + ";gross=" + src.Gross.ToString("0.##", CultureInfo.InvariantCulture)
                        + ";table=" + src.TableLabel))
                    {
                        store.EnqueueSync(row.FCode);
                        rep.Marked++;
                    }
                    continue;
                }

                // کد در هیچ‌کدام از دو جدول پیدا نشد. اگر آن روز در منبع هیچ ردیفی نداشته باشد،
                // منبع را ناقص می‌دانیم و برای آن روز هیچ تصمیمی نمی‌گیریم.
                if (snap == null || !snap.DaysWithRows.Contains(row.FDate))
                {
                    skippedDays.Add(row.FDate);
                    continue;
                }

                rep.Missing++;
                if (string.Equals(row.Status, "review", StringComparison.OrdinalIgnoreCase))
                {
                    if (!string.Equals(row.ReviewReason, "missing", StringComparison.OrdinalIgnoreCase))
                        store.SetReview(row.FCode, "missing", baseEvidence + ";state=missing;checks=already-review");
                    continue;
                }

                int checks = row.MissingChecks + 1;
                if (checks >= confirmations)
                {
                    if (store.SetReview(row.FCode, "missing",
                        baseEvidence + ";state=missing;checks=" + checks + ";confirmation=reached"))
                    {
                        store.EnqueueSync(row.FCode);
                        rep.Marked++;
                    }
                }
                else
                {
                    store.SetMissingProbe(row.FCode, checks,
                        baseEvidence + ";state=missing-pending;checks=" + checks + "/" + confirmations);
                }
            }

            rep.SkippedDays = skippedDays.Count;
            rep.SourceEmpty = snap == null || (snap.LiveRows == 0 && snap.ArchiveRows == 0);
            rep.Message = "تطبیق " + Fmt(from) + " تا " + Fmt(to) + ": بررسی " + rep.Checked
                + " | جدید بررسی: " + rep.Marked + " | غایب: " + rep.Missing
                + " | ابطال: " + rep.Voided + " | نامعتبر: " + rep.Invalid
                + " | تأییدشده: " + rep.Confirmed
                + (rep.SkippedDays > 0 ? " | روزهای ناقص: " + rep.SkippedDays : "");
            return rep;
        }
    }
}
