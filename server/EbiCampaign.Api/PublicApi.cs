using System.Collections.Concurrent;

namespace EbiCampaign.Api;

/// <summary>
/// کش کوتاه‌مدت برای مسیرهای عمومی. با هر تغییر داده (ثبت فاکتور، تصمیم، قرعه‌کشی، انتشار)
/// نسل کش عوض می‌شود تا پاسخ‌ها بلافاصله تازه شوند. در چند‌نمونه‌ای، نمونهٔ دیگر حداکثر
/// به اندازهٔ TTL عقب می‌ماند؛ این محدودیت در مستندات زیرساخت ثبت شده است.
/// </summary>
public sealed class PublicCache
{
    private sealed record Entry(object Value, long Generation, DateTimeOffset ExpiresAt);

    private readonly ConcurrentDictionary<string, Entry> _map = new(StringComparer.Ordinal);
    private long _generation;

    public long Generation => Interlocked.Read(ref _generation);

    public void Invalidate() => Interlocked.Increment(ref _generation);

    /// <summary>تعداد آیتم‌های کش (برای آزمون و تشخیص).</summary>
    public int Size => _map.Count;

    public async Task<T> GetOrAddAsync<T>(string key, TimeSpan ttl, Func<Task<T>> factory) where T : class
    {
        long generation = Generation;
        if (_map.TryGetValue(key, out Entry? hit) && hit.Generation == generation
            && hit.ExpiresAt > DateTimeOffset.UtcNow && hit.Value is T typed)
            return typed;

        T value = await factory();
        _map[key] = new Entry(value, generation, DateTimeOffset.UtcNow.Add(ttl));
        if (_map.Count > 512) Prune();
        return value;
    }

    private void Prune()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        foreach (KeyValuePair<string, Entry> pair in _map)
            if (pair.Value.ExpiresAt < now || pair.Value.Generation != Generation)
                _map.TryRemove(pair.Key, out _);
    }
}

/// <summary>محدودکنندهٔ نرخ پنجرهٔ ثابت بر پایهٔ نشانی کلاینت برای مسیرهای عمومی.</summary>
public sealed class PublicRateLimiter
{
    private sealed class Bucket
    {
        public long StartTicks;
        public int Count;
    }

    private readonly ConcurrentDictionary<string, Bucket> _map = new(StringComparer.Ordinal);
    private readonly int _limit;
    private readonly TimeSpan _window;

    public PublicRateLimiter(int limitPerMinute, TimeSpan? window = null)
    {
        _limit = limitPerMinute < 1 ? 1 : limitPerMinute;
        _window = window ?? TimeSpan.FromMinutes(1);
    }

    public int Limit => _limit;

    public TimeSpan WindowLength => _window;

    /// <summary>مصرف یک درخواست؛ در صورت عبور از حد، false برمی‌گرداند.</summary>
    public bool TryAcquire(string key, out int remaining, out int retryAfterSeconds)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        Bucket window = _map.GetOrAdd(key, _ => new Bucket { StartTicks = now.UtcTicks, Count = 0 });
        lock (window)
        {
            if (now.UtcTicks - window.StartTicks >= _window.Ticks)
            {
                window.StartTicks = now.UtcTicks;
                window.Count = 0;
            }
            if (window.Count >= _limit)
            {
                remaining = 0;
                retryAfterSeconds = (int)Math.Ceiling(
                    (_window - (now - new DateTimeOffset(window.StartTicks, TimeSpan.Zero))).TotalSeconds);
                return false;
            }
            window.Count++;
            remaining = _limit - window.Count;
            retryAfterSeconds = 0;
            return true;
        }
    }

    public void Reset() => _map.Clear();
}

/// <summary>
/// بند ۵ و ۶ — سطح عمومی کمپین: شمارندهٔ واقعی، وضعیت دوره، برندگان منتشرشده و
/// سند عمومی قرعه‌کشی. هیچ دادهٔ خصوصی از این مسیر بیرون نمی‌رود.
/// </summary>
public sealed class PublicService
{
    private static readonly TimeSpan CampaignTtl = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan WinnersTtl = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan AuditTtl = TimeSpan.FromMinutes(5);

    private readonly CampaignService _campaign;
    private readonly LotteryService _lottery;
    private readonly CampaignOptions _opt;
    private readonly PublicCache _cache;

    public PublicService(CampaignService campaign, LotteryService lottery, CampaignOptions opt, PublicCache cache)
    {
        _campaign = campaign;
        _lottery = lottery;
        _opt = opt;
        _cache = cache;
    }

    public async Task<PublicCampaignStatus> GetCampaignAsync(CancellationToken ct = default)
        => await _cache.GetOrAddAsync("public:campaign", CampaignTtl, async () =>
        {
            RoundInfo? round = await _campaign.GetCurrentRoundAsync(ct);
            LotteryRunInfo? published = await _lottery.GetLatestPublishedAsync(ct);

            PublicCampaignStatus status = new()
            {
                PrizeToman = _opt.PrizeToman,
                UpdatedAt = DateTime.UtcNow,
                Target = round?.Target ?? _opt.TargetInvoices
            };

            if (round == null)
            {
                status.State = "NoActiveRound";
                status.StateText = "کمپین هنوز دورهای را آغاز نکرده است.";
                status.AwaitingManagerStart = true;
                status.AcceptingNewInvoices = false;
            }
            else
            {
                status.RoundNo = round.RoundNo;
                status.RoundStatus = round.Status;
                // شمارندهٔ دورهٔ تکمیل‌شده صفر نمی‌شود؛ همان مقدار نهایی نمایش داده می‌شود.
                status.Count = round.Count;
                status.Target = round.Target;
                status.Remaining = Math.Max(0, round.Target - round.Count);
                status.ProgressPercent = round.Target > 0
                    ? Math.Round(Math.Min(100d, round.Count * 100d / round.Target), 1)
                    : 0;

                bool collecting = round.Status == "Collecting";
                status.AcceptingNewInvoices = collecting;
                LotteryRunInfo? run = collecting ? null : await _lottery.GetActiveRunOfRoundAsync(round.RoundId, ct);

                if (collecting)
                {
                    status.State = "Collecting";
                    status.StateText = "دورهٔ جاری در حال جمع‌آوری فاکتور است.";
                }
                else if (run != null && run.Status == "RequiresReview")
                {
                    status.State = "RequiresReview";
                    status.StateText = "نتیجهٔ این دوره پیش از قرعه‌کشی نیازمند بررسی مدیریتی است.";
                }
                else if (run != null && run.PublishApproved && (run.Status == "Selected" || run.Status == "Finalized"))
                {
                    status.State = "ResultPublished";
                    status.StateText = "نتیجهٔ رسمی این دوره اعلام شده است.";
                }
                else
                {
                    status.State = "DrawPending";
                    status.StateText = "دوره تکمیل شده و قرعه‌کشی آن هنوز اعلام نشده است.";
                }

                status.AwaitingManagerStart = !collecting;
            }

            status.LastResult = published == null ? null : ToCard(published, _opt);
            return status;
        });

    public async Task<PublicWinnersPage> GetWinnersAsync(int page, int perPage, int? roundNo,
        CancellationToken ct = default)
    {
        string key = $"public:winners:{page}:{perPage}:{(roundNo?.ToString() ?? "-")}";
        return await _cache.GetOrAddAsync(key, WinnersTtl, async () =>
        {
            int offset = (page - 1) * perPage;
            List<LotteryRunInfo> runs = await _lottery.ListPublishedAsync(perPage + 1, offset, roundNo, ct);
            bool hasMore = runs.Count > perPage;
            if (hasMore) runs.RemoveAt(runs.Count - 1);

            List<PublicWinnerCard> cards = runs.Select(r => ToCard(r, _opt)).ToList();
            // تعداد کل برای صفحه‌بندی: با یک واکشی سبک محاسبه می‌شود.
            int total = await _lottery.CountPublishedAsync(roundNo, ct);
            return new PublicWinnersPage
            {
                Page = page,
                PerPage = perPage,
                Total = total,
                HasMore = hasMore,
                Winners = cards
            };
        });
    }

    public async Task<PublicWinnerCard?> GetWinnerAsync(int roundNo, CancellationToken ct = default)
        => await _cache.GetOrAddAsync<PublicWinnerCard?>($"public:winner:{roundNo}", WinnersTtl,
            async () => await _lottery.GetPublishedByRoundAsync(roundNo, ct) is LotteryRunInfo run
                ? ToCard(run, _opt)
                : null);

    public async Task<PublicLotteryReceipt?> GetReceiptAsync(int runId, CancellationToken ct = default)
        => await _cache.GetOrAddAsync<PublicLotteryReceipt>($"public:receipt:{runId}", AuditTtl, async () =>
        {
            LotteryRunInfo run = await RequirePublishedAsync(runId, ct);
            LotteryVerifyResult verify = await _lottery.VerifyAsync(runId, ct);
            PublicLotteryReceipt receipt = new()
            {
                RunId = run.RunId,
                RoundNo = run.RoundNo,
                Algorithm = run.Algorithm,
                Status = run.Status,
                EntryCount = run.EntryCount,
                EligibleCount = run.EligibleCount,
                TotalWeight = run.TotalWeight,
                PublicEntriesHash = run.PublicEntriesHash ?? "",
                CommitmentHash = run.CommitmentHash,
                SeedHex = run.SeedHex,
                SeedRevealed = run.SeedHex != null,
                DrawValue = run.WinnerDrawValue,
                WinnerEntryNo = run.WinnerEntryNo,
                WinnerPseudonym = run.SeedHex != null && run.WinnerEntryNo.HasValue
                    ? LotteryEngine.Pseudonym(run.SeedHex, run.WinnerEntryNo.Value)
                    : null,
                MobileMasked = Security.MaskMobile(run.WinnerMobileFull),
                DisplayName = run.PublishedDisplayName,
                ImageUrl = run.WinnerImageApproved ? run.WinnerImageUrl : null,
                PreparedAt = run.PreparedAt,
                DrawnAt = run.SelectedAt,
                FinalizedAt = run.FinalizedAt,
                PublishedAt = run.PublishApprovedAt,
                Verified = verify.PublicEntriesHashMatches && verify.CommitmentMatches && verify.WinnerReproduces,
                VerificationNote = verify.Note
            };
            return receipt;
        });

    public async Task<PublicEntriesPage> GetEntriesAsync(int runId, int page, int perPage,
        CancellationToken ct = default)
    {
        LotteryRunInfo run = await RequirePublishedAsync(runId, ct);
        if (string.IsNullOrEmpty(run.SeedHex))
            throw new ApiException(409, "result_not_revealed", "فهرست ورودی‌ها پس از افشای Seed منتشر می‌شود.");

        int offset = (page - 1) * perPage;
        List<PublicEntry> entries = await _lottery.ListPublicEntriesAsync(runId, perPage, offset, ct);
        return new PublicEntriesPage
        {
            RunId = runId,
            PublicEntriesHash = run.PublicEntriesHash ?? "",
            Page = page,
            PerPage = perPage,
            Total = run.EntryCount,
            HasMore = offset + entries.Count < run.EntryCount,
            Entries = entries
        };
    }

    public async Task<PublicVerifyResult?> GetVerifyAsync(int runId, CancellationToken ct = default)
        => await _cache.GetOrAddAsync<PublicVerifyResult>($"public:verify:{runId}", AuditTtl, async () =>
        {
            LotteryRunInfo run = await RequirePublishedAsync(runId, ct);
            LotteryVerifyResult verify = await _lottery.VerifyAsync(runId, ct);
            return new PublicVerifyResult
            {
                RunId = runId,
                RoundNo = run.RoundNo,
                Algorithm = run.Algorithm,
                SeedRevealed = verify.SeedRevealed,
                PublicEntriesHashMatches = verify.PublicEntriesHashMatches,
                CommitmentMatches = verify.CommitmentMatches,
                WinnerReproduces = verify.WinnerReproduces,
                WinnerEntryNo = verify.RecomputedEntryNo,
                WinnerPseudonym = run.SeedHex != null && verify.RecomputedEntryNo.HasValue
                    ? LotteryEngine.Pseudonym(run.SeedHex, verify.RecomputedEntryNo.Value)
                    : null,
                Note = verify.Note
            };
        });

    // ------------------------------------------------------------------
    private async Task<LotteryRunInfo> RequirePublishedAsync(int runId, CancellationToken ct)
    {
        LotteryRunInfo? run = await _lottery.GetRunAsync(runId, false, ct);
        if (run == null) throw new ApiException(404, "not_found", "سند قرعه‌کشی پیدا نشد.");
        // پیش از تأیید انتشار، سند عمومی منتشر نمی‌شود.
        if (run.Status != "Finalized" || !run.PublishApproved)
            throw new ApiException(404, "not_found", "سند قرعه‌کشی پیدا نشد.");
        return run;
    }

    internal static PublicWinnerCard ToCard(LotteryRunInfo run, CampaignOptions opt) => new()
    {
        RoundNo = run.RoundNo,
        DrawnAt = run.SelectedAt,
        PublishedAt = run.PublishApprovedAt?.ToString("yyyy-MM-dd HH:mm:ss"),
        PrizeToman = opt.PrizeToman,
        MobileMasked = Security.MaskMobile(run.WinnerMobileFull),
        DisplayName = run.PublishedDisplayName,
        ImageUrl = run.WinnerImageApproved ? run.WinnerImageUrl : null,
        EntryCount = run.EntryCount,
        EligibleCount = run.EligibleCount,
        TotalWeight = run.TotalWeight,
        VerificationStatus = run.SeedHex != null && !string.IsNullOrEmpty(run.PublicEntriesHash)
            ? "recomputable"
            : "pending"
    };
}