using System;
using System.Globalization;
using System.Text;

namespace PosDiscount
{
    /// <summary>کمکی‌های عددی/زمانی مشترک برنامه.</summary>
    public static class Num
    {
        /// <summary>تبدیل ارقام فارسی/عربی به لاتین و حذف جداکننده‌ها.</summary>
        public static string NormalizeDigits(string s)
        {
            if (s == null) return "";
            StringBuilder sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c >= '\u06F0' && c <= '\u06F9') sb.Append((char)('0' + (c - '\u06F0')));      // ۰-۹
                else if (c >= '\u0660' && c <= '\u0669') sb.Append((char)('0' + (c - '\u0660'))); // ٠-٩
                else if (c == ',' || c == '\u066C' || c == ' ' || c == '\u00A0' || c == '\u200F' || c == '\u200E') continue;
                else sb.Append(c);
            }
            return sb.ToString();
        }

        public static double ParseD(string s, double def)
        {
            string t = NormalizeDigits(s).Replace('\u066B', '.').Trim();
            double v;
            if (double.TryParse(t, NumberStyles.Any, CultureInfo.InvariantCulture, out v)) return v;
            return def;
        }

        public static int ParseI(string s, int def)
        {
            double v = ParseD(s, def);
            return (int)Math.Round(v);
        }

        public static bool ParseB(string s, bool def)
        {
            if (s == null) return def;
            string t = s.Trim().ToLowerInvariant();
            if (t == "true" || t == "1" || t == "yes" || t == "بله") return true;
            if (t == "false" || t == "0" || t == "no" || t == "خیر") return false;
            return def;
        }

        /// <summary>عدد با جداکننده هزارگان و ارقام لاتین: 1,234,500</summary>
        public static string F(double v)
        {
            return Math.Round(v).ToString("N0", CultureInfo.InvariantCulture);
        }

        public static string P(double v)
        {
            return (Math.Round(v * 100.0) / 100.0).ToString("0.##", CultureInfo.InvariantCulture);
        }

        public static string T(DateTime d)
        {
            return d.ToString("HH:mm:ss");
        }

        public static bool TryParseHHmm(string s, out TimeSpan result)
        {
            result = TimeSpan.Zero;
            if (string.IsNullOrEmpty(s)) return false;
            string t = NormalizeDigits(s).Trim().Replace('.', ':');
            string[] parts = t.Split(':');
            if (parts.Length < 1 || parts.Length > 3) return false;
            int h, m = 0, sec = 0;
            if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out h)) return false;
            if (parts.Length > 1 && !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out m)) return false;
            if (parts.Length > 2 && !int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out sec)) return false;
            if (h < 0 || h > 23 || m < 0 || m > 59 || sec < 0 || sec > 59) return false;
            result = new TimeSpan(h, m, sec);
            return true;
        }
    }

    public static class FmtDate
    {
        public static string G(DateTime d) { return d.ToString("yyyy/MM/dd", CultureInfo.InvariantCulture); }
        public static string G2(DateTime d) { return d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture); }
    }
}
