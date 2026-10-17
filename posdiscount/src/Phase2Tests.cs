using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace PosDiscount
{
    internal static partial class SelfTest
    {
        /// <summary>
        /// آزمون تطبیق تاریخی: تفکیک غایب/ابطال/نامعتبر، تأیید دو مرحله‌ای غیبت،
        /// روز ناقص، بازگشت منبع و جلوگیری از تغییر تکراری.
        /// </summary>
        public static void Phase2ReconcileTests(AppSettings s, StringBuilder log)
        {
            log.AppendLine();
            log.AppendLine("--- فاز ۲: تطبیق تاریخی چندروزه ---");
            string dir = Path.Combine(Path.GetTempPath(), "posdisc-phase2-" + Guid.NewGuid().ToString("N"));
            LocalStore store = null;
            try
            {
                Directory.CreateDirectory(dir);
                AppSettings t = s.Clone();
                t.LocalDbFile = Path.Combine(dir, "reconcile.db");
                t.CampaignStartDate = "2026-02-01";
                store = new LocalStore(t.LocalDbFile);
                string err;
                Expect(log, "باز شدن دیتابیس تطبیق", store.Open(out err) ? 1 : 0, 1);

                DateTime day = DateTime.Today;
                DateTime older = day.AddDays(-1);
                SaveLocal(store, t, 8101, day, 5000000);
                SaveLocal(store, t, 8102, day, 5000000);
                SaveLocal(store, t, 8103, day, 5000000);
                SaveLocal(store, t, 8104, day, 5000000);
                SaveLocal(store, t, 8105, older, 5000000);

                SourceSnapshot snap = new SourceSnapshot();
                snap.From = older;
                snap.To = day;
                snap.DaysWithRows.Add(FmtDate.G2(day));
                AddSourceOn(snap, 8101, 2, 5000000, day);
                AddSourceOn(snap, 8102, 3, 5000000, day);
                AddSourceOn(snap, 8103, 1, 5000000, day);
                snap.LiveRows = 3;

                ReconcileReport r1 = SyncAgent.Reconcile(store, older, day, snap, 2, DateTime.Now);
                ExpectStr(log, "نوع ۳ فوراً ابطال علامت می‌خورد", store.Read(8102).Status, "review");
                ExpectStr(log, "دلیل ابطال = voided", store.Read(8102).ReviewReason, "voided");
                ExpectStr(log, "نوع نامعتبر فوراً بررسی می‌شود", store.Read(8103).Status, "review");
                ExpectStr(log, "دلیل نامعتبر = invalid", store.Read(8103).ReviewReason, "invalid");
                ExpectStr(log, "غیبت اول وضعیت را تغییر نمی‌دهد", store.Read(8104).Status, "valid");
                ExpectStr(log, "روز بدون ردیف منبع نادیده گرفته می‌شود", store.Read(8105).Status, "valid");
                Expect(log, "روز ناقص در گزارش شمرده شد", r1.SkippedDays, 1);
                Expect(log, "دو بررسی قطعی در دور اول", r1.Marked, 2);

                ReconcileReport r2 = SyncAgent.Reconcile(store, older, day, snap, 2, DateTime.Now);
                ExpectStr(log, "غیبت دوم وارد بررسی می‌شود", store.Read(8104).Status, "review");
                ExpectStr(log, "دلیل غیبت = missing", store.Read(8104).ReviewReason, "missing");
                Expect(log, "یک بررسی جدید در دور دوم", r2.Marked, 1);

                ReconcileReport r3 = SyncAgent.Reconcile(store, older, day, snap, 2, DateTime.Now);
                Expect(log, "دور تکراری بررسی جدیدی نمی‌سازد", r3.Marked, 0);
                Expect(log, "فاکتور غایب در صف ارسال است", store.IsQueued(8104) ? 1 : 0, 1);

                // منبع دوباره فاکتور را تأیید می‌کند: وضعیت بررسی خودکار باز نمی‌شود،
                // اما شاهد «confirmed-valid» برای تصمیم مدیر ثبت می‌شود.
                AddSourceOn(snap, 8104, 2, 5000000, day);
                ReconcileReport r4 = SyncAgent.Reconcile(store, older, day, snap, 2, DateTime.Now);
                ExpectStr(log, "تأیید منبع وضعیت بررسی را خودکار باز نمی‌کند", store.Read(8104).Status, "review");
                Expect(log, "شاهد تأیید منبع ثبت شد",
                    store.Read(8104).ReviewEvidence.IndexOf("confirmed-valid", StringComparison.Ordinal) >= 0 ? 1 : 0, 1);
                Expect(log, "دور تأیید، بررسی جدید نمی‌سازد", r4.Marked, 0);

                // غیبت موقت سپس تأیید: شمارندهٔ غیبت باید پاک شود.
                SaveLocal(store, t, 8106, day, 5000000);
                SourceSnapshot snap2 = new SourceSnapshot();
                snap2.From = older;
                snap2.To = day;
                snap2.DaysWithRows.Add(FmtDate.G2(day));
                AddSourceOn(snap2, 8101, 2, 5000000, day);
                snap2.LiveRows = 1;
                SyncAgent.Reconcile(store, older, day, snap2, 2, DateTime.Now);
                Expect(log, "غیبت موقت شمارنده ۱ دارد", MissingChecks(store, 8106, day), 1);
                AddSourceOn(snap2, 8106, 2, 5000000, day);
                SyncAgent.Reconcile(store, older, day, snap2, 2, DateTime.Now);
                Expect(log, "تأیید منبع شمارندهٔ غیبت را پاک می‌کند", MissingChecks(store, 8106, day), 0);
                ExpectStr(log, "فاکتور تأییدشده در وضعیت معتبر می‌ماند", store.Read(8106).Status, "valid");

                // منبع خالی: هیچ تغییری نباید اعمال شود.
                SaveLocal(store, t, 8107, day, 5000000);
                SourceSnapshot empty = new SourceSnapshot();
                empty.From = older;
                empty.To = day;
                ReconcileReport r5 = SyncAgent.Reconcile(store, older, day, empty, 2, DateTime.Now);
                Expect(log, "منبع خالی علامت‌گذاری می‌شود", r5.SourceEmpty ? 1 : 0, 1);
                ExpectStr(log, "منبع خالی وضعیت را تغییر نمی‌دهد", store.Read(8107).Status, "valid");
                Expect(log, "منبع خالی بررسی جدید نمی‌سازد", r5.Marked, 0);

                // مسیر واقعی SQL روی جدول روز + تاریخچه (فقط خواندنی).
                HolooDb live = new HolooDb(s.BuildConnectionString());
                SourceSnapshot real = live.GetSourceSnapshot(new DateTime(2026, 2, 8), new DateTime(2026, 2, 9));
                Expect(log, "عکس فوری منبع، ردیف‌های جدول روز را خواند", real.LiveRows >= 160 ? 1 : 0, 1);
                Expect(log, "عکس فوری منبع، ردیف‌های تاریخچه را خواند", real.ArchiveRows > 0 ? 1 : 0, 1);
                Expect(log, "عکس فوری منبع، دو روز را پوشش داد", real.DaysWithRows.Count >= 2 ? 1 : 0, 1);

                // قطعی هلو نباید ارسال صف به سرور را متوقف کند.
                AppSettings offline = s.Clone();
                offline.LocalDbFile = Path.Combine(dir, "offline.db");
                offline.Server = "127.0.0.1,59998";
                offline.ConnectTimeoutSeconds = 2;
                offline.SyncEnabled = true;
                offline.ApiAgentId = "agent-a";
                offline.ApiAgentSecret = "secret-a";
                LocalStore offlineStore = new LocalStore(offline.LocalDbFile);
                TestHttpServer server = null;
                try
                {
                    string oerr;
                    offlineStore.Open(out oerr);
                    offlineStore.SaveInvoice(Fake(8201, 5000000, "09120008201", "مشتری آفلاین"), 0, offline);
                    server = new TestHttpServer(200,
                        "{\"requestId\":\"offline-1\",\"accepted\":1,\"duplicates\":0,\"updated\":0,\"held\":0,"
                        + "\"needsReview\":0,\"outOfScope\":0,\"conflicts\":0,\"cached\":false,"
                        + "\"round\":{\"roundId\":1,\"roundNo\":1,\"status\":\"Collecting\",\"target\":1000,\"count\":1,\"remaining\":999,\"held\":0},"
                        + "\"results\":[{\"fCode\":8201,\"result\":\"accepted\",\"status\":\"Valid\",\"roundId\":1}]}");
                    offline.ApiBaseUrl = server.Url;
                    SyncAgent agent = new SyncAgent(offline, new HolooDb(offline.BuildConnectionString()), offlineStore);
                    SyncRunResult run = agent.RunOnce();
                    Expect(log, "قطعی هلو باعث استثنا نمی‌شود", run != null ? 1 : 0, 1);
                    Expect(log, "ارسال صف در قطعی هلو ادامه یافت", run.Sent, 1);
                    Expect(log, "صف پس از ارسال خالی شد", offlineStore.GetSyncStats().Pending, 0);
                    Expect(log, "پیام خطای منبع حفظ شد",
                        run.Message.IndexOf("هلو", StringComparison.Ordinal) >= 0 ? 1 : 0, 1);
                }
                finally
                {
                    if (server != null) server.Dispose();
                    offlineStore.Dispose();
                }
            }
            catch (Exception ex)
            {
                log.AppendLine("  [FAIL] آزمون تطبیق تاریخی: " + ex.Message);
                _fails++;
            }
            finally
            {
                if (store != null) { try { store.Dispose(); } catch (Exception) { } }
                try { Directory.Delete(dir, true); } catch (Exception) { }
            }
        }

        private static void SaveLocal(LocalStore store, AppSettings s, long fCode, DateTime day, double gross)
        {
            InvoiceInfo inv = Fake(fCode, gross, "", "مشتری تطبیق");
            inv.Date = day;
            inv.JalaliDate = FmtDate.G(day);
            store.SaveInvoice(inv, 0, s);
        }

        private static void AddSourceOn(SourceSnapshot snap, long fCode, int fType, double gross, DateTime day)
        {
            SourceInvoiceState st = new SourceInvoiceState();
            st.FCode = fCode;
            st.FCodeC = fCode;
            st.FType = fType;
            st.Date = day;
            st.Gross = gross;
            st.FromLive = true;
            snap.ByCode[fCode] = st;
        }

        private static int MissingChecks(LocalStore store, long fCode, DateTime day)
        {
            List<ReconcileRow> rows = store.GetReconcileRows(FmtDate.G2(day), FmtDate.G2(day));
            for (int i = 0; i < rows.Count; i++)
                if (rows[i].FCode == fCode) return rows[i].MissingChecks;
            return -1;
        }
    }
}
