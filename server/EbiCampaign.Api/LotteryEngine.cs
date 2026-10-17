using System.Security.Cryptography;
using System.Text;

namespace EbiCampaign.Api;

/// <summary>
/// موتور تصادفی قرعه‌کشی: مولد جریان قطعی از Seed تثبیت‌شده (HMAC-SHA256 DRBG) و
/// انتخاب وزنی بدون سوگیری ناشی از گردکردن.
///
/// الگوریتم: ebi-lottery-weighted-v2
/// تفاوت با v1: تعهد و زمینهٔ انتخاب به «هش عمومی ورودی‌ها» گره خورده است، نه هش داخلی
/// مجموعه. بنابراین ممیز مستقل می‌تواند بدون دسترسی به دادهٔ خصوصی فاکتورها، هم تعهد
/// و هم برنده را از سند عمومی بازتولید کند؛ هش داخلی فقط برای ممیزی محرمانه می‌ماند.
/// </summary>
public static class LotteryEngine
{
    public const string Algorithm = "ebi-lottery-weighted-v2";
    public const int SeedBytes = 32;

    /// <summary>طول شناسهٔ مستعار هر ورودی در سند عمومی (هگز).</summary>
    public const int PseudonymHexLength = 12;

    /// <summary>Seed از CSPRNG سیستم (نه System.Random).</summary>
    public static byte[] NewSeed() => RandomNumberGenerator.GetBytes(SeedBytes);

    public static string Hex(byte[] seed) => Convert.ToHexString(seed).ToLowerInvariant();

    /// <summary>تعهد منتشرشده پیش از انتخاب: SHA-256(الگوریتم|دوره|هش مجموعه|Seed).</summary>
    // ورودی این دو تابع باید «هش عمومی ورودی‌ها» باشد تا تعهد و انتخاب با سند عمومی
    // قابل بازتولید باشند. هش داخلی تنها برای ممیزی محرمانه نگه داشته می‌شود.
    public static string Commitment(int roundNo, string publicEntriesHash, string seedHex)
        => Security.Sha256Hex($"{Algorithm}|round={roundNo}|entries={publicEntriesHash}|seed={seedHex}");

    /// <summary>زمینهٔ جریان تصادفی؛ به مجموعهٔ تثبیت‌شده و دوره گره خورده است.</summary>
    public static string Context(int roundNo, string publicEntriesHash)
        => $"round={roundNo}|entries={publicEntriesHash}";

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

    /// <summary>
    /// شناسهٔ مستعار یک‌طرفهٔ هر ورودی برای سند عمومی: HMAC(seed, "pseudonym|entry=n").
    /// از فاکتور، موبایل یا حساب بله هیچ اطلاعاتی برنمی‌گرداند و تنها پس از افشای Seed
    /// قابل بازتولید است، پس پیش از قرعه‌کشی قابل نقشه‌برداری نیست.
    /// </summary>
    public static string Pseudonym(string seedHex, int entryNo)
        => Security.HmacHex(seedHex, $"pseudonym|entry={entryNo}")[..PseudonymHexLength];

    /// <summary>خط عمومی ورودی: شناسهٔ مستعار، صلاحیت و وزن — بدون هیچ دادهٔ شخصی یا مالی.</summary>
    public static string PublicEntryLine(int entryNo, string pseudonym, string eligibility, long weight)
        => $"{entryNo}|{pseudonym}|{eligibility}|{weight}";

    /// <summary>هش عمومی ورودی‌ها؛ همان ورودی‌ها با شناسهٔ مستعار به‌جای دادهٔ خصوصی.</summary>
    public static string PublicEntriesHash(IEnumerable<string> publicLines)
        => EntriesHash(publicLines);

    /// <summary>هش مجموعهٔ داخلی (محرمانه، فقط برای ممیزی): ترتیب، شناسه‌ها، وزن و صلاحیت را تعهد می‌کند.</summary>
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