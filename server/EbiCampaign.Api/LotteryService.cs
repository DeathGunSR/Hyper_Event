using System.Data;
using Microsoft.Data.SqlClient;

namespace EbiCampaign.Api;

/// <summary>
/// بند ۵ — موتور قرعه‌کشی قابل حسابرسی.
/// این سرویس در سرویس مرکزی اجرا می‌شود؛ نه در صفحهٔ عمومی، نه در افزونهٔ وردپرس و نه در نرم‌افزار صندوق.
/// مراحل: قفل دوره، بررسی مغایرت‌های حل‌نشده، تثبیت مجموعهٔ ورودی‌ها، تعهد هش، بررسی عضویت بله،
/// انتخاب وزنی با CSPRNG، ثبت نتیجه و سند حسابرسی، و نهایی‌سازی اتمیک.
/// </summary>
public sealed class LotteryService
{
    private readonly Db _db;
    private readonly CampaignOptions _opt;
    private readonly IBaleMembershipProvider _bale;
    private readonly ILogger<LotteryService> _log;

    public LotteryService(Db db, CampaignOptions opt, IBaleMembershipProvider bale, ILogger<LotteryService> log)
    {
        _db = db;
        _opt = opt;
        _bale = bale;
        _log = log;
    }

    // ------------------------------------------------------------------
    //  ۵.۱ / ۵.۳ — تثبیت مجموعهٔ ورودی‌ها و تعهد پیش از انتخاب
    // ------------------------------------------------------------------
    public async Task<LotteryRunInfo> PrepareAsync(int roundId, bool acknowledgeReviews, string actor,
        CancellationToken ct = default)
    {
        actor = CleanActor(actor);

        // فاز ۱: خواندن پایدار دوره و فاکتورهای معتبر (بدون نگه‌داشتن قفل در زمان تماس شبکه‌ای)
        RoundSnapshot round;
        List<EntryDraft> drafts;
        string snapshotHash;
        int unresolvedReviews;
        await using (SqlConnection cn = await _db.OpenAsync(ct))
        {
            round = await ReadRoundAsync(cn, roundId, ct)
                ?? throw new ApiException(404, "round_not_found", "دوره پیدا نشد.");

            if (round.Status != "Completed" && round.Status != "Closed")
                throw new ApiException(409, "round_not_completed",
                    "قرعه‌کشی فقط برای دورهٔ تکمیل‌شده (۱۰۰۰ فاکتور) امکان‌پذیر است.");

            if (round.Count < round.Target)
                throw new ApiException(409, "round_incomplete",
                    $"دوره فقط {round.Count} فاکتور معتبر دارد و به {round.Target} نرسیده است.");

            LotteryRunInfo? existing = await ReadRunByRoundAsync(cn, roundId, false, ct);
            if (existing != null) return existing;

            unresolvedReviews = await CountReviewsAsync(cn, roundId, ct);
            if (unresolvedReviews > 0 && !acknowledgeReviews)
                throw new ApiException(409, "unresolved_reviews",
                    $"{unresolvedReviews} فاکتور این دوره در وضعیت بررسی است؛ نخست با مسیر تصمیم تعیین تکلیف شود " +
                    "یا با AcknowledgeReviews=true تأیید کنید که بدون آن‌ها قرعه‌کشی شود.");

            (drafts, snapshotHash) = await ReadCandidatesAsync(cn, roundId, ct);
        }

        if (drafts.Count == 0)
            throw new ApiException(409, "no_invoices", "هیچ فاکتور معتبری در این دوره برای قرعه‌کشی وجود ندارد.");

        // بررسی عضویت بله در زمان قرعه‌کشی (۵.۱ گام ۶)
        Dictionary<string, BaleCheck> baleCache = new(StringComparer.Ordinal);
        foreach (EntryDraft d in drafts)
        {
            if (d.Mobile.Length == 0) continue;
            if (!baleCache.TryGetValue(d.Mobile, out BaleCheck? check))
            {
                check = await _bale.CheckAsync(d.Mobile, ct);
                baleCache[d.Mobile] = check;
            }
            d.Bale = check;
        }

        byte[] seed = LotteryEngine.NewSeed();
        string seedHex = LotteryEngine.Hex(seed);

        // فاز ۲: نوشتن Snapshot با بازبینی پایداری داده
        await using SqlConnection wcn = await _db.OpenAsync(ct);
        await using SqlTransaction tx = (SqlTransaction)await wcn.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        try
        {
            RoundSnapshot again = await ReadRoundAsync(wcn, roundId, tx, ct)
                ?? throw new ApiException(404, "round_not_found", "دوره پیدا نشد.");
            if (again.Status != "Completed" && again.Status != "Closed")
                throw new ApiException(409, "round_not_completed", "وضعیت دوره حین تثبیت مجموعه تغییر کرد.");

            (List<EntryDraft> fresh, string freshHash) = await ReadCandidatesAsync(wcn, roundId, tx, ct);
            if (fresh.Count != drafts.Count || !string.Equals(freshHash, snapshotHash, StringComparison.Ordinal))
                throw new ApiException(409, "snapshot_unstable",
                    "مجموعهٔ فاکتورهای دوره حین تثبیت تغییر کرد؛ لطفاً بلافاصله دوباره تلاش کنید.");

            List<string> lines = new();
            List<string> publicLines = new();
            int entryNo = 0;
            int unknown = 0;
            foreach (EntryDraft d in drafts)
            {
                d.EntryNo = ++entryNo;
                d.Eligibility = Classify(d, out long weight);
                d.Weight = weight;
                if (string.Equals(d.Bale?.Code, "unknown", StringComparison.Ordinal)) unknown++;
                lines.Add(LotteryEngine.EntryLine(d.EntryNo, d.InvoiceId, d.FCode, d.Mobile, d.Points,
                    d.Eligibility, d.Bale?.Code));
                publicLines.Add(LotteryEngine.PublicEntryLine(d.EntryNo,
                    LotteryEngine.Pseudonym(seedHex, d.EntryNo), d.Eligibility, d.Weight));
            }

            string entriesHash = LotteryEngine.EntriesHash(lines);
            string publicEntriesHash = LotteryEngine.PublicEntriesHash(publicLines);
            // تعهد به هش عمومی گره می‌خورد تا ممیز مستقل بدون دادهٔ خصوصی آن را بازتولید کند.
            string commitment = LotteryEngine.Commitment(round.RoundNo, publicEntriesHash, seedHex);
            int eligibleCount = drafts.Count(x => x.Eligibility == "eligible");
            long totalWeight = drafts.Sum(x => x.Weight);

            string status;
            string? reviewReason = null;
            if (_opt.RequireBaleMembership && unknown > 0)
            {
                status = "RequiresReview";
                reviewReason = "bale_membership_unverified";
            }
            else if (eligibleCount == 0)
            {
                status = "RequiresReview";
                reviewReason = "no_eligible_entries";
            }
            else
            {
                status = "Committed";
            }

            int runId;
            try
            {
                using SqlCommand ins = new(@"
INSERT INTO dbo.LotteryRuns(RoundId, Status, Algorithm, EntriesHash, PublicEntriesHash, EntryCount, EligibleCount,
    TotalWeight, CommitmentHash, SeedHex, AcknowledgedReviews, ReviewReason, PreparedBy, PreparedAt)
VALUES(@rid, @status, @algo, @hash, @publichash, @n, @eligible, @total, @commitment, @seed, @ack, @reason, @actor,
       SYSUTCDATETIME());
SELECT CAST(SCOPE_IDENTITY() AS INT);", wcn, tx);
                ins.Parameters.AddWithValue("@rid", roundId);
                ins.Parameters.AddWithValue("@status", status);
                ins.Parameters.AddWithValue("@algo", LotteryEngine.Algorithm);
                ins.Parameters.AddWithValue("@hash", entriesHash);
                ins.Parameters.AddWithValue("@publichash", publicEntriesHash);
                ins.Parameters.AddWithValue("@n", drafts.Count);
                ins.Parameters.AddWithValue("@eligible", eligibleCount);
                ins.Parameters.AddWithValue("@total", totalWeight);
                ins.Parameters.AddWithValue("@commitment", commitment);
                ins.Parameters.AddWithValue("@seed", seedHex);
                ins.Parameters.AddWithValue("@ack", acknowledgeReviews ? unresolvedReviews : 0);
                ins.Parameters.AddWithValue("@reason", (object?)reviewReason ?? DBNull.Value);
                ins.Parameters.AddWithValue("@actor", actor);
                runId = Convert.ToInt32(await ins.ExecuteScalarAsync(ct));
            }
            catch (SqlException ex) when (ex.Number == 2601 || ex.Number == 2627)
            {
                // یک فراگرفت فعال دیگر برای همین دوره ساخته شد؛ همان برگردانده می‌شود.
                await tx.RollbackAsync(ct);
                await using SqlConnection rcn = await _db.OpenAsync(ct);
                LotteryRunInfo? raced = await ReadRunByRoundAsync(rcn, roundId, false, ct);
                if (raced != null) return raced;
                throw;
            }

            await InsertEntriesAsync(wcn, tx, runId, drafts, ct);
            await AuditWriter.WriteAsync(wcn, tx, actor, "lottery.prepare", "LotteryRun", runId.ToString(),
                new
                {
                    roundId, roundNo = round.RoundNo, runId, status, entries = drafts.Count,
                    eligibleCount, totalWeight, entriesHash, publicEntriesHash, commitment,
                    acknowledgedReviews = acknowledgeReviews ? unresolvedReviews : 0, reviewReason,
                    baleProvider = _bale.Name
                }, ct);
            await tx.CommitAsync(ct);

            _log.LogInformation("lottery prepare run={RunId} round={RoundId} status={Status} entries={N} eligible={E}",
                runId, roundId, status, drafts.Count, eligibleCount);

            await using SqlConnection rcn2 = await _db.OpenAsync(ct);
            return (await ReadRunByRoundAsync(rcn2, roundId, false, ct))
                ?? throw new ApiException(500, "run_missing", "فراگرفت قرعه‌کشی پس از ثبت پیدا نشد.");
        }
        catch (ApiException)
        {
            try { await tx.RollbackAsync(ct); } catch { }
            throw;
        }
        catch
        {
            try { await tx.RollbackAsync(ct); } catch { }
            throw;
        }
    }

    // ------------------------------------------------------------------
    //  ۵.۲ — اجرای انتخاب
    // ------------------------------------------------------------------
    public async Task<LotteryRunInfo> SelectAsync(int runId, string actor, CancellationToken ct = default)
    {
        actor = CleanActor(actor);
        await using SqlConnection cn = await _db.OpenAsync(ct);
        await using SqlTransaction tx = (SqlTransaction)await cn.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        try
        {
            LotteryRunInfo run = await LockRunAsync(cn, tx, runId, ct)
                ?? throw new ApiException(404, "run_not_found", "فراگرفت قرعه‌کشی پیدا نشد.");

            // یک درخواست نمی‌تواند چند نتیجه بسازد: انتخاب قبلی بازگردانده می‌شود.
            if (run.Status == "Selected" || run.Status == "Finalized")
            {
                await tx.CommitAsync(ct);
                return run;
            }

            if (run.Status == "Aborted")
                throw new ApiException(409, "run_aborted", "این فراگرفت لغو شده است.");
            if (run.Status == "RequiresReview")
                throw new ApiException(409, "run_requires_review",
                    "این فراگرفت در وضعیت نیازمند بررسی مدیریتی است: " + (run.ReviewReason ?? "-"));

            List<WeightedEntry> eligible = await ReadEligibleAsync(cn, tx, runId, ct);
            long totalWeight = eligible.Sum(x => x.Weight);

            if (totalWeight <= 0)
            {
                using SqlCommand upd = new(@"
UPDATE dbo.LotteryRuns SET Status='RequiresReview', ReviewReason='zero_total_weight' WHERE RunId=@id;", cn, tx);
                upd.Parameters.AddWithValue("@id", runId);
                await upd.ExecuteNonQueryAsync(ct);
                await AuditWriter.WriteAsync(cn, tx, actor, "lottery.no_winner", "LotteryRun", runId.ToString(),
                    new { runId, reason = "zero_total_weight" }, ct);
                await tx.CommitAsync(ct);
                await using SqlConnection rcn = await _db.OpenAsync(ct);
                return (await ReadRunAsync(rcn, runId, false, ct))!;
            }

            byte[] seed = Convert.FromHexString(run.FullSeedHex
                ?? throw new ApiException(500, "seed_missing", "Seed تثبیت‌شدهٔ این فراگرفت پیدا نشد."));
            string context = LotteryEngine.Context(run.RoundNo, PublicHashOf(run));
            int index = LotteryEngine.PickWeighted(seed, context, eligible.Select(x => x.Weight).ToList(),
                out long draw, out long total);
            WeightedEntry winner = eligible[index];

            // انتخاب اتمیک: فقط اگر هنوز Committed باشد اعمال می‌شود، بنابراین دو درخواست همزمان
            // نمی‌توانند دو برنده بسازند.
            int changed;
            using (SqlCommand upd = new(@"
UPDATE dbo.LotteryRuns
SET Status='Selected', SeedHex=@seed, SelectedAt=SYSUTCDATETIME(), SelectedBy=@actor,
    WinnerInvoiceId=@inv, WinnerFCode=@fcode, WinnerEntryNo=@entry, WinnerCustomerCode=@cust,
    WinnerMobile=@mobile, WinnerPoints=@points, WinnerDrawValue=@draw, WinnerTotalWeight=@total,
    ReviewReason=NULL
WHERE RunId=@id AND Status='Committed';", cn, tx))
            {
                upd.Parameters.AddWithValue("@seed", run.FullSeedHex!);
                upd.Parameters.AddWithValue("@actor", actor);
                upd.Parameters.AddWithValue("@inv", winner.InvoiceId);
                upd.Parameters.AddWithValue("@fcode", winner.FCode);
                upd.Parameters.AddWithValue("@entry", winner.EntryNo);
                upd.Parameters.AddWithValue("@cust", (object?)winner.CustomerCode ?? DBNull.Value);
                upd.Parameters.AddWithValue("@mobile", (object?)winner.Mobile ?? DBNull.Value);
                upd.Parameters.AddWithValue("@points", winner.Points);
                upd.Parameters.AddWithValue("@draw", draw);
                upd.Parameters.AddWithValue("@total", total);
                upd.Parameters.AddWithValue("@id", runId);
                changed = await upd.ExecuteNonQueryAsync(ct);
            }

            if (changed == 0)
            {
                await tx.RollbackAsync(ct);
                await using SqlConnection rcn = await _db.OpenAsync(ct);
                return (await ReadRunAsync(rcn, runId, false, ct))!;
            }

            await AuditWriter.WriteAsync(cn, tx, actor, "lottery.select", "LotteryRun", runId.ToString(),
                new
                {
                    runId, roundId = run.RoundId, roundNo = run.RoundNo, algorithm = LotteryEngine.Algorithm,
                    entriesHash = run.EntriesHash, publicEntriesHash = PublicHashOf(run),
                    commitment = run.CommitmentHash, seed = run.FullSeedHex,
                    eligibleCount = eligible.Count, totalWeight = total, draw,
                    winnerEntryNo = winner.EntryNo, winnerInvoiceId = winner.InvoiceId, winnerFCode = winner.FCode,
                    winnerPoints = winner.Points
                }, ct);
            await tx.CommitAsync(ct);

            _log.LogInformation("lottery select run={RunId} round={RoundId} winner={FCode} draw={Draw} total={Total}",
                runId, run.RoundId, winner.FCode, draw, total);

            await using SqlConnection rcn2 = await _db.OpenAsync(ct);
            return (await ReadRunAsync(rcn2, runId, false, ct))!;
        }
        catch (ApiException)
        {
            try { await tx.RollbackAsync(ct); } catch { }
            throw;
        }
        catch
        {
            try { await tx.RollbackAsync(ct); } catch { }
            throw;
        }
    }

    /// <summary>نهایی‌سازی اتمیک نتیجه؛ پس از این مرحله نتیجهٔ رسمی دوره تثبیت می‌شود.</summary>
    public async Task<LotteryRunInfo> FinalizeAsync(int runId, string actor, CancellationToken ct = default)
    {
        actor = CleanActor(actor);
        await using SqlConnection cn = await _db.OpenAsync(ct);
        await using SqlTransaction tx = (SqlTransaction)await cn.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        try
        {
            LotteryRunInfo run = await LockRunAsync(cn, tx, runId, ct)
                ?? throw new ApiException(404, "run_not_found", "فراگرفت قرعه‌کشی پیدا نشد.");

            if (run.Status == "Finalized")
            {
                await tx.CommitAsync(ct);
                return run;
            }
            if (run.Status != "Selected")
                throw new ApiException(409, "run_not_selected",
                    "فقط فراگرفتی که برندهٔ آن انتخاب شده است قابل نهایی‌سازی است.");

            using (SqlCommand upd = new(@"
UPDATE dbo.LotteryRuns SET Status='Finalized', FinalizedBy=@actor, FinalizedAt=SYSUTCDATETIME()
WHERE RunId=@id AND Status='Selected';", cn, tx))
            {
                upd.Parameters.AddWithValue("@actor", actor);
                upd.Parameters.AddWithValue("@id", runId);
                await upd.ExecuteNonQueryAsync(ct);
            }

            await AuditWriter.WriteAsync(cn, tx, actor, "lottery.finalize", "LotteryRun", runId.ToString(),
                new
                {
                    runId, roundId = run.RoundId, winnerFCode = run.WinnerFCode, winnerEntryNo = run.WinnerEntryNo,
                    entriesHash = run.EntriesHash, publicEntriesHash = PublicHashOf(run),
                    commitment = run.CommitmentHash, seed = run.FullSeedHex
                }, ct);
            await tx.CommitAsync(ct);
            await using SqlConnection rcn = await _db.OpenAsync(ct);
            return (await ReadRunAsync(rcn, runId, false, ct))!;
        }
        catch (ApiException)
        {
            try { await tx.RollbackAsync(ct); } catch { }
            throw;
        }
        catch
        {
            try { await tx.RollbackAsync(ct); } catch { }
            throw;
        }
    }

    /// <summary>لغو فراگرفت پیش از انتخاب؛ برای تلاش مجدد پس از رفع علت «نیازمند بررسی».</summary>
    public async Task<LotteryRunInfo> AbortAsync(int runId, string reason, string actor, CancellationToken ct = default)
    {
        actor = CleanActor(actor);
        reason = Trim(reason, 200);
        await using SqlConnection cn = await _db.OpenAsync(ct);
        await using SqlTransaction tx = (SqlTransaction)await cn.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        try
        {
            LotteryRunInfo run = await LockRunAsync(cn, tx, runId, ct)
                ?? throw new ApiException(404, "run_not_found", "فراگرفت قرعه‌کشی پیدا نشد.");

            if (run.Status == "Selected" || run.Status == "Finalized")
                throw new ApiException(409, "run_locked",
                    "فراگرفتی که برندهٔ آن انتخاب شده است قابل لغو نیست؛ مسیر ویژهٔ حسابرسی را ببینید.");
            if (run.Status == "Aborted")
            {
                await tx.CommitAsync(ct);
                return run;
            }
            if (reason.Length == 0)
                throw new ApiException(400, "reason_required", "دلیل لغو فراگرفت الزامی است.");

            using (SqlCommand upd = new(@"
UPDATE dbo.LotteryRuns SET Status='Aborted', ReviewReason=@reason WHERE RunId=@id AND Status<>'Aborted';", cn, tx))
            {
                upd.Parameters.AddWithValue("@reason", reason);
                upd.Parameters.AddWithValue("@id", runId);
                await upd.ExecuteNonQueryAsync(ct);
            }
            await AuditWriter.WriteAsync(cn, tx, actor, "lottery.abort", "LotteryRun", runId.ToString(),
                new { runId, roundId = run.RoundId, prevStatus = run.Status, reason }, ct);
            await tx.CommitAsync(ct);
            await using SqlConnection rcn = await _db.OpenAsync(ct);
            return (await ReadRunAsync(rcn, runId, false, ct))!;
        }
        catch (ApiException)
        {
            try { await tx.RollbackAsync(ct); } catch { }
            throw;
        }
        catch
        {
            try { await tx.RollbackAsync(ct); } catch { }
            throw;
        }
    }

    // ------------------------------------------------------------------
    //  مشاهده / سند حسابرسی / راستی‌آزمایی (۵.۴)
    // ------------------------------------------------------------------
    public async Task<LotteryRunInfo?> GetRunAsync(int runId, bool withEntries = true, CancellationToken ct = default)
    {
        await using SqlConnection cn = await _db.OpenAsync(ct);
        return await ReadRunAsync(cn, runId, withEntries, ct);
    }

    public async Task<List<LotteryRunInfo>> ListRunsAsync(int limit = 50, CancellationToken ct = default)
    {
        limit = Math.Clamp(limit, 1, 200);
        await using SqlConnection cn = await _db.OpenAsync(ct);
        List<LotteryRunInfo> list = new();
        using SqlCommand cmd = new($@"
SELECT TOP (@n) {RunColumns} FROM dbo.LotteryRuns r INNER JOIN dbo.Rounds ro ON ro.RoundId = r.RoundId
ORDER BY r.RunId DESC;", cn);
        cmd.Parameters.AddWithValue("@n", limit);
        using SqlDataReader rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct)) list.Add(MapRun(rd));
        return list;
    }

    /// <summary>سند حسابرسی: هش مجموعه، تعهد، Seed افشاشده، برنده و شمارش صلاحیت‌ها.</summary>
    public async Task<object> ReceiptAsync(int runId, CancellationToken ct = default)
    {
        await using SqlConnection cn = await _db.OpenAsync(ct);
        LotteryRunInfo run = await ReadRunAsync(cn, runId, true, ct)
            ?? throw new ApiException(404, "run_not_found", "فراگرفت قرعه‌کشی پیدا نشد.");

        Dictionary<string, int> histogram = new();
        foreach (LotteryEntryInfo e in run.Entries ?? new List<LotteryEntryInfo>())
            histogram[e.Eligibility] = histogram.TryGetValue(e.Eligibility, out int c) ? c + 1 : 1;

        return new
        {
            run,
            histogram,
            algorithm = LotteryEngine.Algorithm,
            verification = new
            {
                commitmentFormula = "sha256(algorithm|round=<RoundNo>|entries=<EntriesHash>|seed=<SeedHex>)",
                selection = "draw = uniform[0, TotalWeight); برنده = اولین ورودی واجد شرایط که مجموع تجمعی وزن از draw بگذرد",
                stream = "HMAC-SHA256(seed, algorithm|round=<RoundNo>|entries=<EntriesHash>|counter) با counter از ۰",
                steps = new[]
                {
                    "۱) EntriesHash را با مسیر /verify از Snapshot بازسازی کنید.",
                    "۲) CommitmentHash را از SeedHex و EntriesHash بازمحاسبه و مقایسه کنید.",
                    "۳) با همان SeedHex برنده را بازتولید کنید.",
                    "۴) برندهٔ بازتولیدشده باید با WinnerFCode ثبت‌شده یکسان باشد."
                },
                limits = "این سند، بازتولیدپذیری نتیجه از داده‌های تثبیت‌شده را اثبات می‌کند؛ " +
                         "مستقل‌بودن Seed از تصمیم مدیر را فقط ممیزی خارجی/مهر زمانی بیرونی اثبات می‌کند."
            }
        };
    }

    /// <summary>راستی‌آزمایی: بازسازی هش مجموعه، تعهد و برنده از داده‌های فعلی پایگاه داده.</summary>
    public async Task<LotteryVerifyResult> VerifyAsync(int runId, CancellationToken ct = default)
    {
        await using SqlConnection cn = await _db.OpenAsync(ct);
        LotteryRunInfo run = await ReadRunAsync(cn, runId, true, ct)
            ?? throw new ApiException(404, "run_not_found", "فراگرفت قرعه‌کشی پیدا نشد.");

        // بازسازی هش داخلی باید با دادهٔ کامل Snapshot انجام شود؛ شمارهٔ موبایل نقشی در هش دارد
        // و در پاسخ API پوشانده می‌شود، بنابراین اینجا مستقیم از پایگاه داده خوانده می‌شود.
        List<RawEntry> raw = await ReadRawEntriesAsync(cn, runId, ct);
        List<string> lines = raw
            .OrderBy(x => x.EntryNo)
            .Select(x => LotteryEngine.EntryLine(x.EntryNo, x.InvoiceId, x.FCode, x.Mobile, x.Points,
                x.Eligibility, x.BaleStatus))
            .ToList();
        bool entriesMatch = string.Equals(LotteryEngine.EntriesHash(lines), run.EntriesHash, StringComparison.Ordinal);

        bool commitmentMatch = false;
        bool winnerReproduces = false;
        bool publicHashMatch = false;
        long? recomputedFCode = null;
        int? recomputedEntry = null;
        string? publicHash = run.PublicEntriesHash;

        if (!string.IsNullOrEmpty(run.SeedHex))
        {
            // هش عمومی از همان ورودی‌ها با شناسهٔ مستعار بازسازی می‌شود؛ پس از افشای Seed
            // هر ممیز بیرونی هم می‌تواند همین کار را با دادهٔ عمومی انجام دهد.
            string recomputedPublicHash = PublicEntriesHashOf(seedHex: run.SeedHex, raw);
            if (string.IsNullOrEmpty(publicHash)) publicHash = recomputedPublicHash;
            publicHashMatch = string.Equals(recomputedPublicHash, publicHash, StringComparison.Ordinal);

            commitmentMatch = string.Equals(
                LotteryEngine.Commitment(run.RoundNo, publicHash, run.SeedHex), run.CommitmentHash,
                StringComparison.Ordinal);

            List<WeightedEntry> eligible = raw
                .Where(x => x.Eligibility == "eligible" && x.Weight > 0)
                .OrderBy(x => x.EntryNo)
                .Select(x => new WeightedEntry
                {
                    EntryNo = x.EntryNo, InvoiceId = x.InvoiceId, FCode = x.FCode, Points = x.Points,
                    Weight = x.Weight, Mobile = x.Mobile, CustomerCode = x.CustomerCode
                })
                .ToList();

            if (eligible.Count > 0 && eligible.Sum(x => x.Weight) > 0)
            {
                byte[] seed = Convert.FromHexString(run.SeedHex);
                string context = LotteryEngine.Context(run.RoundNo, publicHash!);
                int index = LotteryEngine.PickWeighted(seed, context, eligible.Select(x => x.Weight).ToList(),
                    out _, out _);
                recomputedFCode = eligible[index].FCode;
                recomputedEntry = eligible[index].EntryNo;
                winnerReproduces = run.Status != "Selected" && run.Status != "Finalized"
                    ? true
                    : run.WinnerFCode == recomputedFCode && run.WinnerEntryNo == recomputedEntry;
            }
        }

        return new LotteryVerifyResult
        {
            RunId = runId,
            Algorithm = LotteryEngine.Algorithm,
            SeedRevealed = run.Status == "Selected" || run.Status == "Finalized",
            EntriesHashMatches = entriesMatch,
            PublicEntriesHash = publicHash,
            PublicEntriesHashMatches = publicHashMatch,
            CommitmentMatches = commitmentMatch,
            WinnerReproduces = winnerReproduces,
            ExpectedWinnerFCode = run.WinnerFCode,
            RecomputedWinnerFCode = recomputedFCode,
            RecomputedEntryNo = recomputedEntry,
            Note = publicHashMatch
                ? "هش عمومی، تعهد و برنده از دادهٔ تثبیت‌شده بازتولید شدند."
                : (string.IsNullOrEmpty(run.SeedHex)
                    ? "تا افشای Seed پس از انتخاب، بازتولید هش عمومی ممکن نیست."
                    : "اگر تعهد یا هش مطابقت نداشت، مجموعه یا Seed پس از تثبیت تغییر کرده است و نتیجه بی‌اعتبار می‌شود.")
        };
    }

    // ------------------------------------------------------------------
    //  انتشار عمومی نتیجه (فقط با تأیید صریح مدیر)
    // ------------------------------------------------------------------
    /// <summary>
    /// تأیید انتشار عمومی نتیجه: تا این لحظه هیچ خروجی عمومی برنده را نشان نمی‌دهد.
    /// نام نمایشی، تصویر و وضعیت تأیید تصویر همراه همین اقدام ثبت می‌شوند.
    /// </summary>
    public async Task<LotteryRunInfo> PublishAsync(int runId, string displayName, string? imageUrl,
        bool imageApproved, string actor, CancellationToken ct = default)
    {
        actor = CleanActor(actor);
        displayName = Trim(displayName, 60);
        string? image = ValidateImageUrl(imageUrl);

        await using SqlConnection cn = await _db.OpenAsync(ct);
        await using SqlTransaction tx = (SqlTransaction)await cn.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        try
        {
            LotteryRunInfo run = await ReadRunAsync(cn, tx, runId, false, ct)
                ?? throw new ApiException(404, "run_not_found", "فراگرفت قرعه‌کشی پیدا نشد.");
            if (run.Status != "Finalized")
                throw new ApiException(409, "run_not_finalized",
                    "فقط نتیجهٔ نهایی‌شده قابل انتشار عمومی است.");
            if (run.WinnerFCode == null)
                throw new ApiException(409, "no_winner", "این فراگرفت برندهٔ ثبت‌شده ندارد.");

            using (SqlCommand upd = new(@"
UPDATE dbo.LotteryRuns SET PublishApproved=1, PublishApprovedBy=@actor, PublishApprovedAt=SYSUTCDATETIME(),
    PublishedDisplayName=@name,
    WinnerImageUrl = CASE WHEN @hasImg = 1 THEN @img ELSE WinnerImageUrl END,
    WinnerImageApproved = CASE WHEN @hasImg = 1 THEN @imgOk ELSE WinnerImageApproved END,
    WinnerImageUpdatedAt = CASE WHEN @hasImg = 1 THEN SYSUTCDATETIME() ELSE WinnerImageUpdatedAt END
WHERE RunId=@id;", cn, tx))
            {
                upd.Parameters.AddWithValue("@actor", actor);
                upd.Parameters.AddWithValue("@name", displayName.Length == 0 ? (object)DBNull.Value : displayName);
                upd.Parameters.AddWithValue("@hasImg", image != null ? 1 : 0);
                upd.Parameters.AddWithValue("@img", (object?)image ?? DBNull.Value);
                upd.Parameters.AddWithValue("@imgOk", image != null && imageApproved ? 1 : 0);
                upd.Parameters.AddWithValue("@id", runId);
                await upd.ExecuteNonQueryAsync(ct);
            }

            await AuditWriter.WriteAsync(cn, tx, actor, "lottery.publish", "LotteryRun", runId.ToString(),
                new { runId, roundId = run.RoundId, displayName, imageUrl = image, imageApproved = image != null && imageApproved },
                ct);
            await tx.CommitAsync(ct);

            _log.LogInformation("lottery publish run={RunId} actor={Actor} image={Image}", runId, actor, image != null);
            await using SqlConnection rcn = await _db.OpenAsync(ct);
            return (await ReadRunAsync(rcn, runId, false, ct))!;
        }
        catch (ApiException)
        {
            try { await tx.RollbackAsync(ct); } catch { }
            throw;
        }
        catch
        {
            try { await tx.RollbackAsync(ct); } catch { }
            throw;
        }
    }

    /// <summary>لغو انتشار عمومی؛ نتیجه در سرویس مرکزی حفظ می‌شود ولی دیگر در سایت دیده نمی‌شود.</summary>
    public async Task<LotteryRunInfo> UnpublishAsync(int runId, string reason, string actor,
        CancellationToken ct = default)
    {
        actor = CleanActor(actor);
        reason = Trim(reason, 200);
        if (reason.Length == 0)
            throw new ApiException(400, "reason_required", "دلیل لغو انتشار الزامی است.");

        await using SqlConnection cn = await _db.OpenAsync(ct);
        await using SqlTransaction tx = (SqlTransaction)await cn.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        try
        {
            LotteryRunInfo run = await ReadRunAsync(cn, tx, runId, false, ct)
                ?? throw new ApiException(404, "run_not_found", "فراگرفت قرعه‌کشی پیدا نشد.");

            using (SqlCommand upd = new(@"
UPDATE dbo.LotteryRuns SET PublishApproved=0, PublishApprovedBy=@actor, PublishApprovedAt=SYSUTCDATETIME()
WHERE RunId=@id;", cn, tx))
            {
                upd.Parameters.AddWithValue("@actor", actor);
                upd.Parameters.AddWithValue("@id", runId);
                await upd.ExecuteNonQueryAsync(ct);
            }

            await AuditWriter.WriteAsync(cn, tx, actor, "lottery.unpublish", "LotteryRun", runId.ToString(),
                new { runId, roundId = run.RoundId, reason }, ct);
            await tx.CommitAsync(ct);
            await using SqlConnection rcn = await _db.OpenAsync(ct);
            return (await ReadRunAsync(rcn, runId, false, ct))!;
        }
        catch (ApiException)
        {
            try { await tx.RollbackAsync(ct); } catch { }
            throw;
        }
        catch
        {
            try { await tx.RollbackAsync(ct); } catch { }
            throw;
        }
    }

    /// <summary>ثبت/جایگزینی/حذف تصویر برنده و وضعیت تأیید انتشار آن.</summary>
    public async Task<LotteryRunInfo> SetWinnerImageAsync(int runId, string? imageUrl, bool approved,
        string actor, CancellationToken ct = default)
    {
        actor = CleanActor(actor);
        string? image = ValidateImageUrl(imageUrl);
        if (image != null && !approved)
            throw new ApiException(400, "image_consent_required",
                "انتشار تصویر برنده نیازمند تأیید صریح (approved=true) است.");

        await using SqlConnection cn = await _db.OpenAsync(ct);
        await using SqlTransaction tx = (SqlTransaction)await cn.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        try
        {
            LotteryRunInfo run = await ReadRunAsync(cn, tx, runId, false, ct)
                ?? throw new ApiException(404, "run_not_found", "فراگرفت قرعه‌کشی پیدا نشد.");
            if (run.WinnerFCode == null)
                throw new ApiException(409, "no_winner", "این فراگرفت برندهٔ ثبت‌شده ندارد.");

            using (SqlCommand upd = new(@"
UPDATE dbo.LotteryRuns SET WinnerImageUrl=@img, WinnerImageApproved=@ok, WinnerImageUpdatedAt=SYSUTCDATETIME()
WHERE RunId=@id;", cn, tx))
            {
                upd.Parameters.AddWithValue("@img", (object?)image ?? DBNull.Value);
                upd.Parameters.AddWithValue("@ok", image != null && approved ? 1 : 0);
                upd.Parameters.AddWithValue("@id", runId);
                await upd.ExecuteNonQueryAsync(ct);
            }

            await AuditWriter.WriteAsync(cn, tx, actor,
                image == null ? "lottery.image.removed" : "lottery.image.set", "LotteryRun", runId.ToString(),
                new { runId, imageUrl = image, approved = image != null && approved }, ct);
            await tx.CommitAsync(ct);
            await using SqlConnection rcn = await _db.OpenAsync(ct);
            return (await ReadRunAsync(rcn, runId, false, ct))!;
        }
        catch (ApiException)
        {
            try { await tx.RollbackAsync(ct); } catch { }
            throw;
        }
        catch
        {
            try { await tx.RollbackAsync(ct); } catch { }
            throw;
        }
    }

    /// <summary>تعداد نتایج منتشرشده؛ برای صفحه‌بندی فهرست عمومی برندگان.</summary>
    public async Task<int> CountPublishedAsync(int? roundNo, CancellationToken ct = default)
    {
        await using SqlConnection cn = await _db.OpenAsync(ct);
        using SqlCommand cmd = new(@"
SELECT COUNT(*) FROM dbo.LotteryRuns r INNER JOIN dbo.Rounds ro ON ro.RoundId = r.RoundId
WHERE r.PublishApproved=1 AND r.Status='Finalized' AND (@roundNo IS NULL OR ro.RoundNo=@roundNo);", cn);
        cmd.Parameters.AddWithValue("@roundNo", (object?)roundNo ?? DBNull.Value);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
    }

    /// <summary>برندگان منتشرشده برای صفحهٔ عمومی (فقط تأییدشده و نهایی).</summary>
    public async Task<List<LotteryRunInfo>> ListPublishedAsync(int limit, int offset, int? roundNo,
        CancellationToken ct = default)
    {
        List<LotteryRunInfo> list = new();
        await using SqlConnection cn = await _db.OpenAsync(ct);
        using SqlCommand cmd = new($@"
SELECT {RunColumns} FROM dbo.LotteryRuns r INNER JOIN dbo.Rounds ro ON ro.RoundId = r.RoundId
WHERE r.PublishApproved=1 AND r.Status='Finalized' AND (@roundNo IS NULL OR ro.RoundNo=@roundNo)
ORDER BY ro.RoundNo DESC
OFFSET @offset ROWS FETCH NEXT @limit ROWS ONLY;", cn);
        cmd.Parameters.AddWithValue("@limit", limit);
        cmd.Parameters.AddWithValue("@offset", offset);
        cmd.Parameters.AddWithValue("@roundNo", (object?)roundNo ?? DBNull.Value);
        using SqlDataReader rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct)) list.Add(MapRun(rd));
        return list;
    }

    /// <summary>یک برندهٔ منتشرشده بر اساس شمارهٔ دوره.</summary>
    public async Task<LotteryRunInfo?> GetPublishedByRoundAsync(int roundNo, CancellationToken ct = default)
    {
        await using SqlConnection cn = await _db.OpenAsync(ct);
        using SqlCommand cmd = new($@"
SELECT {RunColumns} FROM dbo.LotteryRuns r INNER JOIN dbo.Rounds ro ON ro.RoundId = r.RoundId
WHERE r.PublishApproved=1 AND r.Status='Finalized' AND ro.RoundNo=@no;", cn);
        cmd.Parameters.AddWithValue("@no", roundNo);
        using SqlDataReader rd = await cmd.ExecuteReaderAsync(ct);
        return await rd.ReadAsync(ct) ? MapRun(rd) : null;
    }

    /// <summary>آخرین نتیجهٔ منتشرشده برای نمایش در صفحهٔ کمپین.</summary>
    public async Task<LotteryRunInfo?> GetLatestPublishedAsync(CancellationToken ct = default)
    {
        await using SqlConnection cn = await _db.OpenAsync(ct);
        using SqlCommand cmd = new($@"
SELECT TOP 1 {RunColumns} FROM dbo.LotteryRuns r INNER JOIN dbo.Rounds ro ON ro.RoundId = r.RoundId
WHERE r.PublishApproved=1 AND r.Status='Finalized' ORDER BY ro.RoundNo DESC;", cn);
        using SqlDataReader rd = await cmd.ExecuteReaderAsync(ct);
        return await rd.ReadAsync(ct) ? MapRun(rd) : null;
    }

    /// <summary>فراگرفت فعال یک دوره (برای نمایش وضعیت «نیازمند بررسی» در صفحهٔ کمپین).</summary>
    public async Task<LotteryRunInfo?> GetActiveRunOfRoundAsync(int roundId, CancellationToken ct = default)
    {
        await using SqlConnection cn = await _db.OpenAsync(ct);
        return await ReadRunByRoundAsync(cn, roundId, false, ct);
    }

    /// <summary>
    /// فهرست عمومی ورودی‌های یک فراگرفت: شناسهٔ مستعار، صلاحیت و وزن. بدون موبایل،
    /// بدون شمارهٔ فاکتور، بدون شناسهٔ مشتری و بدون وضعیت بله.
    /// </summary>
    public async Task<List<PublicEntry>> ListPublicEntriesAsync(int runId, int limit, int offset,
        CancellationToken ct = default)
    {
        List<PublicEntry> list = new();
        await using SqlConnection cn = await _db.OpenAsync(ct);
        LotteryRunInfo run = await ReadRunAsync(cn, runId, false, ct)
            ?? throw new ApiException(404, "run_not_found", "فراگرفت قرعه‌کشی پیدا نشد.");
        if (string.IsNullOrEmpty(run.SeedHex))
            throw new ApiException(409, "seed_not_revealed",
                "فهرست ورودی‌ها پیش از انتخاب برنده منتشر نمی‌شود.");

        using SqlCommand cmd = new(@"
SELECT EntryNo, Eligibility, Weight FROM dbo.LotteryEntries
WHERE RunId=@id ORDER BY EntryNo OFFSET @offset ROWS FETCH NEXT @limit ROWS ONLY;", cn);
        cmd.Parameters.AddWithValue("@id", runId);
        cmd.Parameters.AddWithValue("@limit", limit);
        cmd.Parameters.AddWithValue("@offset", offset);
        using SqlDataReader rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
        {
            int entryNo = rd.GetInt32(0);
            list.Add(new PublicEntry
            {
                EntryNo = entryNo,
                Pseudonym = LotteryEngine.Pseudonym(run.SeedHex, entryNo),
                Eligibility = rd.GetString(1),
                Weight = rd.GetInt64(2),
                Winner = run.WinnerEntryNo == entryNo
            });
        }
        return list;
    }

    // ------------------------------------------------------------------
    private static string PublicEntriesHashOf(string seedHex, List<RawEntry> raw)
        => LotteryEngine.PublicEntriesHash(raw.OrderBy(x => x.EntryNo).Select(x =>
            LotteryEngine.PublicEntryLine(x.EntryNo, LotteryEngine.Pseudonym(seedHex, x.EntryNo),
                x.Eligibility, x.Weight)));

    /// <summary>هش عمومی ذخیره‌شده؛ برای فراگرافت‌های قدیمی از هش داخلی جدا نگه داشته می‌شود.</summary>
    private static string PublicHashOf(LotteryRunInfo run)
        => string.IsNullOrEmpty(run.PublicEntriesHash) ? run.EntriesHash : run.PublicEntriesHash;

    /// <summary>اعتبارسنجی آدرس تصویر برنده: فقط http/https با پسوند تصویر مجاز.</summary>
    private static string? ValidateImageUrl(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        string s = raw.Trim();
        if (s.Length > 400) throw new ApiException(400, "image_url_invalid", "آدرس تصویر بیش از حد بلند است.");
        if (!Uri.TryCreate(s, UriKind.Absolute, out Uri? uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            throw new ApiException(400, "image_url_invalid", "آدرس تصویر باید http یا https باشد.");
        string path = uri.AbsolutePath.ToLowerInvariant();
        if (!(path.EndsWith(".jpg") || path.EndsWith(".jpeg") || path.EndsWith(".png") || path.EndsWith(".webp")))
            throw new ApiException(400, "image_url_invalid", "پسوند تصویر باید jpg، jpeg، png یا webp باشد.");
        return s;
    }

    /// <summary>
    /// شمارهٔ کامل برندهٔ نهایی‌شده برای تماس و تحویل جایزه.
    /// هر دسترسی در AuditLog ثبت می‌شود (بند ۵.۲: اطلاعات مجاز مالک برنده).
    /// </summary>
    public async Task<object> WinnerContactAsync(int runId, string actor, CancellationToken ct = default)
    {
        actor = CleanActor(actor);
        await using SqlConnection cn = await _db.OpenAsync(ct);
        await using SqlTransaction tx = (SqlTransaction)await cn.BeginTransactionAsync(ct);
        try
        {
            LotteryRunInfo run = await ReadRunAsync(cn, tx, runId, false, ct)
                ?? throw new ApiException(404, "run_not_found", "فراگرفت قرعه‌کشی پیدا نشد.");
            if (run.Status != "Selected" && run.Status != "Finalized")
                throw new ApiException(409, "no_winner", "برای این فراگرفت برنده‌ای انتخاب نشده است.");
            if (string.IsNullOrEmpty(run.WinnerMobileFull))
                throw new ApiException(409, "winner_mobile_missing", "شمارهٔ تماس برنده ثبت نشده است.");

            await AuditWriter.WriteAsync(cn, tx, actor, "lottery.winner_contact", "LotteryRun", runId.ToString(),
                new { runId, roundId = run.RoundId, winnerFCode = run.WinnerFCode }, ct);
            await tx.CommitAsync(ct);

            return new
            {
                runId, run.RoundId, run.RoundNo, run.Status,
                winnerFCode = run.WinnerFCode, winnerEntryNo = run.WinnerEntryNo,
                winnerPoints = run.WinnerPoints, winnerCustomerCode = run.WinnerCustomerCode,
                winnerMobile = run.WinnerMobileFull
            };
        }
        catch (ApiException)
        {
            try { await tx.RollbackAsync(ct); } catch { }
            throw;
        }
        catch
        {
            try { await tx.RollbackAsync(ct); } catch { }
            throw;
        }
    }

    // ------------------------------------------------------------------
    //  دسته‌بندی صلاحیت
    // ------------------------------------------------------------------
    private string Classify(EntryDraft d, out long weight)
    {
        weight = 0;
        if (d.Mobile.Length == 0) return "no_mobile";
        if (d.Points <= 0) return "zero_weight"; // امتیاز صفر نباید به شانس مثبت تبدیل شود
        if (_opt.RequireBaleMembership)
        {
            string code = d.Bale?.Code ?? "unknown";
            if (code == "not_member") return "bale_not_member";
            if (code != "member") return "bale_unknown";
        }
        weight = d.Points;
        return "eligible";
    }

    // ------------------------------------------------------------------
    //  خواندن/نوشتن
    // ------------------------------------------------------------------
    private const string RunColumns = @"r.RunId, r.RoundId, ro.RoundNo, r.Status, r.Algorithm, r.EntriesHash,
    r.EntryCount, r.EligibleCount, r.TotalWeight, r.CommitmentHash, r.SeedHex, r.AcknowledgedReviews,
    r.WinnerInvoiceId, r.WinnerFCode, r.WinnerEntryNo, r.WinnerCustomerCode, r.WinnerMobile, r.WinnerPoints,
    r.WinnerDrawValue, r.WinnerTotalWeight, r.ReviewReason, r.ResultsQuestioned, r.PreparedBy, r.PreparedAt,
    r.SelectedAt, r.FinalizedBy, r.FinalizedAt, r.PublicEntriesHash, r.PublishApproved, r.PublishApprovedBy,
    r.PublishApprovedAt, r.PublishedDisplayName, r.WinnerImageUrl, r.WinnerImageApproved, r.WinnerImageUpdatedAt";

    private static LotteryRunInfo MapRun(SqlDataReader r)
    {
        string status = r.GetString(3);
        bool revealed = status == "Selected" || status == "Finalized";
        string? winnerMobile = r.IsDBNull(16) ? null : r.GetString(16);
        return new LotteryRunInfo
        {
        RunId = r.GetInt32(0),
        RoundId = r.GetInt32(1),
        RoundNo = r.GetInt32(2),
        Status = status,
        Algorithm = r.GetString(4),
        EntriesHash = r.GetString(5).Trim(),
        EntryCount = r.GetInt32(6),
        EligibleCount = r.GetInt32(7),
        TotalWeight = r.GetInt64(8),
        CommitmentHash = r.GetString(9).Trim(),
        // Seed فقط پس از انتخاب افشا می‌شود؛ پیش از آن فقط تعهد منتشر می‌شود.
        SeedHex = revealed && !r.IsDBNull(10) ? r.GetString(10).Trim() : null,
        FullSeedHex = r.IsDBNull(10) ? null : r.GetString(10).Trim(),
        AcknowledgedReviews = r.GetInt32(11),
        WinnerInvoiceId = r.IsDBNull(12) ? null : r.GetInt64(12),
        WinnerFCode = r.IsDBNull(13) ? null : r.GetInt64(13),
        WinnerEntryNo = r.IsDBNull(14) ? null : r.GetInt32(14),
        WinnerCustomerCode = r.IsDBNull(15) ? null : r.GetString(15),
        WinnerMobile = Security.MaskMobile(winnerMobile),
        WinnerMobileFull = winnerMobile,
        WinnerPoints = r.IsDBNull(17) ? null : r.GetInt32(17),
        WinnerDrawValue = r.IsDBNull(18) ? null : r.GetInt64(18),
        WinnerTotalWeight = r.IsDBNull(19) ? null : r.GetInt64(19),
        ReviewReason = r.IsDBNull(20) ? null : r.GetString(20),
        ResultsQuestioned = r.GetBoolean(21),
        PreparedBy = r.GetString(22),
        PreparedAt = r.GetDateTime(23),
        SelectedAt = r.IsDBNull(24) ? null : r.GetDateTime(24),
        FinalizedBy = r.IsDBNull(25) ? null : r.GetString(25),
        FinalizedAt = r.IsDBNull(26) ? null : r.GetDateTime(26),
        PublicEntriesHash = r.IsDBNull(27) ? null : r.GetString(27).Trim(),
        PublishApproved = r.GetBoolean(28),
        PublishApprovedBy = r.IsDBNull(29) ? null : r.GetString(29),
        PublishApprovedAt = r.IsDBNull(30) ? null : r.GetDateTime(30),
        PublishedDisplayName = r.IsDBNull(31) ? null : r.GetString(31),
        WinnerImageUrl = r.IsDBNull(32) ? null : r.GetString(32),
        WinnerImageApproved = r.GetBoolean(33),
        WinnerImageUpdatedAt = r.IsDBNull(34) ? null : r.GetDateTime(34)
        };
    }

    private static async Task<LotteryRunInfo?> ReadRunAsync(SqlConnection cn, int runId, bool withEntries,
        CancellationToken ct)
        => await ReadRunAsync(cn, null, runId, withEntries, ct);

    /// <summary>
    /// وقتی اتصال تراکنش باز دارد، خواندن هم باید با همان تراکنش انجام شود؛
    /// وگرنه SqlClient خطای «pending local transaction» می‌دهد.
    /// </summary>
    private static async Task<LotteryRunInfo?> ReadRunAsync(SqlConnection cn, SqlTransaction? tx, int runId,
        bool withEntries, CancellationToken ct)
    {
        LotteryRunInfo? run;
        using (SqlCommand cmd = new($@"
SELECT {RunColumns} FROM dbo.LotteryRuns r INNER JOIN dbo.Rounds ro ON ro.RoundId = r.RoundId
WHERE r.RunId=@id;", cn, tx))
        {
            cmd.Parameters.AddWithValue("@id", runId);
            using SqlDataReader r = await cmd.ExecuteReaderAsync(ct);
            run = await r.ReadAsync(ct) ? MapRun(r) : null;
        }
        if (run == null) return null;
        if (withEntries) run.Entries = await ReadEntriesAsync(cn, tx, runId, ct);
        return run;
    }

    private static async Task<LotteryRunInfo?> ReadRunByRoundAsync(SqlConnection cn, int roundId, bool withEntries,
        CancellationToken ct)
    {
        LotteryRunInfo? run;
        using (SqlCommand cmd = new($@"
SELECT {RunColumns} FROM dbo.LotteryRuns r INNER JOIN dbo.Rounds ro ON ro.RoundId = r.RoundId
WHERE r.RoundId=@id AND r.Status <> 'Aborted' ORDER BY r.RunId DESC;", cn))
        {
            cmd.Parameters.AddWithValue("@id", roundId);
            using SqlDataReader r = await cmd.ExecuteReaderAsync(ct);
            run = await r.ReadAsync(ct) ? MapRun(r) : null;
        }
        if (run == null) return null;
        if (withEntries) run.Entries = await ReadEntriesAsync(cn, null, run.RunId, ct);
        return run;
    }

    /// <summary>فراگرفت را برای تغییر قفل می‌کند (بند ۵.۱ گام ۲).</summary>
    private static async Task<LotteryRunInfo?> LockRunAsync(SqlConnection cn, SqlTransaction tx, int runId,
        CancellationToken ct)
    {
        LotteryRunInfo? run;
        using (SqlCommand cmd = new($@"
SELECT {RunColumns} FROM dbo.LotteryRuns r WITH (UPDLOCK, HOLDLOCK)
INNER JOIN dbo.Rounds ro ON ro.RoundId = r.RoundId WHERE r.RunId=@id;", cn, tx))
        {
            cmd.Parameters.AddWithValue("@id", runId);
            using SqlDataReader r = await cmd.ExecuteReaderAsync(ct);
            run = await r.ReadAsync(ct) ? MapRun(r) : null;
        }
        if (run == null) return null;
        run.Entries = await ReadEntriesAsync(cn, tx, runId, ct);
        return run;
    }

    private static async Task<List<WeightedEntry>> ReadEligibleAsync(SqlConnection cn, SqlTransaction tx, int runId,
        CancellationToken ct)
    {
        List<WeightedEntry> list = new();
        using SqlCommand cmd = new(@"
SELECT EntryNo, InvoiceId, FCode, CustomerCode, Mobile, Points, Weight
FROM dbo.LotteryEntries WITH (UPDLOCK, HOLDLOCK)
WHERE RunId=@id AND Eligibility='eligible' AND Weight > 0 ORDER BY EntryNo;", cn, tx);
        cmd.Parameters.AddWithValue("@id", runId);
        using SqlDataReader r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new WeightedEntry
            {
                EntryNo = r.GetInt32(0),
                InvoiceId = r.GetInt64(1),
                FCode = r.GetInt64(2),
                CustomerCode = r.IsDBNull(3) ? null : r.GetString(3),
                Mobile = r.IsDBNull(4) ? null : r.GetString(4),
                Points = r.GetInt32(5),
                Weight = r.GetInt64(6)
            });
        return list;
    }

    private sealed class RawEntry
    {
        public int EntryNo;
        public long InvoiceId;
        public long FCode;
        public string Mobile = "";
        public string? CustomerCode;
        public int Points;
        public long Weight;
        public string Eligibility = "";
        public string? BaleStatus;
    }

    /// <summary>خواندن دادهٔ کامل Snapshot برای راستی‌آزمایی (بدون پوشاندن شمارهٔ موبایل).</summary>
    private static async Task<List<RawEntry>> ReadRawEntriesAsync(SqlConnection cn, int runId, CancellationToken ct)
    {
        List<RawEntry> list = new();
        using SqlCommand cmd = new(@"
SELECT EntryNo, InvoiceId, FCode, Mobile, CustomerCode, Points, Weight, Eligibility, BaleStatus
FROM dbo.LotteryEntries WHERE RunId=@id ORDER BY EntryNo;", cn);
        cmd.Parameters.AddWithValue("@id", runId);
        using SqlDataReader r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new RawEntry
            {
                EntryNo = r.GetInt32(0),
                InvoiceId = r.GetInt64(1),
                FCode = r.GetInt64(2),
                Mobile = r.IsDBNull(3) ? "" : r.GetString(3),
                CustomerCode = r.IsDBNull(4) ? null : r.GetString(4),
                Points = r.GetInt32(5),
                Weight = r.GetInt64(6),
                Eligibility = r.GetString(7),
                BaleStatus = r.IsDBNull(8) ? null : r.GetString(8)
            });
        return list;
    }

    private static async Task<List<LotteryEntryInfo>> ReadEntriesAsync(SqlConnection cn, SqlTransaction? tx, int runId,
        CancellationToken ct)
    {
        List<LotteryEntryInfo> list = new();
        using SqlCommand cmd = new(@"
SELECT EntryNo, InvoiceId, FCode, FDate, Mobile, Points, Weight, Eligibility, BaleStatus, BaleEvidence, BaleCheckedAt
FROM dbo.LotteryEntries WHERE RunId=@id ORDER BY EntryNo;", cn, tx);
        cmd.Parameters.AddWithValue("@id", runId);
        using SqlDataReader r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new LotteryEntryInfo
            {
                EntryNo = r.GetInt32(0),
                InvoiceId = r.GetInt64(1),
                FCode = r.GetInt64(2),
                FDate = DateOnly.FromDateTime(r.GetDateTime(3)),
                Mobile = r.IsDBNull(4) ? null : Security.MaskMobile(r.GetString(4)),
                Points = r.GetInt32(5),
                Weight = r.GetInt64(6),
                Eligibility = r.GetString(7),
                BaleStatus = r.IsDBNull(8) ? null : r.GetString(8),
                BaleEvidence = r.IsDBNull(9) ? null : r.GetString(9),
                BaleCheckedAt = r.IsDBNull(10) ? null : r.GetDateTime(10)
            });
        return list;
    }

    private static async Task InsertEntriesAsync(SqlConnection cn, SqlTransaction tx, int runId,
        List<EntryDraft> drafts, CancellationToken ct)
    {
        const int batch = 100;
        for (int start = 0; start < drafts.Count; start += batch)
        {
            List<EntryDraft> slice = drafts.GetRange(start, Math.Min(batch, drafts.Count - start));
            System.Text.StringBuilder sql = new(@"
INSERT INTO dbo.LotteryEntries(RunId, EntryNo, InvoiceId, FCode, FDate, CustomerCode, Mobile, Points, Weight,
    Gross, Eligibility, BaleStatus, BaleEvidence, BaleCheckedAt)
VALUES");
            using SqlCommand cmd = new(sql.ToString(), cn, tx);
            cmd.Parameters.AddWithValue("@runId", runId);
            for (int i = 0; i < slice.Count; i++)
            {
                EntryDraft d = slice[i];
                sql.Append(i == 0 ? "" : ",");
                sql.Append($"(@runId, @e{i}, @inv{i}, @fc{i}, @fd{i}, @cc{i}, @m{i}, @p{i}, @w{i}, @g{i}, @el{i}, @bs{i}, @be{i}, @bc{i})");
                cmd.Parameters.AddWithValue($"@e{i}", d.EntryNo);
                cmd.Parameters.AddWithValue($"@inv{i}", d.InvoiceId);
                cmd.Parameters.AddWithValue($"@fc{i}", d.FCode);
                cmd.Parameters.Add($"@fd{i}", SqlDbType.Date).Value = d.FDate.ToDateTime(TimeOnly.MinValue);
                cmd.Parameters.AddWithValue($"@cc{i}", (object?)d.CustomerCode ?? DBNull.Value);
                cmd.Parameters.AddWithValue($"@m{i}", d.Mobile.Length == 0 ? (object)DBNull.Value : d.Mobile);
                cmd.Parameters.AddWithValue($"@p{i}", d.Points);
                cmd.Parameters.AddWithValue($"@w{i}", d.Weight);
                cmd.Parameters.Add($"@g{i}", SqlDbType.Decimal).Value = d.Gross;
                cmd.Parameters[$"@g{i}"].Precision = 18;
                cmd.Parameters[$"@g{i}"].Scale = 2;
                cmd.Parameters.AddWithValue($"@el{i}", d.Eligibility);
                cmd.Parameters.AddWithValue($"@bs{i}", (object?)d.Bale?.Code ?? DBNull.Value);
                cmd.Parameters.AddWithValue($"@be{i}", (object?)Trim(d.Bale?.Evidence, 200) ?? DBNull.Value);
                cmd.Parameters.Add($"@bc{i}", SqlDbType.DateTime2).Value = (object?)d.Bale?.CheckedAt ?? DBNull.Value;
            }
            cmd.CommandText = sql.ToString();
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    private sealed class RoundSnapshot
    {
        public int RoundId; public int RoundNo; public string Status = ""; public int Count; public int Target;
    }

    private sealed class EntryDraft
    {
        public int EntryNo;
        public long InvoiceId;
        public long FCode;
        public DateOnly FDate;
        public string CustomerCode = "";
        public string Mobile = "";
        public int Points;
        public decimal Gross;
        public long Weight;
        public string Eligibility = "";
        public BaleCheck? Bale;
    }

    private sealed class WeightedEntry
    {
        public int EntryNo;
        public long InvoiceId;
        public long FCode;
        public string? CustomerCode;
        public string? Mobile;
        public int Points;
        public long Weight;
    }

    private static async Task<RoundSnapshot?> ReadRoundAsync(SqlConnection cn, int roundId, CancellationToken ct)
        => await ReadRoundAsync(cn, roundId, null, ct);

    private static async Task<RoundSnapshot?> ReadRoundAsync(SqlConnection cn, int roundId, SqlTransaction? tx,
        CancellationToken ct)
    {
        using SqlCommand cmd = new(@"
SELECT RoundId, RoundNo, Status, [Count], Target FROM dbo.Rounds WITH (UPDLOCK, HOLDLOCK) WHERE RoundId=@id;",
            cn, tx);
        cmd.Parameters.AddWithValue("@id", roundId);
        using SqlDataReader r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        return new RoundSnapshot
        {
            RoundId = r.GetInt32(0), RoundNo = r.GetInt32(1), Status = r.GetString(2),
            Count = r.GetInt32(3), Target = r.GetInt32(4)
        };
    }

    private static async Task<int> CountReviewsAsync(SqlConnection cn, int roundId, CancellationToken ct)
    {
        using SqlCommand cmd = new("SELECT COUNT(*) FROM dbo.Invoices WHERE RoundId=@id AND Status='NeedsReview';", cn);
        cmd.Parameters.AddWithValue("@id", roundId);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
    }

    /// <summary>
    /// فاکتورهای معتبر دوره را می‌خواند و هش پایداری مجموعه را برمی‌گرداند.
    /// این هش فقط برای کشف تغییر مجموعه بین دو فاز تثبیت است.
    /// </summary>
    private static async Task<(List<EntryDraft>, string)> ReadCandidatesAsync(SqlConnection cn, int roundId,
        CancellationToken ct)
        => await ReadCandidatesAsync(cn, roundId, null, ct);

    private static async Task<(List<EntryDraft>, string)> ReadCandidatesAsync(SqlConnection cn, int roundId,
        SqlTransaction? tx, CancellationToken ct)
    {
        List<EntryDraft> list = new();
        System.Text.StringBuilder stability = new();
        using SqlCommand cmd = new(@"
SELECT InvoiceId, FCode, FDate, CustomerCode, Mobile, Points, Gross, Status
FROM dbo.Invoices WITH (UPDLOCK, HOLDLOCK)
WHERE RoundId=@id AND Status='Valid' ORDER BY FDate, FCode, InvoiceId;", cn, tx);
        cmd.Parameters.AddWithValue("@id", roundId);
        using SqlDataReader r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            EntryDraft d = new()
            {
                InvoiceId = r.GetInt64(0),
                FCode = r.GetInt64(1),
                FDate = DateOnly.FromDateTime(r.GetDateTime(2)),
                CustomerCode = r.IsDBNull(3) ? "" : r.GetString(3),
                Mobile = Security.NormalizeMobile(r.IsDBNull(4) ? null : r.GetString(4)),
                Points = r.GetInt32(5),
                Gross = r.GetDecimal(6)
            };
            list.Add(d);
            stability.Append(d.InvoiceId).Append(':').Append(d.Points).Append(':').Append(d.Mobile).Append(';');
        }
        return (list, Security.Sha256Hex(stability.ToString()));
    }

    private static string CleanActor(string? actor)
    {
        string a = string.IsNullOrWhiteSpace(actor) ? "admin" : actor.Trim();
        return a.Length > 100 ? a[..100] : a;
    }

    private static string Trim(string? s, int max)
    {
        string t = (s ?? "").Trim();
        return t.Length > max ? t[..max] : t;
    }
}