using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.IO;
using System.Text;
using Microsoft.Win32;

namespace PosDiscount
{
    /// <summary>
    /// تشخیص و اصلاح خودکار «نام سرور» و یافتن اینستنس‌های واقعی SQL Server روی همین رایانه.
    ///
    /// چرا این کلاس لازم شد (باگ واقعی): کلید رجیستری
    ///   HKLM\SOFTWARE\Microsoft\Microsoft SQL Server\Instance Names\SQL
    /// «نام اینستنس» را به‌عنوان *نام مقدار* و «شناسه اینستنس» را به‌عنوان *مقدار* نگه می‌دارد:
    ///   SQLEXPRESS  =  MSSQL17.SQLEXPRESS
    /// اگر اشتباهاً شناسه (MSSQL17.SQLEXPRESS) به‌جای نام (SQLEXPRESS) در settings.xml ذخیره شود،
    /// SQL Server خطای «Error Locating Server/Instance Specified (26)» می‌دهد؛ یعنی سرور «پیدا می‌شود»
    /// ولی اتصال هرگز برقرار نمی‌شود و برنامه از کار می‌افتد. این کلاس هم آن مقدار را می‌شناسد و
    /// اصلاح می‌کند و هم اگر سرور محلی جواب ندهد، اینستنس در دسترس را خودش پیدا می‌کند.
    /// </summary>
    public static class ConnectionFixer
    {
        public const string InstanceKey = @"SOFTWARE\Microsoft\Microsoft SQL Server\Instance Names\SQL";
        public const string InstanceKeyWow = @"SOFTWARE\WOW6432Node\Microsoft\Microsoft SQL Server\Instance Names\SQL";

        // ==================================================================
        //  رجیستری
        // ==================================================================
        /// <summary>نام اینستنس‌های محلی (SQLEXPRESS، MSSQLSERVER، …) — نه شناسه آن‌ها (MSSQL17.SQLEXPRESS).</summary>
        public static List<string> RegistryInstanceNames()
        {
            List<string> names = new List<string>();
            string[] keys = new string[] { InstanceKey, InstanceKeyWow };
            for (int i = 0; i < keys.Length; i++)
            {
                try
                {
                    using (RegistryKey k = Registry.LocalMachine.OpenSubKey(keys[i]))
                    {
                        if (k == null) continue;
                        string[] valueNames = k.GetValueNames();
                        for (int j = 0; j < valueNames.Length; j++)
                        {
                            string name = (valueNames[j] ?? "").Trim();
                            if (name.Length == 0) continue;
                            if (!Contains(names, name)) names.Add(name);
                        }
                    }
                }
                catch (Exception) { }
            }
            return names;
        }

        /// <summary>نقشه «شناسه اینستنس → نام اینستنس» (MSSQL17.SQLEXPRESS → SQLEXPRESS).</summary>
        public static Dictionary<string, string> InstanceIdToName()
        {
            Dictionary<string, string> map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string[] keys = new string[] { InstanceKey, InstanceKeyWow };
            for (int i = 0; i < keys.Length; i++)
            {
                try
                {
                    using (RegistryKey k = Registry.LocalMachine.OpenSubKey(keys[i]))
                    {
                        if (k == null) continue;
                        string[] valueNames = k.GetValueNames();
                        for (int j = 0; j < valueNames.Length; j++)
                        {
                            string name = (valueNames[j] ?? "").Trim();
                            string id = Convert.ToString(k.GetValue(valueNames[j]));
                            if (id == null) id = "";
                            id = id.Trim();
                            if (name.Length > 0 && id.Length > 0 && !map.ContainsKey(id)) map[id] = name;
                        }
                    }
                }
                catch (Exception) { }
            }
            return map;
        }

        // ==================================================================
        //  تشخیص/پاک‌سازی (توابع خالص، بدون اتصال — قابل تست)
        // ==================================================================
        /// <summary>آیا این رشته «شناسه اینستنس» است؟ (مثل MSSQL17.SQLEXPRESS یا MSSQL16.MSSQLSERVER)</summary>
        public static bool LooksLikeInstanceId(string value)
        {
            if (string.IsNullOrEmpty(value)) return false;
            string v = value.Trim().ToUpperInvariant();
            if (v.Length < 8 || !v.StartsWith("MSSQL")) return false;
            int i = 5;
            int digits = 0;
            while (i < v.Length && v[i] >= '0' && v[i] <= '9') { i++; digits++; }
            if (digits == 0) return false;
            if (i >= v.Length || v[i] != '.') return false;
            return i + 1 < v.Length;
        }

        /// <summary>
        /// اصلاح رشته «نام سرور» بدون نیاز به اتصال:
        ///   MSSQL17.SQLEXPRESS      → .\SQLEXPRESS
        ///   .\MSSQL17.SQLEXPRESS    → .\SQLEXPRESS
        ///   SERVER-PC\MSSQL17.SQLEXPRESS → SERVER-PC\SQLEXPRESS
        ///   MSSQL16.MSSQLSERVER     → .            (اینستنس پیش‌فرض)
        /// مقادیر درست (.\SQLEXPRESS ، 192.168.1.10\HOLOO ، .) دست‌نخورده می‌مانند.
        /// </summary>
        public static string Sanitize(string server, Dictionary<string, string> idsToNames)
        {
            if (server == null) return "";
            string v = server.Trim();
            if (v.Length == 0) return v;

            int slash = v.IndexOf('\\');
            if (slash < 0)
            {
                // نام سرور بدون اینستنس — ولی ممکن است خودش یک «شناسه اینستنس» باشد
                string mapped;
                if (idsToNames != null && idsToNames.TryGetValue(v, out mapped) && !string.IsNullOrEmpty(mapped))
                    return LocalServerOf(mapped);
                if (LooksLikeInstanceId(v))
                    return LocalServerOf(InstancePartOf(v));
                return v;
            }

            string host = v.Substring(0, slash).Trim();
            string inst = v.Substring(slash + 1).Trim();
            if (host.Length == 0) host = ".";
            if (inst.Length == 0) return host;

            string real;
            if (idsToNames != null && idsToNames.TryGetValue(inst, out real) && !string.IsNullOrEmpty(real))
                inst = real;
            else if (LooksLikeInstanceId(inst))
                inst = InstancePartOf(inst);

            if (inst.ToUpperInvariant() == "MSSQLSERVER")
                return host.Equals(".") ? "." : host;
            return host + "\\" + inst;
        }

        private static string InstancePartOf(string instanceId)
        {
            int dot = instanceId.IndexOf('.');
            return dot >= 0 && dot + 1 < instanceId.Length ? instanceId.Substring(dot + 1).Trim() : instanceId;
        }

        /// <summary>نام اینستنس → رشته سرور محلی؛ «MSSQLSERVER» یعنی اینستنس پیش‌فرض (.)</summary>
        public static string LocalServerOf(string instanceName)
        {
            if (string.IsNullOrEmpty(instanceName)) return ".";
            return instanceName.Trim().ToUpperInvariant() == "MSSQLSERVER" ? "." : ".\\" + instanceName.Trim();
        }

        /// <summary>
        /// آیا در این رشته سرور از «شناسه اینستنس» استفاده شده؟ کل رشته چک می‌شود و اگر فرم
        /// «میزبان\\نام» باشد، بخش نام هم. مثال: .\MSSQL17.SQLEXPRESS → بله
        /// </summary>
        public static bool HasInstanceIdValue(string server)
        {
            if (string.IsNullOrEmpty(server)) return false;
            string v = server.Trim();
            if (LooksLikeInstanceId(v)) return true;
            int slash = v.IndexOf('\\');
            if (slash < 0) return false;
            return LooksLikeInstanceId(v.Substring(slash + 1).Trim());
        }

        /// <summary>آیا سرور ظاهراً روی همین رایانه است؟ (جابه‌جایی خودکار فقط در این حالت امن است)</summary>
        public static bool LooksLocal(string server)
        {
            if (string.IsNullOrEmpty(server)) return true;
            string v = server.Trim();
            string host = v;
            int slash = v.IndexOf('\\');
            if (slash >= 0) host = v.Substring(0, slash).Trim();
            if (host.ToLowerInvariant().StartsWith("tcp:")) host = host.Substring(4).Trim();

            if (host.Length == 0 || host == ".") return true;
            string h = host.ToLowerInvariant();
            if (h == "(local)" || h == "localhost" || h == "127.0.0.1" || h == "::1" || h == "(localdb)") return true;
            try { if (h == Environment.MachineName.ToLowerInvariant()) return true; } catch (Exception) { }
            // سرور بدون نام میزبان ولی «شناسه اینستنس» → یعنی روی همین رایانه بوده و خراب شده
            if (slash < 0 && LooksLikeInstanceId(v)) return true;
            return false;
        }

        /// <summary>فهرست گزینه‌های معتبر برای امتحان کردن، به ترتیب اولویت (بدون تکرار).</summary>
        public static List<string> CandidateServers(string current)
        {
            List<string> list = new List<string>();
            if (!string.IsNullOrEmpty(current) && current.Trim().Length > 0)
            {
                // مقدار فعلی هم پاک‌سازی می‌شود تا فهرست هیچ‌وقت «شناسه اینستنس» پیشنهاد نکند
                string cur = Sanitize(current, InstanceIdToName());
                if (cur.Length > 0) list.Add(cur);
            }

            List<string> instances = RegistryInstanceNames();
            for (int i = 0; i < instances.Count; i++)
            {
                string srv = LocalServerOf(instances[i]);
                if (!Contains(list, srv)) list.Add(srv);
            }

            string[] tail = new string[] { ".\\SQLEXPRESS", ".", "(local)", "localhost" };
            for (int i = 0; i < tail.Length; i++)
                if (!Contains(list, tail[i])) list.Add(tail[i]);
            return list;
        }

        private static bool Contains(List<string> list, string item)
        {
            for (int i = 0; i < list.Count; i++)
                if (string.Equals(list[i], item, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        // ==================================================================
        //  خطاها
        // ==================================================================
        /// <summary>آیا خطا از نوع «سرور/اینستنس پیدا نشد» است؟ (خطای 26/40 — نه نام کاربری یا نام دیتابیس)</summary>
        public static bool LooksLikeServerNotFound(Exception ex)
        {
            if (ex == null) return false;
            SqlException se = ex as SqlException;
            if (se != null && se.Number == -1) return true;
            string m = ex.Message == null ? "" : ex.Message;
            return IndexOf(m, "network-related") >= 0 || IndexOf(m, "server was not found") >= 0
                || IndexOf(m, "error: 26") >= 0 || IndexOf(m, "error: 40") >= 0
                || IndexOf(m, "error locating server") >= 0;
        }

        private static int IndexOf(string text, string part)
        {
            return text.IndexOf(part, StringComparison.OrdinalIgnoreCase);
        }

        // ==================================================================
        //  آزمایش و اصلاح خودکار
        // ==================================================================
        /// <summary>آزمایش یک نام سرور با همان دیتابیس/احراز هویت تنظیمات (بدون تغییر تنظیمات).</summary>
        public static bool Probe(AppSettings s, string server, out string message)
        {
            message = "";
            if (s == null || string.IsNullOrEmpty(server)) return false;
            try
            {
                AppSettings tmp = s.Clone();
                tmp.Server = server.Trim();
                if (tmp.ConnectTimeoutSeconds > 3) tmp.ConnectTimeoutSeconds = 3;   // آزمایش باید سریع باشد
                HolooDb db = new HolooDb(tmp.BuildConnectionString());
                DbStatus st = db.Test();
                message = st.Message;
                return st.Ok;
            }
            catch (Exception ex)
            {
                message = HolooDb.DescribeConnectionError(ex);
                return false;
            }
        }

        public static string Heal(AppSettings s)
        {
            bool changed;
            return Heal(s, out changed);
        }

        /// <summary>
        /// اصلاح خودکار اتصال:
        ///   ۱) مقدارهای ساختاری خراب (شناسه اینستنس) به نام درست تبدیل می‌شوند.
        ///   ۲) اگر سرور روی همین رایانه است و جواب نمی‌دهد، اینستنس‌های واقعی امتحان می‌شوند
        ///      (فقط با همان نام دیتابیس؛ پس هر اینستنسی که به دیتابیس هولو دسترسی داشته باشد می‌پذیرد).
        /// در صورت تغییر، نسخه قبلی به settings.xml.bak بکاپ گرفته و فایل تنظیمات ذخیره می‌شود.
        /// خروجی: توضیح فارسی تغییرات (خالی = چیزی عوض نشد).
        /// </summary>
        public static string Heal(AppSettings s, out bool changed)
        {
            changed = false;
            if (s == null) return "";
            StringBuilder msg = new StringBuilder();

            // ۱) اصلاح ساختاری (بدون اتصال)
            string before = s.Server == null ? "" : s.Server.Trim();
            string after = Sanitize(before, InstanceIdToName());
            if (!string.Equals(after, before, StringComparison.Ordinal))
            {
                s.Server = after;
                changed = true;
                msg.Append("نام سرور «" + before + "» یک «شناسه اینستنس» بود (نه نام اینستنس) و به «"
                    + after + "» اصلاح شد.");
            }

            // ۲) فقط اگر سرور محلی است و جواب نمی‌دهد، اینستنس در دسترس را پیدا کن
            if (!LooksLocal(s.Server)) { if (changed) SaveWithBackup(s); return msg.ToString(); }

            string probeMsg;
            if (Probe(s, s.Server, out probeMsg))
            {
                if (changed) SaveWithBackup(s);
                return msg.ToString();
            }

            List<string> candidates = CandidateServers(s.Server);
            for (int i = 0; i < candidates.Count; i++)
            {
                string cand = candidates[i];
                if (string.Equals(cand, s.Server, StringComparison.OrdinalIgnoreCase)) continue;
                string m;
                if (Probe(s, cand, out m))
                {
                    if (msg.Length > 0) msg.Append(" ");
                    msg.Append("سرور «" + s.Server + "» در دسترس نبود؛ به‌صورت خودکار به «" + cand
                        + "» تغییر یافت (همان دیتابیس با همان نام).");
                    s.Server = cand;
                    changed = true;
                    break;
                }
            }

            if (changed) SaveWithBackup(s);
            return msg.ToString();
        }

        /// <summary>ذخیره تنظیمات با نگه‌داشتن نسخه قبلی در settings.xml.bak</summary>
        public static void SaveWithBackup(AppSettings s)
        {
            try
            {
                if (s != null && !string.IsNullOrEmpty(s.FilePath) && File.Exists(s.FilePath))
                {
                    try { File.Copy(s.FilePath, s.FilePath + ".bak", true); } catch (Exception) { }
                }
                if (s != null) s.Save();
            }
            catch (Exception) { }
        }
    }
}
