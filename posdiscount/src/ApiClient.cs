using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace PosDiscount
{
    /// <summary>خطای سرور کمپین با کد وضعیت HTTP (برای پیام قابل فهم و تلاش مجدد).</summary>
    public class SyncApiException : Exception
    {
        public int StatusCode;
        public string ResponseBody = "";

        public SyncApiException(int status, string message) : base(message)
        {
            StatusCode = status;
        }
    }

    public class SyncPushResult
    {
        public int Accepted;
        public int Duplicates;
        public int Updated;
        public int Held;
        public int NeedsReview;
        public int OutOfScope;
        public int Conflicts;
        public int RoundCount;
        public int RoundTarget;
        public int RoundNo;
        public string RoundStatus = "";
        public bool Cached;
        public Dictionary<long, string> Statuses = new Dictionary<long, string>();
    }

    public class RoundSnapshot
    {
        public int RoundNo;
        public string Status = "";
        public int Count;
        public int Target;
        public int Remaining;
        public int Held;
    }

    /// <summary>
    /// کلاینت سرور مرکزی کمپین. امضای HMAC-SHA256 روی
    /// «agentId \n timestamp \n METHOD \n path \n sha256(body)» و ارسال با HTTPS.
    /// نکته: هدف برنامه .NET 4.0 است؛ برای TLS 1.2 مقدار 3072 به‌صورت عددی تنظیم می‌شود
    /// تا روی CLR نصب‌شده (4.8) فعال شود.
    /// </summary>
    public class ApiClient
    {
        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
        private readonly AppSettings _s;

        static ApiClient()
        {
            try { ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072; } catch (Exception) { }
            try { ServicePointManager.Expect100Continue = false; } catch (Exception) { }
        }

        public ApiClient(AppSettings s)
        {
            _s = s;
            Json.MaxJsonLength = 16 * 1024 * 1024;
        }

        public SyncPushResult Push(List<SyncItem> items)
        {
            if (items == null || items.Count == 0) return new SyncPushResult();

            Dictionary<string, object> payload = new Dictionary<string, object>();
            payload["requestId"] = Guid.NewGuid().ToString("N");
            payload["sourceId"] = _s.Database;
            List<object> invoices = new List<object>();
            for (int i = 0; i < items.Count; i++)
            {
                StoredInvoice inv = items[i].Invoice;
                Dictionary<string, object> d = new Dictionary<string, object>();
                d["fCode"] = inv.FCode;
                d["fCodeC"] = inv.FCodeC;
                d["fType"] = 2;   // همیشه فروش؛ ابطال/برگشتی وارد صف نمی‌شود
                d["fDate"] = inv.Date;
                d["jalali"] = inv.Jalali;
                d["fTime"] = inv.Time;
                d["userCode"] = inv.UserCode;
                d["cashier"] = inv.Cashier;
                d["gross"] = inv.Gross;
                d["discount"] = inv.HolooDiscount;
                d["net"] = inv.Net;
                d["customerCode"] = inv.CustomerCode;
                d["mobile"] = inv.Mobile;
                d["status"] = string.Equals(inv.Status, "review", StringComparison.OrdinalIgnoreCase) ? "review" : "valid";
                d["reviewReason"] = inv.ReviewReason;
                d["reviewEvidence"] = inv.ReviewEvidence;
                invoices.Add(d);
            }
            payload["invoices"] = invoices;

            string body = Json.Serialize(payload);
            int status;
            string text = Send("POST", "/api/v1/ingest/invoices", body, out status);
            if (status < 200 || status >= 300)
                throw Error(status, text);

            Dictionary<string, object> root;
            try { root = Json.Deserialize<Dictionary<string, object>>(text); }
            catch (Exception) { throw new SyncApiException(status, "پاسخ سرور قابل خواندن نیست."); }
            if (root == null) throw new SyncApiException(status, "پاسخ خالی سرور.");

            SyncPushResult r = new SyncPushResult();
            r.Accepted = Int(root, "accepted");
            r.Duplicates = Int(root, "duplicates");
            r.Updated = Int(root, "updated");
            r.Held = Int(root, "held");
            r.NeedsReview = Int(root, "needsReview");
            r.OutOfScope = Int(root, "outOfScope");
            r.Conflicts = Int(root, "conflicts");
            r.Cached = Bool(root, "cached");

            object roundObj;
            if (root.TryGetValue("round", out roundObj) && roundObj is Dictionary<string, object>)
            {
                Dictionary<string, object> ro = (Dictionary<string, object>)roundObj;
                r.RoundCount = Int(ro, "count");
                r.RoundTarget = Int(ro, "target");
                r.RoundNo = Int(ro, "roundNo");
                r.RoundStatus = Str(ro, "status");
            }

            object resultsObj;
            if (root.TryGetValue("results", out resultsObj) && resultsObj != null)
            {
                // JavaScriptSerializer آرایه‌ها را ArrayList برمی‌گرداند (نه object[])
                System.Collections.IEnumerable rows = resultsObj as System.Collections.IEnumerable;
                if (rows != null)
                {
                    foreach (object item in rows)
                    {
                        Dictionary<string, object> row = item as Dictionary<string, object>;
                        if (row == null) continue;
                        long f = Long(row, "fCode");
                        string st = Str(row, "status");
                        if (f > 0) r.Statuses[f] = st;
                    }
                }
            }

            return r;
        }

        public RoundSnapshot GetCurrentRound()
        {
            int status;
            string text = Send("GET", "/api/v1/rounds/current", "", out status);
            if (status < 200 || status >= 300) throw Error(status, text);

            Dictionary<string, object> root = Json.Deserialize<Dictionary<string, object>>(text);
            RoundSnapshot r = new RoundSnapshot();
            object roundObj;
            if (root != null && root.TryGetValue("round", out roundObj) && roundObj is Dictionary<string, object>)
            {
                Dictionary<string, object> ro = (Dictionary<string, object>)roundObj;
                r.RoundNo = Int(ro, "roundNo");
                r.Status = Str(ro, "status");
                r.Count = Int(ro, "count");
                r.Target = Int(ro, "target");
                r.Remaining = Int(ro, "remaining");
                r.Held = Int(ro, "held");
            }
            return r;
        }

        // ------------------------------------------------------------------
        private string Send(string method, string path, string body, out int status)
        {
            status = 0;
            string baseUrl = (_s.ApiBaseUrl ?? "").Trim();
            if (baseUrl.Length == 0) throw new SyncApiException(0, "آدرس سرور کمپین تنظیم نشده است.");
            if (baseUrl.EndsWith("/", StringComparison.Ordinal)) baseUrl = baseUrl.Substring(0, baseUrl.Length - 1);
            string url = baseUrl + path;

            byte[] bytes = Encoding.UTF8.GetBytes(body ?? "");
            string bodyHash = Sha256Hex(bytes);
            string ts = ((long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds)
                .ToString(CultureInfo.InvariantCulture);
            string canonical = Canonical(_s.ApiAgentId, ts, method, path, bodyHash);
            string signature = HmacHex(_s.ApiAgentSecret, canonical);

            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = method.ToUpperInvariant();
            req.ContentType = "application/json; charset=utf-8";
            req.Accept = "application/json";
            req.Timeout = Math.Max(5, _s.ApiTimeoutSeconds) * 1000;
            req.ReadWriteTimeout = req.Timeout;
            req.Headers["X-Agent-Id"] = _s.ApiAgentId;
            req.Headers["X-Timestamp"] = ts;
            req.Headers["X-Signature"] = signature;
            req.UserAgent = "PosDiscount-Sync/1.0";
            req.Proxy = null;

            try
            {
                if (method != "GET")
                {
                    req.ContentLength = bytes.Length;
                    using (Stream s = req.GetRequestStream()) s.Write(bytes, 0, bytes.Length);
                }

                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                using (StreamReader sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                {
                    status = (int)resp.StatusCode;
                    return sr.ReadToEnd();
                }
            }
            catch (WebException ex)
            {
                HttpWebResponse resp = ex.Response as HttpWebResponse;
                if (resp != null)
                {
                    status = (int)resp.StatusCode;
                    try
                    {
                        using (StreamReader sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                            return sr.ReadToEnd();
                    }
                    catch (Exception) { }
                    return ex.Message;
                }
                throw new SyncApiException(0, "اتصال به سرور کمپین برقرار نشد: " + ex.Message);
            }
        }

        private static SyncApiException Error(int status, string text)
        {
            string msg = "";
            try
            {
                Dictionary<string, object> d = Json.Deserialize<Dictionary<string, object>>(text);
                if (d != null && d.ContainsKey("message")) msg = Convert.ToString(d["message"]);
            }
            catch (Exception) { }
            if (string.IsNullOrEmpty(msg)) msg = "سرور کمپین خطا برگرداند (HTTP " + status + ").";
            SyncApiException ex = new SyncApiException(status, msg);
            ex.ResponseBody = text == null ? "" : text;
            return ex;
        }

        public static string Canonical(string agentId, string timestamp, string method, string path, string bodyHash)
        {
            return (agentId == null ? "" : agentId) + "\n" + timestamp + "\n"
                + (method == null ? "" : method.ToUpperInvariant()) + "\n" + path + "\n" + bodyHash;
        }

        public static string Sha256Hex(byte[] data)
        {
            using (SHA256 sha = new SHA256Managed())
            {
                byte[] h = sha.ComputeHash(data ?? new byte[0]);
                StringBuilder sb = new StringBuilder(h.Length * 2);
                for (int i = 0; i < h.Length; i++) sb.Append(h[i].ToString("x2", CultureInfo.InvariantCulture));
                return sb.ToString();
            }
        }

        public static string HmacHex(string secret, string canonical)
        {
            using (HMACSHA256 h = new HMACSHA256(Encoding.UTF8.GetBytes(secret == null ? "" : secret)))
            {
                byte[] mac = h.ComputeHash(Encoding.UTF8.GetBytes(canonical));
                StringBuilder sb = new StringBuilder(mac.Length * 2);
                for (int i = 0; i < mac.Length; i++) sb.Append(mac[i].ToString("x2", CultureInfo.InvariantCulture));
                return sb.ToString();
            }
        }

        private static int Int(Dictionary<string, object> d, string key)
        {
            object o;
            if (d == null || !d.TryGetValue(key, out o) || o == null) return 0;
            try { return Convert.ToInt32(o, CultureInfo.InvariantCulture); } catch (Exception) { return 0; }
        }

        private static long Long(Dictionary<string, object> d, string key)
        {
            object o;
            if (d == null || !d.TryGetValue(key, out o) || o == null) return 0;
            try { return Convert.ToInt64(o, CultureInfo.InvariantCulture); } catch (Exception) { return 0; }
        }

        private static bool Bool(Dictionary<string, object> d, string key)
        {
            object o;
            if (d == null || !d.TryGetValue(key, out o) || o == null) return false;
            try { return Convert.ToBoolean(o, CultureInfo.InvariantCulture); } catch (Exception) { return false; }
        }

        private static string Str(Dictionary<string, object> d, string key)
        {
            object o;
            if (d == null || !d.TryGetValue(key, out o) || o == null) return "";
            return Convert.ToString(o, CultureInfo.InvariantCulture);
        }
    }
}
