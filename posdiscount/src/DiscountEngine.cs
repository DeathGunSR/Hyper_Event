using System;
using System.Collections.Generic;
using System.Globalization;

namespace PosDiscount
{
    /// <summary>نتیجه محاسبه تخفیف برای یک فاکتور.</summary>
    public class DiscountResult
    {
        public bool HasDiscount;
        public double Gross;        // جمع کل فاکتور (قبل از تخفیف)
        public double Discount;     // مبلغ تخفیف پیشنهادی
        public double Percent;      // درصد واقعی تخفیف
        public double Net;          // مبلغ قابل پرداخت بعد از تخفیف و گرد کردن
        public string RuleName = ""; // نام قاعده اعمال‌شده
        public string Reason = "";   // توضیح فارسی وضعیت (شرایط برقرار / دلیل عدم تخفیف)
        public string Conditions = ""; // خلاصه شرایط تنظیمات که بررسی شد
    }

    /// <summary>
    /// موتور محاسبه تخفیف: بر اساس تنظیمات، مبلغ فاکتور، جمع تخفیف امروز و مجوز کاربر.
    /// هیچ نوشتنی در دیتابیس انجام نمی‌دهد؛ فقط محاسبه می‌کند.
    /// </summary>
    public static class DiscountEngine
    {
        public static DiscountResult Compute(AppSettings s, double gross, double todayDiscountSum, bool canDiscountPermission)
        {
            DiscountResult r = new DiscountResult();
            r.Gross = gross;
            r.Net = gross;

            string modeName;
            switch (s.DiscountMode)
            {
                case "fixed": modeName = "مبلغ ثابت"; break;
                case "tiered": modeName = "پله‌ای"; break;
                default: modeName = "درصدی"; break;
            }
            r.Conditions = "حالت " + modeName
                + " | حداقل مبلغ فاکتور: " + Num.F(s.MinInvoiceAmount)
                + " | بازه مجاز: " + s.StartTime + " تا " + s.EndTime;

            if (!s.DiscountEnabled)
            {
                r.Reason = "تخفیف در تنظیمات غیرفعال است.";
                return r;
            }

            if (s.RequireDiscountPermission && !canDiscountPermission)
            {
                r.Reason = "این کاربر مجوز تخفیف (ChangeTakhfif) در هولو ندارد.";
                return r;
            }

            TimeSpan from = TimeSpan.Zero;
            TimeSpan to = TimeSpan.Zero;
            TimeSpan now = DateTime.Now.TimeOfDay;
            bool okFrom = Num.TryParseHHmm(s.StartTime, out from);
            bool okTo = Num.TryParseHHmm(s.EndTime, out to);
            if (okTo) to = to.Add(new TimeSpan(0, 0, 59));   // کل دقیقه پایانی هم مجاز باشد
            bool hasWindow = okFrom && okTo;
            if (hasWindow)
            {
                bool inside = from <= to ? (now >= from && now <= to) : (now >= from || now <= to);
                if (!inside)
                {
                    r.Reason = "خارج از بازه مجاز تخفیف (" + s.StartTime + " تا " + s.EndTime + ").";
                    return r;
                }
            }

            if (gross <= 0)
            {
                r.Reason = "مبلغ فاکتور صفر است.";
                return r;
            }

            if (gross < s.MinInvoiceAmount)
            {
                r.Reason = "مبلغ فاکتور (" + Num.F(gross) + ") کمتر از حد تعیین‌شده (" + Num.F(s.MinInvoiceAmount) + ") است.";
                return r;
            }

            // --- تخفیف پایه بر اساس حالت انتخابی ---
            double d = 0;
            string rule = "";
            if (s.DiscountMode == "fixed")
            {
                d = s.FixedAmount;
                rule = "مبلغ ثابت " + Num.F(s.FixedAmount) + " ریال";
            }
            else if (s.DiscountMode == "tiered")
            {
                DiscountTier best = null;
                for (int i = 0; i < s.Tiers.Count; i++)
                {
                    DiscountTier t = s.Tiers[i];
                    if (gross >= t.MinAmount && (best == null || t.MinAmount > best.MinAmount)) best = t;
                }
                if (best == null)
                {
                    r.Reason = "هیچ پله‌ای برای مبلغ " + Num.F(gross) + " تعریف نشده است.";
                    return r;
                }
                if (best.Amount > 0) { d = best.Amount; rule = "پله " + Num.F(best.MinAmount) + " ریال → مبلغ ثابت " + Num.F(best.Amount); }
                else { d = gross * best.Percent / 100.0; rule = "پله " + Num.F(best.MinAmount) + " ریال → " + Num.P(best.Percent) + "٪"; }
            }
            else
            {
                d = gross * s.Percent / 100.0;
                rule = Num.P(s.Percent) + "٪ از مبلغ فاکتور";
            }

            // --- سقف‌ها ---
            if (s.MaxPercent > 0)
            {
                double cap = gross * s.MaxPercent / 100.0;
                if (d > cap) { d = cap; rule += " (محدود به سقف " + Num.P(s.MaxPercent) + "٪)"; }
            }
            if (s.MaxAmount > 0 && d > s.MaxAmount)
            {
                d = s.MaxAmount;
                rule += " (محدود به سقف " + Num.F(s.MaxAmount) + " ریال)";
            }

            // --- سقف تخفیف روزانه کاربر ---
            if (s.MaxDiscountPerDay > 0)
            {
                double left = s.MaxDiscountPerDay - todayDiscountSum;
                if (left <= 0)
                {
                    r.Reason = "سقف تخفیف امروز این کاربر (" + Num.F(s.MaxDiscountPerDay) + ") تکمیل شده است.";
                    return r;
                }
                if (d > left) { d = left; rule += " (باقی‌مانده سقف روزانه)"; }
            }

            if (d <= 0)
            {
                r.Reason = "مبلغ تخفیف محاسبه‌شده صفر است.";
                return r;
            }

            // --- گرد کردن مبلغ قابل پرداخت ---
            double net = gross - d;
            if (s.RoundTo > 0)
            {
                double rounded = s.RoundInFavorOfCustomer
                    ? Math.Floor(net / s.RoundTo) * s.RoundTo
                    : Math.Ceiling(net / s.RoundTo) * s.RoundTo;
                if (rounded < 0) rounded = 0;
                if (Math.Abs(rounded - net) > 0.001)
                    rule += " + گرد کردن به " + Num.F(s.RoundTo) + " ریال";
                net = rounded;
            }

            d = gross - net;
            if (d <= 0)
            {
                r.Reason = "پس از گرد کردن، تخفیفی باقی نمی‌ماند.";
                return r;
            }

            r.HasDiscount = true;
            r.Discount = d;
            r.Net = net;
            r.Percent = gross > 0 ? (d * 100.0 / gross) : 0;
            r.RuleName = rule;
            r.Reason = "شرایط تخفیف برقرار است.";
            return r;
        }

        /// <summary>محاسبه بدون فاکتور (برای دکمه «آزمایش پاپ‌آپ»).</summary>
        public static DiscountResult Sample(AppSettings s, double gross)
        {
            return Compute(s, gross, 0, true);
        }

        public static string ModePersian(string mode)
        {
            switch (mode)
            {
                case "fixed": return "مبلغ ثابت";
                case "tiered": return "پله‌ای";
                default: return "درصدی";
            }
        }

        public static List<string> ModeKeys()
        {
            List<string> l = new List<string>();
            l.Add("percent");
            l.Add("fixed");
            l.Add("tiered");
            return l;
        }
    }
}
