using System;
using System.Globalization;
using System.Text;

namespace PosDiscount
{
    /// <summary>
    /// ابزار شماره موبایل: تبدیل ارقام فارسی/عربی، حذف فاصله و خط تیره و یکسان‌سازی
    /// همه قالب‌ها (09123456789 / 9123456789 / +989123456789 / 00989123456789) به 09xxxxxxxxx.
    /// </summary>
    public static class PhoneUtil
    {
        /// <summary>فقط ارقام لاتین را برمی‌گرداند (ارقام فارسی/عربی و فاصله‌ها حذف می‌شوند).</summary>
        public static string Digits(string raw)
        {
            if (raw == null) return "";
            string n = Num.NormalizeDigits(raw);
            StringBuilder sb = new StringBuilder(n.Length);
            for (int i = 0; i < n.Length; i++)
            {
                char c = n[i];
                if (c >= '0' && c <= '9') sb.Append(c);
            }
            return sb.ToString();
        }

        /// <summary>قالب یکسان موبایل ایران: 09xxxxxxxxx (اگر شماره قابل تشخیص نباشد، فقط ارقام برگردانده می‌شود).</summary>
        public static string Normalize(string raw)
        {
            string n = Digits(raw);
            if (n.Length == 0) return "";
            if (n.StartsWith("0098")) n = n.Substring(4);
            else if (n.StartsWith("98") && n.Length == 12) n = n.Substring(2);
            if (n.Length == 10 && n[0] == '9') n = "0" + n;
            return n;
        }

        /// <summary>آیا شماره موبایل ایران است؟ (11 رقم و با 09 شروع شود)</summary>
        public static bool IsMobile(string normalized)
        {
            if (normalized == null) return false;
            if (normalized.Length != 11) return false;
            if (!normalized.StartsWith("09")) return false;
            for (int i = 0; i < normalized.Length; i++)
                if (normalized[i] < '0' || normalized[i] > '9') return false;
            return true;
        }

        /// <summary>نمایش خوانا: 0912 345 6789</summary>
        public static string Pretty(string normalized)
        {
            if (string.IsNullOrEmpty(normalized)) return "";
            if (normalized.Length != 11) return normalized;
            return normalized.Substring(0, 4) + " " + normalized.Substring(4, 3) + " " + normalized.Substring(7);
        }
    }

    /// <summary>
    /// محاسبه امتیاز وفاداری: برای هر «مبلغ مشخص تومانی» که در یک فاکتور کامل شود، یک امتیاز.
    /// قاعده دقیقاً همان است که کارفرما گفته:
    ///   • دو فاکتور ۲۵۰ هزار تومانی ⇒ هیچ امتیازی (امتیاز روی هر فاکتور جدا حساب می‌شود)
    ///   • ۵۰۰ هزار تومان ⇒ ۱ امتیاز | ۷۵۰ هزار تومان ⇒ ۱ امتیاز | ۱ میلیون تومان ⇒ ۲ امتیاز
    /// برنامه مبالغ را ریالی نگه می‌دارد (۱۰ ریال = ۱ تومان)، پس آستانه تومانی در ۱۰ ضرب می‌شود.
    /// </summary>
    public static class PointsEngine
    {
        public const double RialPerToman = 10.0;

        /// <summary>آستانه امتیاز به ریال (مثلاً ۵۰۰٬۰۰۰ تومان ⇒ ۵٬۰۰۰٬۰۰۰ ریال).</summary>
        public static double ThresholdRial(double perAmountToman)
        {
            if (perAmountToman <= 0) return 0;
            return perAmountToman * RialPerToman;
        }

        /// <summary>تعداد امتیاز یک فاکتور: تقسیم صحیح مبلغ بر آستانه (بدون جمع زدن فاکتورها).</summary>
        public static int ForAmount(double amountRial, double perAmountToman)
        {
            double t = ThresholdRial(perAmountToman);
            if (t <= 0 || amountRial <= 0) return 0;
            double n = Math.Floor(amountRial / t + 1e-9);
            if (n < 0) n = 0;
            if (n > int.MaxValue) n = int.MaxValue;
            return (int)n;
        }

        /// <summary>مبنای امتیاز: جمع کل فاکتور یا مبلغ قابل پرداخت (پس از تخفیف ثبت‌شده در هولو).</summary>
        public static double BaseAmount(AppSettings s, double gross, double holooDiscount)
        {
            if (s != null && s.PointsBase == "net")
            {
                double net = gross - holooDiscount;
                return net > 0 ? net : 0;
            }
            return gross;
        }

        public static string BaseLabel(string baseKind)
        {
            return baseKind == "net" ? "مبلغ قابل پرداخت (پس از تخفیف)" : "جمع کل فاکتور";
        }

        /// <summary>توضیح فارسی برای نمایش در فرم‌ها.</summary>
        public static string Explain(double amountRial, double perAmountToman)
        {
            int p = ForAmount(amountRial, perAmountToman);
            return Num.F(amountRial) + " ریال (" + Num.F(amountRial / RialPerToman) + " تومان) ⇒ "
                + p + " امتیاز";
        }

        /// <summary>توضیح قاعده (برای راهنما).</summary>
        public static string RuleText(double perAmountToman)
        {
            return "هر " + Num.F(perAmountToman) + " تومان خرید در یک فاکتور = ۱ امتیاز (جداگانه برای هر فاکتور)";
        }
    }
}
