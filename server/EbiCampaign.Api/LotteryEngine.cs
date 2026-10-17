using System.Security.Cryptography;
using System.Text;

namespace EbiCampaign.Api;

/// <summary>
/// موتور تصادفی قرعه‌کشی: مولد جریان قطعی از Seed تثبیت‌شده (HMAC-SHA256 DRBG) و
/// انتخاب وزنی بدون سوگیری ناشی از گردکردن.
/// الگوریتم: ebi-lottery-weighted-v1
/// </summary>
public static class LotteryEngine
{
    public const string Algorithm = "ebi-lottery-weighted-v1";
    public const int SeedBytes = 32;

    /// <summary>Seed از CSPRNG سیستم (نه System.Random).</summary>
    public static byte[] NewSeed() => RandomNumberGenerator.GetBytes(SeedBytes);

    public static string Hex(byte[] seed) => Convert.ToHexString(seed).ToLowerInvariant();

    /// <summary>تعهد منتشرشده پیش از انتخاب: SHA-256(الگوریتم|دوره|هش مجموعه|Seed).</summary>
    public static string Commitment(int roundNo, string entriesHash, string seedHex)
        => Security.Sha256Hex($"{Algorithm}|round={roundNo}|entries={entriesHash}|seed={seedHex}");

    /// <summary>زمینهٔ جریان تصادفی؛ به مجموعهٔ تثبیت‌شده و دوره گره خورده است.</summary>
    public static string Context(int roundNo, string entriesHash)
        => $"round={roundNo}|entries={entriesHash}";

    /// <summary>قطعهٔ شمارهٔ counter از جریان: HMAC(key=seed, msg=الگوریتم|زمینه|counter).</summary>
    private static byte[] Block(byte[] seed, string context, ulong counter)
    {
        using HMACSHA256 h = new(seed);
        byte[] msg = Encoding.UTF8.GetBytes($"{Algorithm}|{context}|{counter}");
        return h.ComputeHash(msg);
    }

    /// <summary>
    /// عدد یکنواخت در بازهٔ [0, bound) با ردکردن نمونه‌های خارج از بزرگ‌ترین مضرب bound؛
    /// بنابراین سوگیری باقی‌مانده‌ٔ تقسیم وجود ندارد.
    /// </summary>
    public static ulong NextBelow(byte[] seed, string context, ulong bound, ref ulong counter)
    {
        if (bound == 0) throw new ArgumentOutOfRangeException(nameof(bound), "بازهٔ انتخاب صفر است.");
        if (bound == 1) return 0;
        ulong limit = ulong.MaxValue - (ulong.MaxValue % bound); // بزرگ‌ترین مضرب bound ≤ ulong.MaxValue
        while (true)
        {
            ulong value = BitConverter.ToUInt64(Block(seed, context, counter++), 0);
            if (value < limit) return value % bound;
        }
    }

    /// <summary>
    /// انتخاب یک اندیس از میان وزن‌های داده‌شده با احتمال w_i / Σw.
    /// وزن صفر شانس انتخاب ندارد؛ ورودی‌های با وزن صفر نباید در این فهرست باشند.
    /// </summary>
    public static int PickWeighted(byte[] seed, string context, IReadOnlyList<long> weights,
        out long draw, out long totalWeight)
    {
        totalWeight = 0;
        for (int i = 0; i < weights.Count; i++)
        {
            if (weights[i] < 0) throw new ArgumentOutOfRangeException(nameof(weights), "وزن منفی مجاز نیست.");
            totalWeight += weights[i];
        }
        if (totalWeight <= 0) throw new InvalidOperationException("مجموع وزن ورودی‌های واجد شرایط صفر است.");

        ulong counter = 0;
        draw = (long)NextBelow(seed, context, (ulong)totalWeight, ref counter);

        long acc = 0;
        for (int i = 0; i < weights.Count; i++)
        {
            acc += weights[i];
            if (draw < acc) return i;
        }
        return weights.Count - 1; // از نظر ریاضی دست‌نیافتنی؛ فقط برای اطمینان
    }

    /// <summary>هش مجموعهٔ ورودی‌ها؛ ترتیب، شناسه‌ها، وزن و صلاحیت را تعهد می‌کند.</summary>
    public static string EntriesHash(IEnumerable<string> canonicalLines)
    {
        StringBuilder sb = new();
        foreach (string line in canonicalLines) sb.Append(line).Append('\n');
        return Security.Sha256Hex(sb.ToString());
    }

    public static string EntryLine(int entryNo, long invoiceId, long fCode, string mobile, int points,
        string eligibility, string? baleStatus)
        => $"{entryNo}|{invoiceId}|{fCode}|{mobile}|{points}|{eligibility}|{baleStatus ?? "-"}";
}