using System;
using System.Collections.Generic;
using System.Text;

namespace PosDiscount
{
/// <summary>یک جایزه در قرعه‌کشی پله‌ای (مثلاً «جایزه اول» با ۱ برنده).</summary>
public class Prize
{
    public string Title = "";
    public int Winners = 1;

    public Prize() { }

    public Prize(string title, int winners)
    {
        Title = title;
        Winners = winners;
    }
}

/// <summary>یک برنده با ترتیب انتخاب و شانس‌های محاسبه‌شده (برای مستندسازی).</summary>
public class Winner
{
    public int Rank;                // ۱ = برنده اول (ترتیب کل قرعه‌کشی)
    public string Prize = "";        // عنوان جایزه (خالی = حالت ساده)
    public string Mobile = "";
        public string Name = "";
        public int Points;              // امتیاز = تعداد شانس (بلیت)
        public int Invoices;
        public double Amount;
        public double ChanceInitial;    // شانس در ابتدای قرعه‌کشی (۰..۱)
        public double ChanceAtPick;     // شانس در لحظه انتخاب (پس از حذف برندگان قبلی)
        public int RemainingTickets;    // مجموع شانس‌های باقی‌مانده در لحظه انتخاب
    }

    /// <summary>شرکت‌کننده با تعداد شانسش (۱ امتیاز = ۱ شانس).</summary>
    public class Ticket
    {
        public string Mobile = "";
        public string Name = "";
        public int Points;              // = تعداد شانس
        public int Invoices;
        public double Amount;
        public double ChanceInitial;    // Points / TotalTickets
    }

    /// <summary>نتیجه کامل یک قرعه‌کشی (قابل ذخیره، بازتولید و گزارش).</summary>
    public class LotteryResult
    {
        public DateTime At = DateTime.Now;
        public int Seed;
        public int RequestedWinners;
        public int MinPoints = 1;
        public bool PointsUsed = true;
        public string TypeName = "ساده";
        public string FromYmd = "";
        public string ToYmd = "";
        public string Scope = "";        // شرح دسته/دامنه شرکت‌کنندگان (صندوق‌دار، بازه تاریخ، حداقل فاکتور)
        public int Participants;        // تعداد شرکت‌کنندگان واجد شرط
        public int Excluded;            // تعداد حذف‌شده (امتیاز کمتر از حد یا بدون شماره)
        public long TotalTickets;       // مجموع امتیازها = مجموع شانس‌ها
        public List<Prize> Prizes = new List<Prize>();
        public List<Ticket> Tickets = new List<Ticket>();
        public List<Winner> Winners = new List<Winner>();
        public List<string> Notes = new List<string>();

        public string Summary()
        {
            return "قرعه‌کشی " + TypeName + " | " + Participants + " شرکت‌کننده | "
                + TotalTickets + " شانس (هر ۱ امتیاز = ۱ شانس) | " + Winners.Count + " برنده | seed: " + Seed;
        }

        /// <summary>متن جایزه‌ها برای نمایش/لاگ (مثلاً «جایزه اول: ۱ / جایزه دوم: ۳»).</summary>
        public string PrizesText()
        {
            // حالت ساده (یک جایزه بی‌عنوان) → متنی لازم نیست
            if (Prizes.Count == 1 && (Prizes[0].Title == null || Prizes[0].Title.Length == 0)) return "";

            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < Prizes.Count; i++)
            {
                if (i > 0) sb.Append(" | ");
                string t = Prizes[i].Title.Length > 0 ? Prizes[i].Title : ("جایزه " + (i + 1));
                sb.Append(t + ": " + Prizes[i].Winners);
            }
            return sb.ToString();
        }

        /// <summary>تعداد کل برندگان درخواستی در همه جایزه‌ها.</summary>
        public int RequestedTotal()
        {
            int sum = 0;
            for (int i = 0; i < Prizes.Count; i++) sum += Prizes[i].Winners;
            return sum;
        }
    }

    /// <summary>
    /// موتور قرعه‌کشی وزنی.
    /// قاعده: هر ۱ امتیاز = ۱ شانس. انتخاب «بدون جایگذاری» است (یک نفر دو بار برنده نمی‌شود)
    /// و با seed مشخص کاملاً قابل بازتولید است: همان فهرست شرکت‌کنندگان + همان seed = همان نتیجه.
    /// </summary>
    public static class LotteryEngine
    {
        /// <summary>شرط ورود به قرعه‌کشی: شماره موبایل داشته باشد و امتیازش از حداقل کمتر نباشد.</summary>
        public static List<PhoneProfile> Eligible(List<PhoneProfile> all, int minPoints)
        {
            List<PhoneProfile> list = new List<PhoneProfile>();
            if (all == null) return list;
            if (minPoints < 1) minPoints = 1;
            for (int i = 0; i < all.Count; i++)
            {
                PhoneProfile p = all[i];
                if (p == null) continue;
                string m = PhoneUtil.Normalize(p.Mobile);
                if (m.Length == 0) continue;
                if (p.Points < minPoints) continue;
                p.Mobile = m;
                list.Add(p);
            }

            // ترتیب قطعی (امتیاز نزولی، سپس شماره) تا نتیجه با seed یکسان همیشه یکسان بماند
            list.Sort(new Comparison<PhoneProfile>(delegate (PhoneProfile a, PhoneProfile b)
            {
                if (a.Points != b.Points) return b.Points - a.Points;
                return string.CompareOrdinal(a.Mobile, b.Mobile);
            }));
            return list;
        }

        public static int SuggestedSeed()
        {
            return unchecked((int)(DateTime.Now.Ticks & 0x7FFFFFFF));
        }

        /// <summary>تعداد شانس (بلیت) هر شرکت‌کننده: هر ۱ امتیاز = ۱ شانس.</summary>
        public static int TicketsOf(PhoneProfile p)
        {
            if (p == null) return 0;
            return p.Points < 0 ? 0 : p.Points;
        }

        /// <summary>قرعه‌کشی ساده: تعدادی برنده به ترتیب انتخاب، از یک سبد مشترک و بدون تکرار.</summary>
        public static LotteryResult Draw(List<PhoneProfile> all, int winners, int seed, int minPoints)
        {
            List<Prize> prizes = new List<Prize>();
            prizes.Add(new Prize("", winners));
            return DrawPrizes(all, prizes, seed, minPoints, "ساده");
        }

        /// <summary>
        /// قرعه‌کشی پله‌ای/چند جایزه: جایزه‌ها به ترتیب بررسی می‌شوند و برندگان هر جایزه
        /// از همان سبد مشترک و **بدون تکرار** انتخاب می‌شوند (یک نفر فقط یک بار برنده می‌شود).
        /// نتیجه با همان seed عیناً قابل بازتولید است.
        /// </summary>
        public static LotteryResult DrawPrizes(List<PhoneProfile> all, List<Prize> prizes, int seed, int minPoints, string typeName)
        {
            LotteryResult res = new LotteryResult();
            res.Seed = seed;
            res.MinPoints = minPoints < 1 ? 1 : minPoints;
            res.TypeName = typeName == null || typeName.Length == 0 ? "ساده" : typeName;
            if (prizes == null) prizes = new List<Prize>();
            if (prizes.Count == 0) prizes.Add(new Prize("", 1));

            int winners = 0;
            for (int i = 0; i < prizes.Count; i++)
            {
                Prize pin = prizes[i];
                int cnt = pin.Winners < 1 ? 1 : pin.Winners;
                Prize p = new Prize(pin.Title == null ? "" : pin.Title.Trim(), cnt);
                res.Prizes.Add(p);
                winners += cnt;
            }
            res.RequestedWinners = winners;
            if (winners < 1) winners = 1;

            List<PhoneProfile> pool = Eligible(all, res.MinPoints);
            res.Participants = pool.Count;
            res.Excluded = (all == null ? 0 : all.Count) - pool.Count;

            long total = 0;
            for (int i = 0; i < pool.Count; i++)
            {
                Ticket t = new Ticket();
                t.Mobile = pool[i].Mobile;
                t.Name = pool[i].Name;
                t.Points = TicketsOf(pool[i]);
                t.Invoices = pool[i].Count;
                t.Amount = pool[i].Amount;
                res.Tickets.Add(t);
                total += t.Points;
            }
            res.TotalTickets = total;

            for (int i = 0; i < res.Tickets.Count; i++)
                res.Tickets[i].ChanceInitial = total > 0 ? (double)res.Tickets[i].Points / (double)total : 0;

            if (pool.Count == 0)
            {
                res.Notes.Add("هیچ شرکت‌کننده‌ای با شرط انتخاب‌شده پیدا نشد (حداقل امتیاز " + res.MinPoints + ").");
                return res;
            }
            if (res.Prizes.Count > 1) res.Notes.Add("جایزه‌ها: " + res.PrizesText());
            if (winners > pool.Count)
            {
                res.Notes.Add("تعداد برندگان درخواستی (" + winners + ") بیشتر از شرکت‌کنندگان واجد شرط ("
                    + pool.Count + ") بود؛ به تعداد شرکت‌کنندگان محدود شد.");
                winners = pool.Count;
            }

            bool[] used = new bool[pool.Count];
            Random rng = new Random(seed);
            long remaining = total;

            for (int k = 0; k < winners; k++)
            {
                int idx = -1;
                int lastEligible = -1;
                if (remaining > 0)
                {
                    double r = rng.NextDouble() * (double)remaining;
                    double cum = 0;
                    for (int i = 0; i < pool.Count; i++)
                    {
                        if (used[i]) continue;
                        lastEligible = i;
                        cum += TicketsOf(pool[i]);
                        if (r < cum) { idx = i; break; }
                    }
                }
                // اگر به‌خاطر گردکردن اعشاری از حلقه بیرون نیامد، آخرین نفر باقی‌مانده همان سبدِ درست است
                if (idx < 0) idx = lastEligible;
                if (idx < 0) break;

                used[idx] = true;
                PhoneProfile p = pool[idx];
                Winner w = new Winner();
                w.Rank = k + 1;
                w.Prize = PrizeOf(res.Prizes, k);
                w.Mobile = p.Mobile;
                w.Name = p.Name;
                w.Points = TicketsOf(p);
                w.Invoices = p.Count;
                w.Amount = p.Amount;
                w.ChanceInitial = total > 0 ? (double)w.Points / (double)total : 0;
                w.RemainingTickets = (int)(remaining > int.MaxValue ? int.MaxValue : remaining);
                w.ChanceAtPick = remaining > 0 ? (double)w.Points / (double)remaining : 0;
                res.Winners.Add(w);

                remaining -= w.Points;
            }

            return res;
        }

        /// <summary>عنوان جایزه برنده شماره k (۰ = برنده اول) بر اساس تعداد برندگان هر جایزه.</summary>
        public static string PrizeOf(List<Prize> prizes, int k)
        {
            if (prizes == null) return "";
            int acc = 0;
            for (int i = 0; i < prizes.Count; i++)
            {
                acc += prizes[i].Winners < 1 ? 1 : prizes[i].Winners;
                if (k < acc) return prizes[i].Title == null ? "" : prizes[i].Title;
            }
            return "";
        }

        /// <summary>شانس به شکل «1 از N» (خوانا در فضای قرعه‌کشی).</summary>
        public static string OddsText(double chance)
        {
            if (chance <= 0) return "—";
            if (chance >= 1) return "1 از 1";
            double oneIn = 1.0 / chance;
            string n = oneIn < 10
                ? oneIn.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)
                : Num.F(oneIn);
            return "1 از " + n;
        }

        /// <summary>شانس به درصد (مثل 57.1٪).</summary>
        public static string PercentText(double chance)
        {
            if (chance <= 0) return "—";
            return Num.P(chance * 100.0) + "٪";
        }
    }
}
