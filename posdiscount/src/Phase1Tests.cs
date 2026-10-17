using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace PosDiscount
{
    internal static partial class SelfTest
    {
        /// <summary>
        /// آزمون‌های عامل همگام‌سازی: مهاجرت نسخه ۴، صف پایدار، مالکیت شماره،
        /// جلوگیری از پاپ‌آپ تکراری، امضای HMAC و ارسال واقعی به یک سرور آزمایشی محلی.
        /// </summary>
        public static void Phase1AgentTests(AppSettings s, StringBuilder log)
        {
            log.AppendLine();
            log.AppendLine("--- فاز ۱: صف همگام‌سازی، مالکیت موبایل و امضای درخواست ---");

            string dir = Path.Combine(Path.GetTempPath(), "posdisc-phase1-" + Guid.NewGuid().ToString("N"));
            try { Directory.CreateDirectory(dir); }
            catch (Exception ex) { log.AppendLine("  [FAIL] ساخت پوشهٔ موقت: " + ex.Message); _fails++; return; }

            AppSettings t = s.Clone();
            t.FilePath = Path.Combine(dir, "settings.xml");
            t.LocalDbFile = Path.Combine(dir, "phase1.db");
            t.SyncEnabled = true;
            t.ApiAgentId = "agent-a";
            t.ApiAgentSecret = "secret-a";
            t.CampaignStartDate = "2026-02-09";

            string dbPath = t.LocalDbFile;
            LocalStore store = new LocalStore(dbPath);
            try
            {
                string err;
                Expect(log, "باز شدن دیتابیس محلی نسخه ۴", store.Open(out err) ? 1 : 0, 1);
                Expect(log, "نسخهٔ اسکیما = ۴", Num.ParseI(store.GetMetaValue("SchemaVersion"), 0), 4);

                StoredInvoice inv = store.SaveInvoice(Fake(7001, 5000000, "09120007001", "مشتری تست"), 0, t);
                Expect(log, "ذخیرهٔ فاکتور در دیتابیس محلی", inv == null ? -1 : 1, 1);
                Expect(log, "فاکتور تازه خودکار در صف ارسال", store.GetSyncStats().Pending, 1);

                // بازگشایی: صف باید پایدار بماند
                store.Dispose();
                store = new LocalStore(dbPath);
                Expect(log, "بازگشایی دیتابیس", store.Open(out err) ? 1 : 0, 1);
                Expect(log, "صف پس از بسته‌شدن برنامه حفظ شد", store.GetSyncStats().Pending, 1);

                store.MarkSyncSent(7001, "Valid");
                Expect(log, "ارسال موفق صف را خالی می‌کند", store.GetSyncStats().Pending, 0);
                ExpectStr(log, "وضعیت سرور روی فاکتور ذخیره شد", store.Read(7001).ServerStatus, "Valid");

                store.SaveInvoice(Fake(7001, 5000000, "", "مشتری تست"), 0, t);
                Expect(log, "ذخیرهٔ تکراری بدون تغییر، صف را بازنمی‌کند", store.GetSyncStats().Pending, 0);

                StoredInvoice owned = store.SetMobile(7001, "09121234567", "مشتری تست", t, 12);
                Expect(log, "ثبت موبایل", owned == null ? -1 : 1, 1);
                ExpectStr(log, "شمارهٔ یکسان‌شده", owned == null ? "" : owned.Mobile, "09121234567");
                Expect(log, "هویت ثبت‌کنندهٔ شماره", owned == null ? -1 : owned.MobileEnteredBy, 12);
                Expect(log, "تغییر موبایل دوباره در صف می‌نشیند", store.GetSyncStats().Pending, 1);

                Expect(log, "پاپ‌آپ هنوز نمایش داده نشده", store.WasPopupShown(7001) ? 1 : 0, 0);
                store.MarkPopupShown(7001);
                Expect(log, "علامت نمایش پاپ‌آپ ذخیره شد", store.WasPopupShown(7001) ? 1 : 0, 1);

                store.SaveInvoice(Fake(7002, 5000000, "09120007002", "مشتری ۲"), 0, t);
                store.SaveInvoice(Fake(7003, 5000000, "09120007003", "مشتری ۳"), 0, t);
                HashSet<long> source = new HashSet<long>();
                source.Add(7001);
                source.Add(7002);
                int marked = SyncAgent.MarkMissingAsReview(store, FmtDate.G2(DateTime.Today), source);
                Expect(log, "تطبیق روزانه: یک فاکتور غایب مشکوک شد", marked, 1);
                ExpectStr(log, "وضعیت فاکتور غایب", store.Read(7003).Status, "review");
                ExpectStr(log, "فاکتور موجود دست‌نخورده", store.Read(7002).Status, "valid");
                int markedAgain = SyncAgent.MarkMissingAsReview(store, FmtDate.G2(DateTime.Today), source);
                Expect(log, "تطبیق تکراری صف را دوباره پر نمی‌کند", markedAgain, 0);
            }
            finally
            {
                try { store.Dispose(); } catch (Exception) { }
            }

            // ---------------- امضای HMAC با بردار استاندارد ----------------
            log.AppendLine();
            log.AppendLine("  --- امضای HMAC-SHA256 ---");
            ExpectStr(log, "بردار استاندارد RFC (key/quick brown fox)",
                ApiClient.HmacHex("key", "The quick brown fox jumps over the lazy dog"),
                "f7bc83f430538424b13298e6aa6fb143ef4d59a14946175997479dbc2d1a3cd8");

            // ---------------- رمزنگاری کلید عامل -------------\n            log.AppendLine();
            log.AppendLine("  --- محافظت از کلید عامل در فایل تنظیمات ---");
            try
            {
                t.Save();
                string xml = File.ReadAllText(t.FilePath);
                Expect(log, "کلید عامل به‌صورت متن ساده ذخیره نمی‌شود",
                    xml.IndexOf("secret-a", StringComparison.Ordinal) < 0 ? 1 : 0, 1);
                AppSettings reloaded = AppSettings.Load(t.FilePath);
                ExpectStr(log, "کلید رمزشده درست بازخوانی می‌شود", reloaded.ApiAgentSecret, "secret-a");
            }
            catch (Exception ex)
            {
                log.AppendLine("  [FAIL] رمزنگاری کلید: " + ex.Message);
                _fails++;
            }

            // ---------------- ارسال واقعی به سرور آزمایشی ----------------
            log.AppendLine();
            log.AppendLine("  --- ارسال واقعی به سرور آزمایشی محلی (HTTP) ---");
            string db2 = Path.Combine(dir, "push.db");
            LocalStore pushStore = new LocalStore(db2);
            TestHttpServer server = null;
            try
            {
                string err;
                pushStore.Open(out err);
                pushStore.SaveInvoice(Fake(8001, 5000000, "09120008001", "مشتری ارسال"), 0, t);
                pushStore.SetInvoiceStatus(8001, "review");
                pushStore.EnqueueSync(8001);

                server = new TestHttpServer(200,
                    "{\"requestId\":\"r1\",\"accepted\":1,\"duplicates\":0,\"updated\":0,\"held\":0,"
                    + "\"needsReview\":0,\"outOfScope\":0,\"conflicts\":0,\"cached\":false,"
                    + "\"round\":{\"roundId\":1,\"roundNo\":1,\"status\":\"Collecting\",\"target\":1000,\"count\":1,\"remaining\":999,\"held\":0},"
                    + "\"results\":[{\"fCode\":8001,\"result\":\"accepted\",\"status\":\"Valid\",\"roundId\":1}]}");

                AppSettings t2 = t.Clone();
                t2.ApiBaseUrl = server.Url;
                SyncAgent agent = new SyncAgent(t2, new HolooDb(t2.BuildConnectionString()), pushStore);
                int pending;
                int sent = agent.PushPendingOnce(out pending);

                Expect(log, "یک فاکتور ارسال شد", sent, 1);
                Expect(log, "صف پس از ارسال خالی است", pending, 0);
                ExpectStr(log, "سرور، شناسهٔ عامل را دید", server.LastAgentId, "agent-a");
                Expect(log, "امضای درخواست در سرور معتبر بود", server.SignatureValid ? 1 : 0, 1);
                Expect(log, "بدنه شامل همان فاکتور بود", server.Body.IndexOf("\"fCode\":8001", StringComparison.Ordinal) >= 0 ? 1 : 0, 1);
                Expect(log, "وضعیت مشکوک به ابطال در بدنه ارسال شد",
                    server.Body.IndexOf("\"status\":\"review\"", StringComparison.Ordinal) >= 0 ? 1 : 0, 1);
                ExpectStr(log, "وضعیت سرور برای فاکتور ذخیره شد", pushStore.Read(8001).ServerStatus, "Valid");

                // خطای سرور: باید ناموفق بماند و پیام خطا ذخیره شود
                TestHttpServer bad = new TestHttpServer(500, "{\"code\":\"boom\",\"message\":\"سرور در دسترس نیست\"}");
                try
                {
                    pushStore.SaveInvoice(Fake(8002, 5000000, "09120008002", "مشتری خطا"), 0, t2);
                    AppSettings t3 = t2.Clone();
                    t3.ApiBaseUrl = bad.Url;
                    SyncAgent badAgent = new SyncAgent(t3, new HolooDb(t3.BuildConnectionString()), pushStore);
                    try
                    {
                        badAgent.PushPendingOnce(out pending);
                        Expect(log, "خطای HTTP باعث استثنا می‌شود", 0, 1);
                    }
                    catch (Exception)
                    {
                        Expect(log, "خطای HTTP باعث استثنا می‌شود", 1, 1);
                    }
                    SyncStats st = pushStore.GetSyncStats();
                    Expect(log, "فاکتور ناموفق در صف می‌ماند", st.Pending, 1);
                    Expect(log, "پیام خطای سرور ذخیره شد",
                        st.LastError.IndexOf("سرور در دسترس نیست", StringComparison.Ordinal) >= 0 ? 1 : 0, 1);
                }
                finally
                {
                    bad.Dispose();
                }
            }
            catch (Exception ex)
            {
                log.AppendLine("  [FAIL] آزمون ارسال: " + ex.Message);
                _fails++;
            }
            finally
            {
                if (server != null) server.Dispose();
                try { pushStore.Dispose(); } catch (Exception) { }
            }

            // ---------------- خواندن همهٔ صندوق‌دارها از منبع واقعی ----------------
            log.AppendLine();
            log.AppendLine("  --- خواندن فاکتورهای همهٔ صندوق‌دارها از هلو (فقط خواندنی) ---");
            try
            {
                HolooDb live = new HolooDb(s.BuildConnectionString());
                DateTime start = new DateTime(2026, 2, 9);
                long min = live.GetMinFCodeSince(start);
                long max = live.GetMaxFCodeAll();
                Expect(log, "کمترین FCode از شروع کمپین > 0", min > 0 ? 1 : 0, 1);
                Expect(log, "بزرگ‌ترین FCode شامل جدول روز و تاریخچه", max >= 590669 ? 1 : 0, 1);

                List<InvoiceInfo> sample = live.GetNewInvoicesAll(min - 1, start, 200);
                Expect(log, "نمونهٔ فاکتورهای همهٔ صندوق‌دارها خوانده شد", sample.Count > 0 ? 1 : 0, 1);
                Dictionary<int, bool> users = new Dictionary<int, bool>();
                for (int i = 0; i < sample.Count; i++) users[sample[i].UserCode] = true;
                Expect(log, "نمونه حداقل از دو صندوق‌دار است", users.Count >= 2 ? 1 : 0, 1);

                bool ascending = true;
                for (int i = 1; i < sample.Count; i++)
                    if (sample[i].FCode <= sample[i - 1].FCode) ascending = false;
                Expect(log, "ترتیب صعودی FCode برای وااترمارک", ascending ? 1 : 0, 1);

                // مسیر تاریخچه (Rs_Facture): روزی را می‌خوانیم که فقط در آرشیو است.
                DateTime archiveDay = new DateTime(2026, 2, 8);
                List<InvoiceInfo> archiveSample = live.GetNewInvoicesAll(0, archiveDay, 5);
                bool fromArchive = false;
                for (int i = 0; i < archiveSample.Count; i++)
                    if (archiveSample[i].FCode < min) fromArchive = true;
                Expect(log, "خواندن فاکتور از جدول تاریخچه", fromArchive ? 1 : 0, 1);
                Expect(log, "کدهای معتبر یک روز از تاریخچه", live.GetInvoiceCodesForDay(archiveDay).Count > 0 ? 1 : 0, 1);
            }
            catch (Exception ex)
            {
                log.AppendLine("  [FAIL] آزمون زندهٔ هلو: " + ex.Message);
                _fails++;
            }

            // ---------------- همگام‌سازی واقعی: خواندن از هلو و پرکردن صف (بدون سرور) ----------------
            log.AppendLine();
            log.AppendLine("  --- اولین همگام‌سازی: همهٔ فاکتورهای کمپین باید در صف باشند ---");
            LocalStore pullStore = null;
            try
            {
                AppSettings t4 = t.Clone();
                t4.LocalDbFile = Path.Combine(dir, "pull.db");
                t4.SyncEnabled = true;
                t4.ApiBaseUrl = "http://127.0.0.1:59999";
                t4.ApiTimeoutSeconds = 5;
                t4.CampaignStartDate = "2026-02-09";

                pullStore = new LocalStore(t4.LocalDbFile);
                string perr;
                pullStore.Open(out perr);
                HolooDb hdb = new HolooDb(t4.BuildConnectionString());

                // شبیه‌سازی نصب قدیمی: چند فاکتور از قبل در دیتابیس محلی هستند و در صف نیستند.
                List<InvoiceInfo> pre = hdb.GetNewInvoicesAll(0, new DateTime(2026, 2, 9), 5);
                long probe = 0;
                for (int i = 0; i < pre.Count; i++)
                {
                    pullStore.SaveInvoice(pre[i], 0, t4);
                    pullStore.MarkSyncSent(pre[i].FCode, "Valid");
                    if (i == 0) probe = pre[i].FCode;
                }
                Expect(log, "پیش‌آماده‌سازی نصب قدیمی (بدون صف)", pullStore.GetSyncStats().Pending, 0);

                SyncAgent agent = new SyncAgent(t4, hdb, pullStore);
                SyncRunResult run = agent.RunOnce();

                Expect(log, "همگام‌سازی واقعی فاکتورهای کمپین را خواند", run.Pulled > 0 ? 1 : 0, 1);
                Expect(log, "همهٔ فاکتورهای خوانده‌شده در صف قرار گرفتند",
                    pullStore.GetSyncStats().Pending, run.Pulled);
                Expect(log, "فاکتور قدیمی موجود هم برای شمارش به صف رفت",
                    pullStore.IsQueued(probe) ? 1 : 0, 1);
                Expect(log, "ارسال ناموفق، صف را از بین نمی‌برد", run.Ok ? 0 : 1, 1);
            }
            catch (Exception ex)
            {
                log.AppendLine("  [FAIL] آزمون همگام‌سازی واقعی: " + ex.Message);
                _fails++;
            }
            finally
            {
                if (pullStore != null) { try { pullStore.Dispose(); } catch (Exception) { } }
            }

            try { Directory.Delete(dir, true); } catch (Exception) { }
        }

        /// <summary>سرور HTTP حداقلی روی 127.0.0.1 برای آزمون امضا و ارسال (بدون نیاز به مجوز URL).</summary>
        private sealed class TestHttpServer : IDisposable
        {
            private readonly TcpListener _listener;
            private readonly Thread _thread;
            private readonly int _status;
            private readonly string _response;
            private volatile bool _stop;

            public string Url = "";
            public string Body = "";
            public string LastAgentId = "";
            public bool SignatureValid;

            public TestHttpServer(int status, string response)
            {
                _status = status;
                _response = response;
                _listener = new TcpListener(IPAddress.Loopback, 0);
                _listener.Start();
                int port = ((IPEndPoint)_listener.LocalEndpoint).Port;
                Url = "http://127.0.0.1:" + port;
                _thread = new Thread(new ThreadStart(Loop));
                _thread.IsBackground = true;
                _thread.Start();
            }

            private void Loop()
            {
                try
                {
                    while (!_stop)
                    {
                        TcpClient client = _listener.AcceptTcpClient();
                        try
                        {
                            using (client)
                            {
                                client.ReceiveTimeout = 10000;
                                NetworkStream ns = client.GetStream();
                                byte[] data = ReadRequest(ns);
                                string text = Encoding.UTF8.GetString(data);
                                int headerEnd = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                                string headers = headerEnd >= 0 ? text.Substring(0, headerEnd) : text;
                                Body = headerEnd >= 0 ? text.Substring(headerEnd + 4) : "";

                                LastAgentId = Header(headers, "X-Agent-Id");
                                string ts = Header(headers, "X-Timestamp");
                                string sig = Header(headers, "X-Signature");
                                string path = "/api/v1/ingest/invoices";
                                string canonical = ApiClient.Canonical(LastAgentId, ts, "POST", path,
                                    ApiClient.Sha256Hex(Encoding.UTF8.GetBytes(Body)));
                                string expected = ApiClient.HmacHex("secret-a", canonical);
                                SignatureValid = string.Equals(expected, sig, StringComparison.OrdinalIgnoreCase);

                                byte[] payload = Encoding.UTF8.GetBytes(_response);
                                string head = "HTTP/1.1 " + _status + " " + (_status == 200 ? "OK" : "Error") + "\r\n"
                                    + "Content-Type: application/json; charset=utf-8\r\n"
                                    + "Content-Length: " + payload.Length + "\r\n"
                                    + "Connection: close\r\n\r\n";
                                byte[] headBytes = Encoding.ASCII.GetBytes(head);
                                ns.Write(headBytes, 0, headBytes.Length);
                                ns.Write(payload, 0, payload.Length);
                                ns.Flush();
                            }
                        }
                        catch (Exception) { }
                        if (_status == 200) break;   // یک درخواست کافی است
                    }
                }
                catch (Exception) { }
            }

            private static byte[] ReadRequest(NetworkStream ns)
            {
                MemoryStream ms = new MemoryStream();
                byte[] buf = new byte[8192];
                int headerEnd = -1;
                int contentLength = 0;
                while (true)
                {
                    int n = ns.Read(buf, 0, buf.Length);
                    if (n <= 0) break;
                    ms.Write(buf, 0, n);
                    string sofar = Encoding.UTF8.GetString(ms.ToArray());
                    if (headerEnd < 0)
                    {
                        headerEnd = sofar.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                        if (headerEnd >= 0)
                        {
                            int idx = sofar.IndexOf("Content-Length:", StringComparison.OrdinalIgnoreCase);
                            if (idx >= 0)
                            {
                                int end = sofar.IndexOf("\r\n", idx, StringComparison.Ordinal);
                                string v = sofar.Substring(idx + 15, (end < 0 ? sofar.Length : end) - idx - 15).Trim();
                                int.TryParse(v, out contentLength);
                            }
                        }
                    }
                    if (headerEnd >= 0 && ms.Length >= headerEnd + 4 + contentLength) break;
                }
                return ms.ToArray();
            }

            private static string Header(string headers, string name)
            {
                string[] lines = headers.Split(new string[] { "\r\n" }, StringSplitOptions.None);
                for (int i = 0; i < lines.Length; i++)
                {
                    int c = lines[i].IndexOf(':');
                    if (c <= 0) continue;
                    if (string.Equals(lines[i].Substring(0, c).Trim(), name, StringComparison.OrdinalIgnoreCase))
                        return lines[i].Substring(c + 1).Trim();
                }
                return "";
            }

            public void Dispose()
            {
                _stop = true;
                try { _listener.Stop(); } catch (Exception) { }
                try { if (_thread != null && _thread.IsAlive) _thread.Join(1500); } catch (Exception) { }
            }
        }
    }
}
