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
                try
                {
                    result.Pulled = PullNewInvoices();
                    ReconcileIfDue();
                }
                catch (Exception ex)
                {
                    result.Ok = false;
                    result.Message = "خواندن فاکتورها از هلو ناموفق بود: " + ex.Message;
                    LastOk = false;
                    LastMessage = result.Message;
                    LastRun = DateTime.Now;
                    return result;
                }

                try
                {
                    result.Sent = PushQueue(out result.Pending);
                    result.Ok = true;
                    result.Message = "دریافت " + result.Pulled + " فاکتور جدید | ارسال " + result.Sent
                        + " | در صف: " + result.Pending;
                    if (result.Sent == 0 && result.Pulled == 0)
                        result.Message = "همه‌چیز به‌روز است (در صف: " + result.Pending + ")";
                }
                catch (Exception ex)
                {
                    result.Ok = false;
                    SyncStats st = _store.GetSyncStats();
                    result.Pending = st.Pending;
                    result.Message = "ارسال به سرور ناموفق بود: " + ex.Message;
                }

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
        /// تطبیق روزانه (حداکثر هر ۶۰ دقیقه): فاکتورهای محلی که در منبع نیستند «مشکوک به ابطال» می‌شوند.
        /// این کار در دوره‌های بسته، شمارش تاریخی را تغییر نمی‌دهد و فقط برای بررسی مدیریتی است.
        /// </summary>
        private void ReconcileIfDue()
        {
            DateTime last;
            string raw = _store.GetMetaValue("LastReconcileAt");
            if (DateTime.TryParseExact(raw, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out last) && DateTime.Now - last < TimeSpan.FromMinutes(60))
                return;

            DateTime day = _db.GetWorkDay();
            HashSet<long> source = _db.GetInvoiceCodesForDay(day);
            int marked = MarkMissingAsReview(_store, day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), source);
            _store.SetMetaValue("LastReconcileAt", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
            if (marked > 0)
                CsvLog.Info(_s, "sync reconcile: " + marked + " فاکتور مشکوک به ابطال علامت خورد (روز "
                    + day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ")");
        }

        /// <summary>
        /// تطبیق: فاکتورهای محلی یک روز که در منبع نیستند، «مشکوک به ابطال» علامت می‌خورند
        /// و دوباره در صف ارسال قرار می‌گیرند تا سرور آن‌ها را NeedsReview ثبت کند.
        /// حذف فیزیکی انجام نمی‌شود.
        /// </summary>
        public static int MarkMissingAsReview(LocalStore store, string dayYmd, HashSet<long> sourceCodes)
        {
            if (store == null) return 0;
            List<StoredInvoice> local = store.GetInvoicesOnDay(dayYmd);
            int marked = 0;
            for (int i = 0; i < local.Count; i++)
            {
                if (sourceCodes != null && sourceCodes.Contains(local[i].FCode)) continue;
                // یک بار علامت بزن و همان یک بار را به سرور بفرست؛ نه هر ساعت دوباره.
                if (string.Equals(local[i].Status, "review", StringComparison.OrdinalIgnoreCase)) continue;
                store.SetInvoiceStatus(local[i].FCode, "review");
                store.EnqueueSync(local[i].FCode);
                marked++;
            }
            return marked;
        }
    }
}
