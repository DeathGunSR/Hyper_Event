using System.Data;
using Microsoft.Data.SqlClient;

namespace EbiCampaign.Api;

/// <summary>
/// بند ۴.۲ — مسیر تأیید مجدد مدیریتی:
/// مشاهدهٔ فاکتور و شواهد، تصمیم «اعتبار» یا «ابطال»، ثبت هویت و زمان مدیر،
/// تاریخچهٔ تغییر وضعیت، اصلاح شمارندهٔ دورهٔ باز، جلوگیری از اجرای تکراری تصمیم،
/// و مسیر ویژهٔ حسابرسی‌شده برای دوره‌ای که نتیجهٔ قرعه‌کشی آن نهایی شده است.
/// </summary>
public sealed class ReviewDecisionService
{
    private readonly Db _db;
    private readonly ILogger<ReviewDecisionService> _log;

    public ReviewDecisionService(Db db, ILogger<ReviewDecisionService> log)
    {
        _db = db;
        _log = log;
    }

    public async Task<InvoiceDetail> GetInvoiceDetailAsync(long invoiceId, CancellationToken ct = default)
    {
        await using SqlConnection cn = await _db.OpenAsync(ct);
        InvoiceRow? invoice = await ReadInvoiceAsync(cn, null, invoiceId, ct);
        if (invoice == null) throw new ApiException(404, "invoice_not_found", "فاکتور پیدا نشد.");

        List<DecisionRow> decisions = new();
        using (SqlCommand cmd = new(@"
SELECT DecisionId, Verdict, Basis, PrevStatus, NewStatus, PrevRoundId, NewRoundId, CountBefore, CountAfter,
       CountEffected, RoundFinalized, Forced, Actor, Reason, Evidence, DecidedAt
FROM dbo.InvoiceDecisions WHERE InvoiceId=@id ORDER BY DecisionId;", cn))
        {
            cmd.Parameters.AddWithValue("@id", invoiceId);
            using SqlDataReader r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                decisions.Add(new DecisionRow
                {
                    DecisionId = r.GetInt64(0),
                    Verdict = r.GetString(1),
                    Basis = r.GetString(2),
                    PrevStatus = r.GetString(3),
                    NewStatus = r.GetString(4),
                    PrevRoundId = r.IsDBNull(5) ? null : r.GetInt32(5),
                    NewRoundId = r.IsDBNull(6) ? null : r.GetInt32(6),
                    CountBefore = r.IsDBNull(7) ? null : r.GetInt32(7),
                    CountAfter = r.IsDBNull(8) ? null : r.GetInt32(8),
                    CountEffected = r.GetBoolean(9),
                    RoundFinalized = r.GetBoolean(10),
                    Forced = r.GetBoolean(11),
                    Actor = r.GetString(12),
                    Reason = r.IsDBNull(13) ? null : r.GetString(13),
                    Evidence = r.IsDBNull(14) ? null : r.GetString(14),
                    DecidedAt = r.GetDateTime(15)
                });
        }

        (int? runId, bool finalized) = await ReadRunStateAsync(cn, null, invoice.RoundId, ct);
        return new InvoiceDetail
        {
            Invoice = invoice,
            Decisions = decisions,
            RoundFinalized = finalized,
            LotteryRunId = runId
        };
    }

    public async Task<DecisionResult> DecideAsync(long invoiceId, DecisionRequest req, string actor,
        CancellationToken ct = default)
    {
        actor = CleanActor(actor);
        string verdict = (req.Verdict ?? "").Trim().ToLowerInvariant();
        if (verdict != "valid" && verdict != "void")
            throw new ApiException(400, "verdict_invalid", "Verdict باید valid یا void باشد.");

        string reason = Trim(req.Reason, 400);
        string? decisionKey = string.IsNullOrWhiteSpace(req.DecisionKey) ? null : req.DecisionKey.Trim();
        if (decisionKey != null && decisionKey.Length > 80)
            throw new ApiException(400, "decision_key_invalid", "DecisionKey حداکثر ۸۰ کاراکتر است.");

        await using SqlConnection cn = await _db.OpenAsync(ct);
        await using SqlTransaction tx = (SqlTransaction)await cn.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        try
        {
            // ۱) ایدِمپوتنسی تصمیم: تکرار همان کلید، تصمیم را دوباره اجرا نمی‌کند.
            if (decisionKey != null)
            {
                using SqlCommand q = new(@"
SELECT InvoiceId, NewStatus, NewRoundId, CountAfter, CountEffected, RoundFinalized, Forced
FROM dbo.InvoiceDecisions WITH (UPDLOCK, HOLDLOCK) WHERE DecisionKey=@k;", cn, tx);
                q.Parameters.AddWithValue("@k", decisionKey);
                using SqlDataReader r = await q.ExecuteReaderAsync(ct);
                if (await r.ReadAsync(ct))
                {
                    long storedInvoice = r.GetInt64(0);
                    if (storedInvoice != invoiceId)
                    {
                        await r.CloseAsync();
                        throw new ApiException(409, "decision_key_reused",
                            "این DecisionKey قبلاً برای فاکتور دیگری ثبت شده است.");
                    }
                    string storedStatus = r.GetString(1);
                    int? storedRound = r.IsDBNull(2) ? null : r.GetInt32(2);
                    bool roundFinalized = r.GetBoolean(5);
                    bool forced = r.GetBoolean(6);
                    await r.CloseAsync();
                    await tx.CommitAsync(ct);

                    int roundCount = storedRound.HasValue ? await ReadRoundCountAsync(cn, storedRound.Value, ct) : 0;
                    return new DecisionResult
                    {
                        InvoiceId = invoiceId,
                        Status = storedStatus,
                        RoundId = storedRound,
                        RoundCount = roundCount,
                        CountChanged = false,
                        AlreadyDecided = true,
                        Forced = forced,
                        FinalizedRound = roundFinalized,
                        Message = "این تصمیم قبلاً با همین DecisionKey ثبت شده است و دوباره اجرا نشد."
                    };
                }
            }

            // ۲) قفل و خواندن وضعیت فعلی فاکتور
            (long fCode, string status, int? roundId) = await LockInvoiceAsync(cn, tx, invoiceId, ct);
            // شواهد تطبیق پیش از هر تغییر خوانده می‌شود تا در تاریخچهٔ تصمیم باقی بماند.
            string? evidence = await ReadEvidenceAsync(cn, tx, invoiceId, ct);

            // ۳) وضعیت نتیجهٔ قرعه‌کشی دورهٔ مربوطه
            (int? runId, bool finalized) = await ReadRunStateAsync(cn, tx, roundId, ct);

            if (finalized && !req.Force)
                throw new ApiException(409, "round_result_finalized",
                    "نتیجهٔ قرعه‌کشی این دوره نهایی شده است؛ تغییر وضعیت فاکتور فقط با مسیر ویژه و دلیل مکتوب (force=true) ممکن است.");

            if (finalized && req.Force && reason.Length == 0)
                throw new ApiException(400, "force_reason_required",
                    "برای تصمیم روی دورهٔ نهایی‌شده، ثبت دلیل الزامی است.");

            string basis = finalized && req.Force ? "forced" : (reason.Length > 0 ? "manager" : "evidence");

            // وضعیت پردازش‌شدهٔ فاکتور
            string newStatus;
            int? newRoundId = roundId;
            int? countBefore = null;
            int? countAfter = null;
            bool countEffected = false;
            string message;

            if (verdict == "valid")
            {
                switch (status)
                {
                    case "Valid":
                    case "Held":
                        await tx.CommitAsync(ct);
                        int currentCount = roundId.HasValue ? await ReadRoundCountAsync(cn, roundId.Value, ct) : 0;
                        return new DecisionResult
                        {
                            InvoiceId = invoiceId, FCode = fCode, Verdict = verdict, Status = status,
                            RoundId = roundId, RoundCount = currentCount, CountChanged = false,
                            AlreadyDecided = true, Forced = false, FinalizedRound = finalized,
                            Message = status == "Held"
                                ? "این فاکتور از قبل معتبر است و در وضعیت انتظار نگه داشته شده؛ تصمیم تکراری اجرا نشد."
                                : "این فاکتور از قبل معتبر است؛ تصمیم تکراری اجرا نشد."
                        };

                    case "NeedsReview":
                        // شواهد کافی نیست که خودکار معتبر شود؛ تصمیم مدیر آن را تأیید می‌کند.
                        if (roundId.HasValue)
                        {
                            (countBefore, countAfter) = await KeepInRoundAsync(cn, tx, invoiceId, roundId.Value, ct);
                            newStatus = "Valid";
                            message = "فاکتور تأیید شد؛ چون به دورهٔ تاریخی تعلق دارد، شمارش دوره تغییر نکرد.";
                        }
                        else
                        {
                            (newStatus, newRoundId, countBefore, countAfter, countEffected) =
                                await AttachToOpenRoundAsync(cn, tx, invoiceId, ct);
                            message = newStatus == "Valid"
                                ? "فاکتور تأیید و به دورهٔ جاری اضافه شد."
                                : "دورهٔ باز ظرفیت ندارد؛ فاکتور تأیید و در وضعیت انتظار (Held) نگه داشته شد.";
                        }
                        break;

                    case "Voided":
                        if (roundId.HasValue && await IsCollectingAsync(cn, tx, roundId.Value, ct))
                        {
                            // دورهٔ مربوطه هنوز در حال جمع‌آوری است: اگر ظرفیت باشد فاکتور برمی‌گردد.
                            int target = await ReadRoundTargetAsync(cn, tx, roundId.Value, ct);
                            int count = await ReadRoundCountAsync(cn, tx, roundId.Value, ct);
                            countBefore = count;
                            if (count < target)
                            {
                                countAfter = count + 1;
                                await SetRoundCountAsync(cn, tx, roundId.Value, countAfter.Value, target, ct);
                                countEffected = true;
                                newStatus = "Valid";
                                newRoundId = roundId;
                                message = "ابطال لغو شد؛ فاکتور به اعتبار و شمارندهٔ دورهٔ باز برگشت.";
                            }
                            else
                            {
                                countAfter = count;
                                newStatus = "Held";
                                newRoundId = null;
                                message = "ابطال لغو شد؛ دورهٔ باز ظرفیت ندارد و فاکتور در وضعیت انتظار ماند.";
                            }

                            using SqlCommand upd = new(@"
UPDATE dbo.Invoices SET Status=@st, RoundId=@rid, VoidedAt=NULL, VoidedBy=NULL, VoidReason=NULL
WHERE InvoiceId=@id;", cn, tx);
                            upd.Parameters.AddWithValue("@st", newStatus);
                            upd.Parameters.AddWithValue("@rid", (object?)newRoundId ?? DBNull.Value);
                            upd.Parameters.AddWithValue("@id", invoiceId);
                            await upd.ExecuteNonQueryAsync(ct);
                        }
                        else if (roundId.HasValue)
                        {
                            // دورهٔ تاریخی: برچسب دوره حفظ می‌شود و شمارش دست‌نخورده می‌ماند.
                            countBefore = await ReadRoundCountAsync(cn, tx, roundId.Value, ct);
                            countAfter = countBefore;
                            newStatus = "Valid";
                            newRoundId = roundId;
                            await ClearVoidAsync(cn, tx, invoiceId, ct);
                            message = "ابطال لغو شد؛ چون دورهٔ مربوطه بسته/تکمیل‌شده بود، شمارش تاریخی تغییر نکرد.";
                        }
                        else
                        {
                            (newStatus, newRoundId, countBefore, countAfter, countEffected) =
                                await AttachToOpenRoundAsync(cn, tx, invoiceId, ct);
                            await ClearVoidAsync(cn, tx, invoiceId, ct);
                            message = newStatus == "Valid"
                                ? "ابطال لغو شد و فاکتور به دورهٔ جاری اضافه شد."
                                : "ابطال لغو شد؛ دورهٔ باز ظرفیت ندارد و فاکتور در وضعیت انتظار ماند.";
                        }
                        break;

                    default: // OutOfScope و هر وضعیت ناشناخته
                        throw new ApiException(409, "decision_not_allowed",
                            "این فاکتور خارج از دامنهٔ کمپین است و با تصمیم مدیریتی معتبر نمی‌شود.");
                }
            }
            else // verdict == "void"
            {
                if (status == "Voided")
                {
                    await tx.CommitAsync(ct);
                    int currentCount = roundId.HasValue ? await ReadRoundCountAsync(cn, roundId.Value, ct) : 0;
                    return new DecisionResult
                    {
                        InvoiceId = invoiceId, FCode = fCode, Verdict = verdict, Status = status,
                        RoundId = roundId, RoundCount = currentCount, CountChanged = false,
                        AlreadyDecided = true, FinalizedRound = finalized,
                        Message = "این فاکتور از قبل باطل شده است؛ تصمیم تکراری اجرا نشد."
                    };
                }

                if (reason.Length == 0)
                    throw new ApiException(400, "reason_required", "برای تأیید ابطال، ثبت دلیل الزامی است.");

                bool release = false;
                if (roundId.HasValue)
                {
                    bool collecting = await IsCollectingAsync(cn, tx, roundId.Value, ct);
                    countBefore = await ReadRoundCountAsync(cn, tx, roundId.Value, ct);
                    countAfter = countBefore.Value;
                    release = collecting;
                    if (collecting)
                    {
                        if (countBefore.Value > 0)
                        {
                            countAfter = countBefore.Value - 1;
                            await SetRoundCountAsync(cn, tx, roundId.Value, countAfter.Value, -1, ct);
                            countEffected = true;
                        }
                        // فاکتور دیگر به شمارش آن دوره تعلق ندارد؛ برچسب دوره برداشته می‌شود.
                        newRoundId = null;
                    }
                    else
                    {
                        countEffected = false;
                    }
                }

                using SqlCommand upd = new(@"
UPDATE dbo.Invoices
SET Status='Voided', VoidedAt=SYSUTCDATETIME(), VoidedBy=@actor, VoidReason=@reason,
    ReviewReason=NULL, ReviewEvidence=NULL,
    RoundId = CASE WHEN @release = 1 THEN NULL ELSE RoundId END
WHERE InvoiceId=@id;", cn, tx);
                upd.Parameters.AddWithValue("@actor", actor);
                upd.Parameters.AddWithValue("@reason", reason);
                upd.Parameters.AddWithValue("@release", release ? 1 : 0);
                upd.Parameters.AddWithValue("@id", invoiceId);
                await upd.ExecuteNonQueryAsync(ct);

                newStatus = "Voided";
                message = countEffected
                    ? "ابطال فاکتور تأیید شد و از شمارندهٔ دورهٔ جاری حذف شد."
                    : (roundId.HasValue
                        ? "ابطال فاکتور تأیید شد؛ دورهٔ مربوطه در حال جمع‌آوری نبود، پس شمارش تاریخی تغییر نکرد."
                        : "ابطال فاکتور تأیید شد.");
            }

            // ۴) مسیر ویژهٔ حسابرسی‌شده: علامت‌گذاری نتیجهٔ نهایی‌شده برای بازبینی، بدون تغییر بی‌صدای نتیجه
            if (finalized && req.Force && runId.HasValue)
            {
                using SqlCommand flag = new(@"
UPDATE dbo.LotteryRuns SET ResultsQuestioned=1 WHERE RunId=@id;", cn, tx);
                flag.Parameters.AddWithValue("@id", runId.Value);
                await flag.ExecuteNonQueryAsync(ct);
            }

            // ۵) ثبت تاریخچهٔ تصمیم (فقط افزودنی)
            using (SqlCommand ins = new(@"
INSERT INTO dbo.InvoiceDecisions(InvoiceId, FCode, Verdict, Basis, PrevStatus, NewStatus, PrevRoundId, NewRoundId,
    CountBefore, CountAfter, CountEffected, RoundFinalized, Forced, Actor, Reason, Evidence, DecisionKey, DecidedAt)
VALUES(@id, @f, @v, @basis, @prev, @next, @prevRound, @newRound, @cb, @ca, @effected, @finalized, @forced,
       @actor, @reason, @evidence, @key, SYSUTCDATETIME());", cn, tx))
            {
                ins.Parameters.AddWithValue("@id", invoiceId);
                ins.Parameters.AddWithValue("@f", fCode);
                ins.Parameters.AddWithValue("@v", verdict);
                ins.Parameters.AddWithValue("@basis", basis);
                ins.Parameters.AddWithValue("@prev", status);
                ins.Parameters.AddWithValue("@next", newStatus);
                ins.Parameters.AddWithValue("@prevRound", (object?)roundId ?? DBNull.Value);
                ins.Parameters.AddWithValue("@newRound", (object?)newRoundId ?? DBNull.Value);
                ins.Parameters.AddWithValue("@cb", (object?)countBefore ?? DBNull.Value);
                ins.Parameters.AddWithValue("@ca", (object?)countAfter ?? DBNull.Value);
                ins.Parameters.AddWithValue("@effected", countEffected ? 1 : 0);
                ins.Parameters.AddWithValue("@finalized", finalized ? 1 : 0);
                ins.Parameters.AddWithValue("@forced", finalized && req.Force ? 1 : 0);
                ins.Parameters.AddWithValue("@actor", actor);
                ins.Parameters.AddWithValue("@reason", reason.Length == 0 ? (object)DBNull.Value : reason);
                ins.Parameters.AddWithValue("@evidence", (object?)evidence ?? DBNull.Value);
                ins.Parameters.AddWithValue("@key", (object?)decisionKey ?? DBNull.Value);
                await ins.ExecuteNonQueryAsync(ct);
            }

            await AuditWriter.WriteAsync(cn, tx, actor,
                finalized && req.Force ? "invoice.decision.forced" : "invoice.decision", "Invoice",
                invoiceId.ToString(),
                new
                {
                    invoiceId, fCode, verdict, basis, prevStatus = status, newStatus,
                    prevRoundId = roundId, newRoundId, countBefore, countAfter, countEffected,
                    roundFinalized = finalized, forced = finalized && req.Force, runId, reason
                }, ct);

            await tx.CommitAsync(ct);

            int finalCount = newRoundId.HasValue ? await ReadRoundCountAsync(cn, newRoundId.Value, ct) : 0;
            _log.LogInformation("decision invoice={InvoiceId} verdict={Verdict} basis={Basis} forced={Forced}",
                invoiceId, verdict, basis, finalized && req.Force);

            return new DecisionResult
            {
                InvoiceId = invoiceId,
                FCode = fCode,
                Verdict = verdict,
                Status = newStatus,
                RoundId = newRoundId,
                RoundCount = finalCount,
                CountChanged = countEffected,
                AlreadyDecided = false,
                Forced = finalized && req.Force,
                FinalizedRound = finalized,
                Message = message
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
    //  کمکی‌ها
    // ------------------------------------------------------------------
    /// <summary>فاکتور NeedsReview را به دورهٔ باز می‌چسباند یا در انتظار نگه می‌دارد.</summary>
    private static async Task<(string status, int? roundId, int? before, int? after, bool effected)> AttachToOpenRoundAsync(
        SqlConnection cn, SqlTransaction tx, long invoiceId, CancellationToken ct)
    {
        int? openId = null;
        int openCount = 0;
        int openTarget = 0;
        using (SqlCommand q = new(@"
SELECT TOP 1 RoundId, [Count], Target FROM dbo.Rounds WITH (UPDLOCK, HOLDLOCK)
WHERE Status='Collecting' ORDER BY RoundNo DESC;", cn, tx))
        using (SqlDataReader r = await q.ExecuteReaderAsync(ct))
        {
            if (await r.ReadAsync(ct))
            {
                openId = r.GetInt32(0);
                openCount = r.GetInt32(1);
                openTarget = r.GetInt32(2);
            }
        }

        if (openId.HasValue && openCount < openTarget)
        {
            int after = openCount + 1;
            await SetRoundCountAsync(cn, tx, openId.Value, after, openTarget, ct);
            using SqlCommand upd = new(@"
UPDATE dbo.Invoices SET Status='Valid', RoundId=@rid, ReviewReason=NULL, ReviewEvidence=NULL WHERE InvoiceId=@id;", cn, tx);
            upd.Parameters.AddWithValue("@rid", openId.Value);
            upd.Parameters.AddWithValue("@id", invoiceId);
            await upd.ExecuteNonQueryAsync(ct);
            return ("Valid", openId, openCount, after, true);
        }

        using (SqlCommand upd = new(@"
UPDATE dbo.Invoices SET Status='Held', RoundId=NULL, ReviewReason=NULL, ReviewEvidence=NULL WHERE InvoiceId=@id;", cn, tx))
        {
            upd.Parameters.AddWithValue("@id", invoiceId);
            await upd.ExecuteNonQueryAsync(ct);
        }
        return ("Held", null, openCount, openCount, false);
    }

    private static async Task ClearVoidAsync(SqlConnection cn, SqlTransaction tx, long invoiceId, CancellationToken ct)
    {
        using SqlCommand upd = new(@"
UPDATE dbo.Invoices SET VoidedAt=NULL, VoidedBy=NULL, VoidReason=NULL WHERE InvoiceId=@id;", cn, tx);
        upd.Parameters.AddWithValue("@id", invoiceId);
        await upd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>فاکتور تاریخی را معتبر می‌کند بدون تغییر شمارش دوره.</summary>
    private static async Task<(int? before, int? after)> KeepInRoundAsync(SqlConnection cn, SqlTransaction tx,
        long invoiceId, int roundId, CancellationToken ct)
    {
        int count = await ReadRoundCountAsync(cn, tx, roundId, ct);
        using SqlCommand upd = new(@"
UPDATE dbo.Invoices SET Status='Valid', ReviewReason=NULL, ReviewEvidence=NULL WHERE InvoiceId=@id;", cn, tx);
        upd.Parameters.AddWithValue("@id", invoiceId);
        await upd.ExecuteNonQueryAsync(ct);
        return (count, count);
    }

    private static async Task SetRoundCountAsync(SqlConnection cn, SqlTransaction tx, int roundId, int count,
        int target, CancellationToken ct)
    {
        bool unknownTarget = target < 0;
        using SqlCommand upd = new(unknownTarget
            ? @"UPDATE dbo.Rounds SET [Count]=@c WHERE RoundId=@id;"
            : @"UPDATE dbo.Rounds SET [Count]=@c,
                    Status = CASE WHEN @c >= Target THEN 'Completed' ELSE Status END,
                    CompletedAt = CASE WHEN @c >= Target THEN SYSUTCDATETIME() ELSE CompletedAt END
                WHERE RoundId=@id;", cn, tx);
        upd.Parameters.AddWithValue("@c", count);
        upd.Parameters.AddWithValue("@id", roundId);
        await upd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<(long fCode, string status, int? roundId)> LockInvoiceAsync(SqlConnection cn,
        SqlTransaction tx, long invoiceId, CancellationToken ct)
    {
        using SqlCommand q = new(@"
SELECT FCode, Status, RoundId FROM dbo.Invoices WITH (UPDLOCK, HOLDLOCK) WHERE InvoiceId=@id;", cn, tx);
        q.Parameters.AddWithValue("@id", invoiceId);
        using SqlDataReader r = await q.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) throw new ApiException(404, "invoice_not_found", "فاکتور پیدا نشد.");
        return (r.GetInt64(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetInt32(2));
    }

    private static async Task<string?> ReadEvidenceAsync(SqlConnection cn, SqlTransaction tx, long invoiceId,
        CancellationToken ct)
    {
        using SqlCommand q = new("SELECT ReviewEvidence FROM dbo.Invoices WHERE InvoiceId=@id", cn, tx);
        q.Parameters.AddWithValue("@id", invoiceId);
        object? o = await q.ExecuteScalarAsync(ct);
        return o == null || o == DBNull.Value ? null : Convert.ToString(o);
    }

    private static async Task<(int? runId, bool finalized)> ReadRunStateAsync(SqlConnection cn, SqlTransaction? tx,
        int? roundId, CancellationToken ct)
    {
        if (!roundId.HasValue) return (null, false);
        using SqlCommand q = new(@"
SELECT TOP 1 RunId, Status FROM dbo.LotteryRuns WITH (UPDLOCK, HOLDLOCK)
WHERE RoundId=@rid AND Status <> 'Aborted' ORDER BY RunId DESC;", cn, tx);
        q.Parameters.AddWithValue("@rid", roundId.Value);
        using SqlDataReader r = await q.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return (null, false);
        return (r.GetInt32(0), string.Equals(r.GetString(1), "Finalized", StringComparison.Ordinal));
    }

    private static async Task<bool> IsCollectingAsync(SqlConnection cn, SqlTransaction tx, int roundId, CancellationToken ct)
    {
        using SqlCommand q = new("SELECT Status FROM dbo.Rounds WHERE RoundId=@id", cn, tx);
        q.Parameters.AddWithValue("@id", roundId);
        return string.Equals(Convert.ToString(await q.ExecuteScalarAsync(ct)), "Collecting", StringComparison.Ordinal);
    }

    private static async Task<int> ReadRoundCountAsync(SqlConnection cn, int roundId, CancellationToken ct)
    {
        using SqlCommand q = new("SELECT [Count] FROM dbo.Rounds WHERE RoundId=@id", cn);
        q.Parameters.AddWithValue("@id", roundId);
        object? o = await q.ExecuteScalarAsync(ct);
        return o == null || o == DBNull.Value ? 0 : Convert.ToInt32(o);
    }

    private static async Task<int> ReadRoundCountAsync(SqlConnection cn, SqlTransaction tx, int roundId, CancellationToken ct)
    {
        using SqlCommand q = new("SELECT [Count] FROM dbo.Rounds WHERE RoundId=@id", cn, tx);
        q.Parameters.AddWithValue("@id", roundId);
        object? o = await q.ExecuteScalarAsync(ct);
        return o == null || o == DBNull.Value ? 0 : Convert.ToInt32(o);
    }

    private static async Task<int> ReadRoundTargetAsync(SqlConnection cn, SqlTransaction tx, int roundId, CancellationToken ct)
    {
        using SqlCommand q = new("SELECT Target FROM dbo.Rounds WHERE RoundId=@id", cn, tx);
        q.Parameters.AddWithValue("@id", roundId);
        return Convert.ToInt32(await q.ExecuteScalarAsync(ct));
    }

    private static async Task<InvoiceRow?> ReadInvoiceAsync(SqlConnection cn, SqlTransaction? tx, long invoiceId,
        CancellationToken ct)
    {
        using SqlCommand cmd = new(@"
SELECT InvoiceId, FCode, FCodeC, FDate, Jalali, FTime, UserCode, Cashier, Gross, Points,
       Status, RoundId, Mobile, VoidReason, ReceivedAt, ReviewReason, ReviewEvidence
FROM dbo.Invoices WHERE InvoiceId=@id;", cn, tx);
        cmd.Parameters.AddWithValue("@id", invoiceId);
        using SqlDataReader r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        return new InvoiceRow
        {
            InvoiceId = r.GetInt64(0),
            FCode = r.GetInt64(1),
            FCodeC = r.IsDBNull(2) ? 0 : r.GetInt64(2),
            FDate = DateOnly.FromDateTime(r.GetDateTime(3)),
            Jalali = r.IsDBNull(4) ? null : r.GetString(4),
            FTime = r.IsDBNull(5) ? null : r.GetString(5),
            UserCode = r.GetInt32(6),
            Cashier = r.IsDBNull(7) ? null : r.GetString(7),
            Gross = r.GetDecimal(8),
            Points = r.GetInt32(9),
            Status = r.GetString(10),
            RoundId = r.IsDBNull(11) ? null : r.GetInt32(11),
            Mobile = Security.MaskMobile(r.IsDBNull(12) ? null : r.GetString(12)),
            VoidReason = r.IsDBNull(13) ? null : r.GetString(13),
            ReceivedAt = r.GetDateTime(14),
            ReviewReason = r.IsDBNull(15) ? null : r.GetString(15),
            ReviewEvidence = r.IsDBNull(16) ? null : r.GetString(16)
        };
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