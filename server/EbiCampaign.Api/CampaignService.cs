using System.Data;
using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace EbiCampaign.Api;

public sealed class CampaignOptions
{
    public string SourceId { get; init; } = "Holoo1";
    public int TargetInvoices { get; init; } = 1000;
    public DateOnly StartDate { get; init; } = new(2026, 1, 1);
    public decimal PointsPerAmountToman { get; init; } = 500000m;
    public string PointsBase { get; init; } = "gross";
}

public sealed class CampaignService
{
    private readonly Db _db;
    private readonly CampaignOptions _opt;
    private readonly ILogger<CampaignService> _log;

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public CampaignService(Db db, CampaignOptions opt, ILogger<CampaignService> log)
    {
        _db = db;
        _opt = opt;
        _log = log;
    }

    // ------------------------------------------------------------------
    //  دوره‌ها
    // ------------------------------------------------------------------
    public async Task<RoundInfo?> GetCurrentRoundAsync(CancellationToken ct = default)
    {
        await using SqlConnection cn = await _db.OpenAsync(ct);
        RoundRow? row = await ReadRoundAsync(cn, null, ct);
        if (row == null) return null;
        return await ToInfoAsync(cn, row, ct);
    }

    public async Task<List<RoundInfo>> GetRoundsAsync(CancellationToken ct = default)
    {
        await using SqlConnection cn = await _db.OpenAsync(ct);
        List<RoundRow> rows = new();
        using (SqlCommand cmd = new(@"
SELECT RoundId, RoundNo, Status, Target, [Count], StartedAt, CompletedAt, ClosedAt
FROM dbo.Rounds ORDER BY RoundNo DESC", cn))
        using (SqlDataReader r = await cmd.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct)) rows.Add(MapRound(r));
        }
        List<RoundInfo> list = new();
        foreach (RoundRow row in rows) list.Add(await ToInfoAsync(cn, row, ct));
        return list;
    }

    public async Task<RoundInfo> StartRoundAsync(string actor, CancellationToken ct = default)
    {
        actor = CleanActor(actor);
        await using SqlConnection cn = await _db.OpenAsync(ct);
        await using SqlTransaction tx = (SqlTransaction)await cn.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        try
        {
            using (SqlCommand check = new(@"
IF EXISTS (SELECT 1 FROM dbo.Rounds WITH (UPDLOCK, HOLDLOCK) WHERE Status = 'Collecting')
    THROW 51001, N'یک دورهٔ در حال جمع‌آوری از قبل باز است.', 1;

DECLARE @lastNo INT = ISNULL((SELECT MAX(RoundNo) FROM dbo.Rounds WITH (UPDLOCK, HOLDLOCK)), 0);
DECLARE @lastStatus VARCHAR(20) = (SELECT TOP 1 Status FROM dbo.Rounds WITH (UPDLOCK, HOLDLOCK) ORDER BY RoundNo DESC);

IF @lastStatus IS NOT NULL AND @lastStatus <> 'Closed'
    THROW 51002, N'دورهٔ قبلی هنوز بسته نشده است؛ ابتدا آن را ببندید.', 1;

INSERT INTO dbo.Rounds(RoundNo, Status, Target, [Count], StartedAt, OpenedBy)
VALUES(@lastNo + 1, 'Collecting', @target, 0, SYSUTCDATETIME(), @actor);

SELECT CAST(SCOPE_IDENTITY() AS INT);", cn, tx))
            {
                check.Parameters.AddWithValue("@target", _opt.TargetInvoices);
                check.Parameters.AddWithValue("@actor", actor);
                int id = Convert.ToInt32(await check.ExecuteScalarAsync(ct));
                await AuditAsync(cn, tx, actor, "round.start", "Round", id.ToString(), new { roundId = id }, ct);
                await tx.CommitAsync(ct);
                RoundRow? row = await ReadRoundAsync(cn, id, ct);
                return await ToInfoAsync(cn, row!, ct);
            }
        }
        catch (SqlException ex) when (ex.Number == 51001 || ex.Number == 51002)
        {
            await tx.RollbackAsync(ct);
            throw new ApiException(409, ex.Number == 51001 ? "round_already_open" : "round_not_closed", ex.Message);
        }
        catch
        {
            try { await tx.RollbackAsync(ct); } catch { }
            throw;
        }
    }

    public async Task<RoundInfo> CloseRoundAsync(int roundId, string actor, CancellationToken ct = default)
    {
        actor = CleanActor(actor);
        await using SqlConnection cn = await _db.OpenAsync(ct);
        await using SqlTransaction tx = (SqlTransaction)await cn.BeginTransactionAsync(ct);

        int changed;
        using (SqlCommand cmd = new(@"
UPDATE dbo.Rounds SET Status='Closed', ClosedAt=SYSUTCDATETIME()
WHERE RoundId=@id AND Status='Completed';", cn, tx))
        {
            cmd.Parameters.AddWithValue("@id", roundId);
            changed = await cmd.ExecuteNonQueryAsync(ct);
        }

        if (changed == 0)
        {
            string? status = null;
            using (SqlCommand q = new("SELECT Status FROM dbo.Rounds WHERE RoundId=@id", cn, tx))
            {
                q.Parameters.AddWithValue("@id", roundId);
                object? o = await q.ExecuteScalarAsync(ct);
                status = o == null || o == DBNull.Value ? null : Convert.ToString(o);
            }
            if (status == "Closed")
            {
                await tx.CommitAsync(ct);
                return (await ToInfoAsync(cn, (await ReadRoundAsync(cn, roundId, ct))!, ct));
            }
            await tx.RollbackAsync(ct);
            if (status == null) throw new ApiException(404, "round_not_found", "دوره پیدا نشد.");
            throw new ApiException(409, "round_not_completed", "فقط دورهٔ تکمیل‌شده را می‌توان بست.");
        }

        await AuditAsync(cn, tx, actor, "round.close", "Round", roundId.ToString(), new { roundId }, ct);
        await tx.CommitAsync(ct);
        return await ToInfoAsync(cn, (await ReadRoundAsync(cn, roundId, ct))!, ct);
    }

    // ------------------------------------------------------------------
    //  دریافت فاکتور (ایدِمپوتنت)
    // ------------------------------------------------------------------
    public async Task<IngestResponse> IngestAsync(string agentId, IngestRequest req, string bodyHash, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(req.RequestId) || req.RequestId.Length > 64)
            throw new ApiException(400, "request_id_invalid", "RequestId الزامی و حداکثر ۶۴ کاراکتر است.");
        if (!string.Equals(req.SourceId, _opt.SourceId, StringComparison.Ordinal))
            throw new ApiException(400, "source_invalid", "SourceId با منبع پیکربندی‌شده مطابقت ندارد.");
        if (req.Invoices == null || req.Invoices.Count == 0 || req.Invoices.Count > 500)
            throw new ApiException(400, "batch_invalid", "تعداد فاکتورهای هر درخواست باید بین ۱ و ۵۰۰ باشد.");

        await using SqlConnection cn = await _db.OpenAsync(ct);
        await using SqlTransaction tx = (SqlTransaction)await cn.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        try
        {
            // ۱) ایدِمپوتنسی
            using (SqlCommand cached = new(@"
SELECT BodyHash, StatusCode, ResponseJson FROM dbo.IngestRequests WITH (UPDLOCK, HOLDLOCK)
WHERE RequestId=@id;", cn, tx))
            {
                cached.Parameters.AddWithValue("@id", req.RequestId);
                using SqlDataReader r = await cached.ExecuteReaderAsync(ct);
                if (await r.ReadAsync(ct))
                {
                    string hash = r.GetString(0);
                    int status = r.GetInt32(1);
                    string json = r.GetString(2);
                    await r.CloseAsync();

                    if (!string.Equals(hash, bodyHash, StringComparison.OrdinalIgnoreCase))
                    {
                        await tx.RollbackAsync(ct);
                        throw new ApiException(409, "request_id_reused", "همین RequestId با بدنهٔ دیگری استفاده شده است.");
                    }

                    await tx.CommitAsync(ct);
                    IngestResponse? old = JsonSerializer.Deserialize<IngestResponse>(json, Json);
                    if (old == null) throw new ApiException(500, "cache_corrupt", "پاسخ ذخیره‌شده قابل خواندن نبود.");
                    old.Cached = true;
                    return old;
                }
            }

            // ۲) قفل دورهٔ باز (اگر وجود دارد)
            RoundRow? open = await LockOpenRoundAsync(cn, tx, ct);
            int count = open?.Count ?? 0;
            bool completedNow = false;
            DateTime? completedAt = open?.CompletedAt;

            IngestResponse res = new() { RequestId = req.RequestId };
            HashSet<long> seen = new();
            DateTime now = DateTime.UtcNow;

            foreach (IngestInvoice inv in req.Invoices)
            {
                if (inv.FCode <= 0 || inv.FDate == default)
                {
                    res.Results.Add(new InvoiceResult { FCode = inv.FCode, Result = "rejected", Message = "FCode یا تاریخ نامعتبر است." });
                    continue;
                }

                if (!seen.Add(inv.FCode))
                {
                    res.Duplicates++;
                    res.Results.Add(new InvoiceResult { FCode = inv.FCode, Result = "duplicate", Message = "تکرار در همان دسته." });
                    continue;
                }

                string mobile = Security.NormalizeMobile(inv.Mobile);
                string decided = DecideStatus(inv, out string? note);
                int points = mobile.Length == 0 ? 0 : ComputePoints(inv.Gross, inv.Discount, mobile);

                // ۳) رکورد موجود؟
                ExistingRow? ex = await FindInvoiceAsync(cn, tx, inv.FCode, ct);
                if (ex != null)
                {
                    // عامل همگام‌سازی فاکتور را «مشکوک به ابطال» علامت زده است.
                    // اگر هنوز در دورهٔ در حال جمع‌آوری شمرده شده، از شمارنده خارج می‌شود؛
                    // در دورهٔ تکمیل/بسته فقط برای بررسی مدیریتی علامت می‌خورد.
                    if (string.Equals(inv.Status, "review", StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(ex.Status, "NeedsReview", StringComparison.Ordinal)
                        && !string.Equals(ex.Status, "Voided", StringComparison.Ordinal))
                    {
                        bool released = false;
                        if (string.Equals(ex.Status, "Valid", StringComparison.Ordinal) && ex.RoundId.HasValue
                            && open != null && open.RoundId == ex.RoundId.Value && open.Status == "Collecting")
                        {
                            count = Math.Max(0, count - 1);
                            released = true;
                        }

                        string reviewReason = CleanReviewReason(inv.ReviewReason);
                        if (reviewReason.Length == 0) reviewReason = released ? "missing" : "source";
                        string reviewEvidence = Trim400(inv.ReviewEvidence);
                        using (SqlCommand upd = new(@"
UPDATE dbo.Invoices
SET Status='NeedsReview', RoundId = CASE WHEN @release = 1 THEN NULL ELSE RoundId END,
    ReviewReason=@reason, ReviewEvidence=@evidence
WHERE InvoiceId=@id", cn, tx))
                        {
                            upd.Parameters.AddWithValue("@release", released ? 1 : 0);
                            upd.Parameters.AddWithValue("@reason", reviewReason);
                            upd.Parameters.AddWithValue("@evidence", (object?)reviewEvidence ?? DBNull.Value);
                            upd.Parameters.AddWithValue("@id", ex.InvoiceId);
                            await upd.ExecuteNonQueryAsync(ct);
                        }

                        await AuditAsync(cn, tx, agentId, "invoice.review", "Invoice", ex.InvoiceId.ToString(),
                            new { fCode = inv.FCode, countDecremented = released, requiresReview = !released, reason = reviewReason }, ct);
                        res.NeedsReview++;
                        res.Results.Add(new InvoiceResult
                        {
                            FCode = inv.FCode, Result = "review", InvoiceId = ex.InvoiceId,
                            Status = "NeedsReview", RoundId = released ? null : ex.RoundId,
                            Message = released
                                ? "فاکتور مشکوک به ابطال است و از شمارندهٔ دورهٔ جاری خارج شد."
                                : "فاکتور مشکوک به ابطال است؛ چون دوره بسته/تکمیل شده، شمارش تاریخی تغییر نکرد."
                        });
                        continue;
                    }

                    if (mobile.Length > 0 && string.IsNullOrEmpty(ex.Mobile))
                    {
                        using SqlCommand upd = new(@"
UPDATE dbo.Invoices SET Mobile=@m, Points=@p WHERE InvoiceId=@id AND (Mobile IS NULL OR Mobile='');", cn, tx);
                        upd.Parameters.AddWithValue("@m", mobile);
                        upd.Parameters.AddWithValue("@p", points);
                        upd.Parameters.AddWithValue("@id", ex.InvoiceId);
                        await upd.ExecuteNonQueryAsync(ct);
                        await AuditAsync(cn, tx, agentId, "invoice.mobile", "Invoice", ex.InvoiceId.ToString(),
                            new { fCode = inv.FCode, fCodeC = inv.FCodeC, points }, ct);
                        res.Updated++;
                        res.Results.Add(new InvoiceResult
                        {
                            FCode = inv.FCode, Result = "updated", InvoiceId = ex.InvoiceId,
                            Status = ex.Status, RoundId = ex.RoundId, Message = "شماره موبایل به فاکتور اضافه شد."
                        });
                    }
                    else if (mobile.Length > 0 && !string.Equals(mobile, ex.Mobile, StringComparison.Ordinal))
                    {
                        res.Conflicts++;
                        res.Results.Add(new InvoiceResult
                        {
                            FCode = inv.FCode, Result = "conflict", InvoiceId = ex.InvoiceId,
                            Status = ex.Status, RoundId = ex.RoundId,
                            Message = "این فاکتور از قبل شمارهٔ دیگری دارد؛ تغییر فقط با مدیر مجاز."
                        });
                    }
                    else
                    {
                        res.Duplicates++;
                        res.Results.Add(new InvoiceResult
                        {
                            FCode = inv.FCode, Result = "duplicate", InvoiceId = ex.InvoiceId,
                            Status = ex.Status, RoundId = ex.RoundId
                        });
                    }
                    continue;
                }

                // ۴) تعیین وضعیت و تخصیص
                string status = decided;
                int? roundId = null;

                if (decided == "Valid")
                {
                    if (open != null && count < open.Target)
                    {
                        roundId = open.RoundId;
                        count++;
                        status = "Valid";
                        if (count >= open.Target)
                        {
                            completedNow = true;
                            completedAt = now;
                        }
                    }
                    else
                    {
                        status = "Held";
                        res.Held++;
                    }
                }
                else if (decided == "NeedsReview") res.NeedsReview++;
                else res.OutOfScope++;

                string newReviewReason = status == "NeedsReview" ? CleanReviewReason(inv.ReviewReason) : "";
                if (status == "NeedsReview" && newReviewReason.Length == 0) newReviewReason = "source";
                string newReviewEvidence = status == "NeedsReview" ? Trim400(inv.ReviewEvidence) : "";

                long id;
                using (SqlCommand ins = new(@"
INSERT INTO dbo.Invoices(SourceId, FCode, FCodeC, FType, FDate, Jalali, FTime, UserCode, Cashier,
    Gross, Discount, Net, CustomerCode, Mobile, Points, Status, RoundId, AgentId, ReceivedAt,
    ReviewReason, ReviewEvidence)
VALUES(@src, @f, @fc, @ft, @fd, @j, @t, @u, @cashier, @g, @d, @n, @cc, @m, @p, @st, @rid, @agent, @now,
    @rr, @re);
SELECT CAST(SCOPE_IDENTITY() AS BIGINT);", cn, tx))
                {
                    ins.Parameters.AddWithValue("@src", _opt.SourceId);
                    ins.Parameters.AddWithValue("@f", inv.FCode);
                    ins.Parameters.AddWithValue("@fc", inv.FCodeC);
                    ins.Parameters.AddWithValue("@ft", inv.FType);
                    ins.Parameters.Add("@fd", SqlDbType.Date).Value = inv.FDate.ToDateTime(TimeOnly.MinValue);
                    ins.Parameters.AddWithValue("@j", (object?)NullIfEmpty(inv.Jalali) ?? DBNull.Value);
                    ins.Parameters.AddWithValue("@t", (object?)NullIfEmpty(inv.FTime) ?? DBNull.Value);
                    ins.Parameters.AddWithValue("@u", inv.UserCode);
                    ins.Parameters.AddWithValue("@cashier", (object?)NullIfEmpty(inv.Cashier) ?? DBNull.Value);
                    ins.Parameters.Add("@g", SqlDbType.Decimal).Value = inv.Gross;
                    ins.Parameters["@g"].Precision = 18; ins.Parameters["@g"].Scale = 2;
                    ins.Parameters.Add("@d", SqlDbType.Decimal).Value = inv.Discount;
                    ins.Parameters["@d"].Precision = 18; ins.Parameters["@d"].Scale = 2;
                    ins.Parameters.Add("@n", SqlDbType.Decimal).Value = Math.Max(0, inv.Net);
                    ins.Parameters["@n"].Precision = 18; ins.Parameters["@n"].Scale = 2;
                    ins.Parameters.AddWithValue("@cc", (object?)NullIfEmpty(inv.CustomerCode) ?? DBNull.Value);
                    ins.Parameters.AddWithValue("@m", mobile.Length == 0 ? DBNull.Value : mobile);
                    ins.Parameters.AddWithValue("@p", points);
                    ins.Parameters.AddWithValue("@st", status);
                    ins.Parameters.AddWithValue("@rid", (object?)roundId ?? DBNull.Value);
                    ins.Parameters.AddWithValue("@agent", agentId);
                    ins.Parameters.Add("@now", SqlDbType.DateTime2).Value = now;
                    ins.Parameters.AddWithValue("@rr", newReviewReason.Length == 0 ? (object)DBNull.Value : newReviewReason);
                    ins.Parameters.AddWithValue("@re", newReviewEvidence.Length == 0 ? (object)DBNull.Value : newReviewEvidence);
                    id = Convert.ToInt64(await ins.ExecuteScalarAsync(ct));
                }

                string result = status switch
                {
                    "Valid" => "accepted",
                    "Held" => "held",
                    "NeedsReview" => "review",
                    _ => "out_of_scope"
                };
                if (result == "accepted") res.Accepted++;
                res.Results.Add(new InvoiceResult
                {
                    FCode = inv.FCode, Result = result, InvoiceId = id,
                    Status = status, RoundId = roundId, Message = note
                });
            }

            // ۵) به‌روزرسانی شمارنده و وضعیت دوره
            if (open != null && count != open.Count)
            {
                using SqlCommand upd = new(@"
UPDATE dbo.Rounds SET [Count]=@c, Status=@st, CompletedAt=@done WHERE RoundId=@id;", cn, tx);
                upd.Parameters.AddWithValue("@c", count);
                upd.Parameters.AddWithValue("@st", completedNow ? "Completed" : open.Status);
                upd.Parameters.Add("@done", SqlDbType.DateTime2).Value = (object?)completedAt ?? DBNull.Value;
                upd.Parameters.AddWithValue("@id", open.RoundId);
                await upd.ExecuteNonQueryAsync(ct);

                if (completedNow)
                    await AuditAsync(cn, tx, "system", "round.completed", "Round", open.RoundId.ToString(),
                        new { roundId = open.RoundId, count }, ct);
            }

            res.Round = await ReadCurrentRoundInfoAsync(cn, tx, ct);
            string responseJson = JsonSerializer.Serialize(res, Json);

            using (SqlCommand save = new(@"
INSERT INTO dbo.IngestRequests(RequestId, AgentId, BodyHash, StatusCode, ResponseJson, CreatedAt)
VALUES(@id, @agent, @hash, 200, @json, SYSUTCDATETIME());", cn, tx))
            {
                save.Parameters.AddWithValue("@id", req.RequestId);
                save.Parameters.AddWithValue("@agent", agentId);
                save.Parameters.AddWithValue("@hash", bodyHash);
                save.Parameters.AddWithValue("@json", responseJson);
                await save.ExecuteNonQueryAsync(ct);
            }

            await tx.CommitAsync(ct);
            _log.LogInformation("ingest {RequestId} agent={Agent} new={New} dup={Dup} held={Held}",
                req.RequestId, agentId, res.Accepted, res.Duplicates, res.Held);
            return res;
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
    //  Held / ابطال / فهرست‌ها
    // ------------------------------------------------------------------
    public async Task<List<HeldInvoice>> GetHeldAsync(int limit, CancellationToken ct = default)
    {
        limit = Math.Clamp(limit, 1, 500);
        await using SqlConnection cn = await _db.OpenAsync(ct);
        List<HeldInvoice> list = new();
        using SqlCommand cmd = new(@"
SELECT TOP (@n) InvoiceId, FCode, FCodeC, FDate, Jalali, UserCode, Cashier, Gross, Points, Mobile
FROM dbo.Invoices WHERE Status='Held' ORDER BY FDate, FCode;", cn);
        cmd.Parameters.AddWithValue("@n", limit);
        using SqlDataReader r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            list.Add(new HeldInvoice
            {
                InvoiceId = r.GetInt64(0),
                FCode = r.GetInt64(1),
                FCodeC = r.IsDBNull(2) ? 0 : r.GetInt64(2),
                FDate = DateOnly.FromDateTime(r.GetDateTime(3)),
                Jalali = r.IsDBNull(4) ? null : r.GetString(4),
                UserCode = r.GetInt32(5),
                Cashier = r.IsDBNull(6) ? null : r.GetString(6),
                Gross = r.GetDecimal(7),
                Points = r.GetInt32(8),
                Mobile = Security.MaskMobile(r.IsDBNull(9) ? null : r.GetString(9)),
                Status = "Held"
            });
        }
        return list;
    }

    public async Task<int> AssignHeldAsync(int roundId, int limit, string actor, CancellationToken ct = default)
    {
        actor = CleanActor(actor);
        limit = Math.Clamp(limit, 1, _opt.TargetInvoices);
        await using SqlConnection cn = await _db.OpenAsync(ct);
        await using SqlTransaction tx = (SqlTransaction)await cn.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        try
        {
            RoundRow? round = await ReadRoundAsync(cn, roundId, tx, ct);
            if (round == null) throw new ApiException(404, "round_not_found", "دوره پیدا نشد.");
            if (round.Status != "Collecting")
                throw new ApiException(409, "round_not_collecting", "فقط به دورهٔ در حال جمع‌آوری می‌توان فاکتور منتظر افزود.");

            int capacity = round.Target - round.Count;
            int take = Math.Min(capacity, limit);
            int assigned = 0;

            if (take > 0)
            {
                using SqlCommand upd = new($@"
UPDATE TOP (@take) dbo.Invoices
SET Status='Valid', RoundId=@rid
WHERE Status='Held';", cn, tx);
                upd.Parameters.AddWithValue("@take", take);
                upd.Parameters.AddWithValue("@rid", roundId);
                assigned = await upd.ExecuteNonQueryAsync(ct);

                int newCount = round.Count + assigned;
                bool full = newCount >= round.Target;
                using SqlCommand upd2 = new(@"
UPDATE dbo.Rounds SET [Count]=@c, Status=@st, CompletedAt=@done WHERE RoundId=@id;", cn, tx);
                upd2.Parameters.AddWithValue("@c", newCount);
                upd2.Parameters.AddWithValue("@st", full ? "Completed" : "Collecting");
                upd2.Parameters.Add("@done", SqlDbType.DateTime2).Value = full ? DateTime.UtcNow : DBNull.Value;
                upd2.Parameters.AddWithValue("@id", roundId);
                await upd2.ExecuteNonQueryAsync(ct);

                await AuditAsync(cn, tx, actor, "round.assign_held", "Round", roundId.ToString(),
                    new { roundId, assigned, count = newCount }, ct);
            }

            await tx.CommitAsync(ct);
            return assigned;
        }
        catch (ApiException)
        {
            await tx.RollbackAsync(ct);
            throw;
        }
        catch
        {
            try { await tx.RollbackAsync(ct); } catch { }
            throw;
        }
    }

    public async Task<VoidResult> VoidInvoiceAsync(long invoiceId, string reason, string actor, CancellationToken ct = default)
    {
        actor = CleanActor(actor);
        if (string.IsNullOrWhiteSpace(reason))
            throw new ApiException(400, "reason_required", "دلیل ابطال الزامی است.");
        if (reason.Length > 400) reason = reason[..400];

        await using SqlConnection cn = await _db.OpenAsync(ct);
        await using SqlTransaction tx = (SqlTransaction)await cn.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        try
        {
            long fCode; string status; int? roundId;
            using (SqlCommand q = new(@"
SELECT FCode, Status, RoundId FROM dbo.Invoices WITH (UPDLOCK, HOLDLOCK) WHERE InvoiceId=@id;", cn, tx))
            {
                q.Parameters.AddWithValue("@id", invoiceId);
                using SqlDataReader r = await q.ExecuteReaderAsync(ct);
                if (!await r.ReadAsync(ct))
                {
                    await r.CloseAsync();
                    throw new ApiException(404, "invoice_not_found", "فاکتور پیدا نشد.");
                }
                fCode = r.GetInt64(0);
                status = r.GetString(1);
                roundId = r.IsDBNull(2) ? null : r.GetInt32(2);
                await r.CloseAsync();
            }

            if (status == "Voided")
            {
                await tx.CommitAsync(ct);
                return new VoidResult { InvoiceId = invoiceId, AlreadyVoided = true, Message = "این فاکتور قبلاً باطل شده است." };
            }

            bool collecting = false;
            if (roundId.HasValue)
            {
                using SqlCommand qr = new("SELECT Status FROM dbo.Rounds WHERE RoundId=@id", cn, tx);
                qr.Parameters.AddWithValue("@id", roundId.Value);
                object? o = await qr.ExecuteScalarAsync(ct);
                collecting = Convert.ToString(o) == "Collecting";
            }

            using (SqlCommand upd = new(@"
UPDATE dbo.Invoices
SET Status='Voided', VoidedAt=SYSUTCDATETIME(), VoidedBy=@actor, VoidReason=@reason,
    RoundId = CASE WHEN @release = 1 THEN NULL ELSE RoundId END
WHERE InvoiceId=@id;", cn, tx))
            {
                upd.Parameters.AddWithValue("@actor", actor);
                upd.Parameters.AddWithValue("@reason", reason);
                upd.Parameters.AddWithValue("@release", collecting ? 1 : 0);
                upd.Parameters.AddWithValue("@id", invoiceId);
                await upd.ExecuteNonQueryAsync(ct);
            }

            bool decremented = false;
            if (collecting && roundId.HasValue)
            {
                using SqlCommand upd = new(@"
UPDATE dbo.Rounds SET [Count] = CASE WHEN [Count] > 0 THEN [Count] - 1 ELSE 0 END WHERE RoundId=@id;", cn, tx);
                upd.Parameters.AddWithValue("@id", roundId.Value);
                await upd.ExecuteNonQueryAsync(ct);
                decremented = true;
            }

            await AuditAsync(cn, tx, actor, "invoice.void", "Invoice", invoiceId.ToString(),
                new { invoiceId, fCode, reason, countDecremented = decremented, requiresReview = !collecting }, ct);
            await tx.CommitAsync(ct);

            return new VoidResult
            {
                InvoiceId = invoiceId,
                CountDecremented = decremented,
                RequiresReview = !collecting,
                Message = decremented
                    ? "فاکتور باطل شد و از شمارندهٔ دورهٔ جاری حذف شد."
                    : "فاکتور باطل شد؛ چون دوره بسته/تکمیل‌شده است، شمارش تاریخی تغییر نکرد و نیازمند بررسی مدیریتی است."
            };
        }
        catch (ApiException)
        {
            await tx.RollbackAsync(ct);
            throw;
        }
        catch
        {
            try { await tx.RollbackAsync(ct); } catch { }
            throw;
        }
    }

    /// <summary>
    /// تأیید صریح یک فاکتور در وضعیت NeedsReview. اگر شمارش آن قبلاً آزاد شده باشد،
    /// به دورهٔ باز (در صورت ظرفیت) اضافه می‌شود؛ در غیر این صورت Held می‌ماند.
    /// برای فاکتور تاریخی، شمارش دوره دست‌نخورده می‌ماند.
    /// </summary>
    public async Task<RevalidateResult> RevalidateInvoiceAsync(long invoiceId, string actor, CancellationToken ct = default)
    {
        actor = CleanActor(actor);
        await using SqlConnection cn = await _db.OpenAsync(ct);
        await using SqlTransaction tx = (SqlTransaction)await cn.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        try
        {
            long fCode; string status; int? roundId;
            using (SqlCommand q = new(@"
SELECT FCode, Status, RoundId FROM dbo.Invoices WITH (UPDLOCK, HOLDLOCK) WHERE InvoiceId=@id;", cn, tx))
            {
                q.Parameters.AddWithValue("@id", invoiceId);
                using SqlDataReader r = await q.ExecuteReaderAsync(ct);
                if (!await r.ReadAsync(ct))
                {
                    await r.CloseAsync();
                    throw new ApiException(404, "invoice_not_found", "فاکتور پیدا نشد.");
                }
                fCode = r.GetInt64(0);
                status = r.GetString(1);
                roundId = r.IsDBNull(2) ? null : r.GetInt32(2);
                await r.CloseAsync();
            }

            if (status != "NeedsReview")
                throw new ApiException(409, "not_in_review", "فقط فاکتور در وضعیت NeedsReview قابل تأیید است.");

            string newStatus;
            int? targetRound = roundId;
            int roundCount;
            bool countChanged = false;
            string message;

            if (roundId.HasValue)
            {
                // در زمان علامت‌گذاری فاکتور تاریخی، شمارنده کم نشده بود؛ فقط وضعیت را برمی‌گردانیم.
                using (SqlCommand upd = new(@"
UPDATE dbo.Invoices SET Status='Valid', ReviewReason=NULL, ReviewEvidence=NULL WHERE InvoiceId=@id", cn, tx))
                {
                    upd.Parameters.AddWithValue("@id", invoiceId);
                    await upd.ExecuteNonQueryAsync(ct);
                }
                RoundRow? rr = await ReadRoundAsync(cn, roundId.Value, tx, ct);
                roundCount = rr?.Count ?? 0;
                newStatus = "Valid";
                message = "فاکتور تأیید شد؛ چون به دورهٔ تاریخی تعلق دارد، شمارش تغییر نکرد.";
            }
            else
            {
                RoundRow? open = await LockOpenRoundAsync(cn, tx, ct);
                if (open != null && open.Count < open.Target)
                {
                    int newCount = open.Count + 1;
                    bool full = newCount >= open.Target;
                    using (SqlCommand upd = new(@"
UPDATE dbo.Invoices SET Status='Valid', RoundId=@rid, ReviewReason=NULL, ReviewEvidence=NULL WHERE InvoiceId=@id", cn, tx))
                    {
                        upd.Parameters.AddWithValue("@rid", open.RoundId);
                        upd.Parameters.AddWithValue("@id", invoiceId);
                        await upd.ExecuteNonQueryAsync(ct);
                    }
                    using (SqlCommand upd = new(@"
UPDATE dbo.Rounds SET [Count]=@c, Status=@st, CompletedAt=@done WHERE RoundId=@id", cn, tx))
                    {
                        upd.Parameters.AddWithValue("@c", newCount);
                        upd.Parameters.AddWithValue("@st", full ? "Completed" : "Collecting");
                        upd.Parameters.Add("@done", SqlDbType.DateTime2).Value = full ? DateTime.UtcNow : DBNull.Value;
                        upd.Parameters.AddWithValue("@id", open.RoundId);
                        await upd.ExecuteNonQueryAsync(ct);
                    }
                    if (full)
                        await AuditAsync(cn, tx, "system", "round.completed", "Round", open.RoundId.ToString(),
                            new { roundId = open.RoundId, count = newCount }, ct);

                    targetRound = open.RoundId;
                    roundCount = newCount;
                    countChanged = true;
                    newStatus = "Valid";
                    message = "فاکتور تأیید شد و به دورهٔ جاری اضافه شد.";
                }
                else
                {
                    using (SqlCommand upd = new(@"
UPDATE dbo.Invoices SET Status='Held', RoundId=NULL, ReviewReason=NULL, ReviewEvidence=NULL WHERE InvoiceId=@id", cn, tx))
                    {
                        upd.Parameters.AddWithValue("@id", invoiceId);
                        await upd.ExecuteNonQueryAsync(ct);
                    }
                    roundCount = open?.Count ?? 0;
                    newStatus = "Held";
                    targetRound = null;
                    message = "دورهٔ باز ظرفیت ندارد؛ فاکتور تأیید و در وضعیت Held نگه داشته شد.";
                }
            }

            await AuditAsync(cn, tx, actor, "invoice.revalidate", "Invoice", invoiceId.ToString(),
                new { invoiceId, fCode, status = newStatus, roundId = targetRound, countChanged }, ct);
            await tx.CommitAsync(ct);
            return new RevalidateResult
            {
                InvoiceId = invoiceId, FCode = fCode, Status = newStatus, RoundId = targetRound,
                RoundCount = roundCount, CountChanged = countChanged, Message = message
            };
        }
        catch (ApiException)
        {
            await tx.RollbackAsync(ct);
            throw;
        }
        catch
        {
            try { await tx.RollbackAsync(ct); } catch { }
            throw;
        }
    }

    public async Task<List<InvoiceRow>> GetInvoicesAsync(int? roundId, string? status, int limit, CancellationToken ct = default)
    {
        limit = Math.Clamp(limit, 1, 500);
        List<string> allowed = new() { "Valid", "Held", "NeedsReview", "OutOfScope", "Voided" };
        if (!string.IsNullOrWhiteSpace(status) && !allowed.Contains(status))
            throw new ApiException(400, "status_invalid", "وضعیت نامعتبر است.");

        await using SqlConnection cn = await _db.OpenAsync(ct);
        List<InvoiceRow> list = new();
        using SqlCommand cmd = new(@"
SELECT TOP (@n) InvoiceId, FCode, FCodeC, FDate, Jalali, FTime, UserCode, Cashier, Gross, Points,
       Status, RoundId, Mobile, VoidReason, ReceivedAt, ReviewReason, ReviewEvidence
FROM dbo.Invoices
WHERE (@rid IS NULL OR RoundId = @rid) AND (@st IS NULL OR Status = @st)
ORDER BY InvoiceId DESC;", cn);
        cmd.Parameters.AddWithValue("@n", limit);
        cmd.Parameters.AddWithValue("@rid", (object?)roundId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@st", (object?)NullIfEmpty(status) ?? DBNull.Value);
        using SqlDataReader r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            list.Add(new InvoiceRow
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
            });
        }
        return list;
    }

    // ------------------------------------------------------------------
    //  کمکی‌ها
    // ------------------------------------------------------------------
    private string DecideStatus(IngestInvoice inv, out string? note)
    {
        note = null;
        if (inv.FType != 2)
        {
            note = "نوع سند فروش نیست (F_Type <> 2).";
            return "OutOfScope";
        }
        if (inv.Gross <= 0)
        {
            note = "مبلغ فاکتور صفر یا منفی است.";
            return "OutOfScope";
        }
        if (inv.FDate < _opt.StartDate)
        {
            note = "فاکتور پیش از تاریخ شروع کمپین است.";
            return "OutOfScope";
        }
        if (string.Equals(inv.Status, "review", StringComparison.OrdinalIgnoreCase))
        {
            note = "عامل همگام‌سازی این فاکتور را مشکوک علامت زده است.";
            return "NeedsReview";
        }
        if (inv.Net < 0)
        {
            note = "مبلغ خالص منفی است.";
            return "NeedsReview";
        }
        return "Valid";
    }

    private int ComputePoints(decimal gross, decimal discount, string mobile)
    {
        if (mobile.Length == 0) return 0;
        decimal baseAmount = string.Equals(_opt.PointsBase, "net", StringComparison.OrdinalIgnoreCase)
            ? Math.Max(0, gross - discount)
            : gross;
        decimal threshold = _opt.PointsPerAmountToman * 10m; // ریال
        if (threshold <= 0 || baseAmount <= 0) return 0;
        decimal n = Math.Floor(baseAmount / threshold);
        if (n < 0) return 0;
        if (n > int.MaxValue) return int.MaxValue;
        return (int)n;
    }

    private static string CleanActor(string? actor)
    {
        string a = string.IsNullOrWhiteSpace(actor) ? "admin" : actor.Trim();
        return a.Length > 100 ? a[..100] : a;
    }

    private static string CleanReviewReason(string? raw)
    {
        string r = (raw ?? "").Trim().ToLowerInvariant();
        if (r == "missing" || r == "voided" || r == "invalid" || r == "source") return r;
        return "";
    }

    private static string Trim400(string? raw)
    {
        string t = (raw ?? "").Trim();
        return t.Length > 400 ? t[..400] : t;
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private sealed class RoundRow
    {
        public int RoundId; public int RoundNo; public string Status = "";
        public int Target; public int Count; public DateTime StartedAt;
        public DateTime? CompletedAt; public DateTime? ClosedAt;
    }

    private sealed class ExistingRow
    {
        public long InvoiceId; public string? Mobile; public string Status = ""; public int? RoundId;
    }

    private static RoundRow MapRound(SqlDataReader r) => new()
    {
        RoundId = r.GetInt32(0),
        RoundNo = r.GetInt32(1),
        Status = r.GetString(2),
        Target = r.GetInt32(3),
        Count = r.GetInt32(4),
        StartedAt = r.GetDateTime(5),
        CompletedAt = r.IsDBNull(6) ? null : r.GetDateTime(6),
        ClosedAt = r.IsDBNull(7) ? null : r.GetDateTime(7)
    };

    private static async Task<RoundRow?> ReadRoundAsync(SqlConnection cn, int? id, CancellationToken ct)
        => await ReadRoundAsync(cn, id, null, ct);

    private static async Task<RoundRow?> ReadRoundAsync(SqlConnection cn, int? id, SqlTransaction? tx, CancellationToken ct)
    {
        string sql = id.HasValue
            ? @"SELECT RoundId, RoundNo, Status, Target, [Count], StartedAt, CompletedAt, ClosedAt
                FROM dbo.Rounds WHERE RoundId=@id"
            : @"SELECT TOP 1 RoundId, RoundNo, Status, Target, [Count], StartedAt, CompletedAt, ClosedAt
                FROM dbo.Rounds ORDER BY CASE WHEN Status='Collecting' THEN 0 ELSE 1 END, RoundNo DESC";
        using SqlCommand cmd = new(sql, cn, tx);
        if (id.HasValue) cmd.Parameters.AddWithValue("@id", id.Value);
        using SqlDataReader r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? MapRound(r) : null;
    }

    private static async Task<RoundRow?> LockOpenRoundAsync(SqlConnection cn, SqlTransaction tx, CancellationToken ct)
    {
        using SqlCommand cmd = new(@"
SELECT TOP 1 RoundId, RoundNo, Status, Target, [Count], StartedAt, CompletedAt, ClosedAt
FROM dbo.Rounds WITH (UPDLOCK, HOLDLOCK)
WHERE Status='Collecting' ORDER BY RoundNo DESC;", cn, tx);
        using SqlDataReader r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? MapRound(r) : null;
    }

    private async Task<ExistingRow?> FindInvoiceAsync(SqlConnection cn, SqlTransaction tx, long fCode, CancellationToken ct)
    {
        using SqlCommand cmd = new(@"
SELECT InvoiceId, Mobile, Status, RoundId FROM dbo.Invoices WITH (UPDLOCK, HOLDLOCK)
WHERE SourceId=@src AND FCode=@f;", cn, tx);
        cmd.Parameters.AddWithValue("@src", _opt.SourceId);
        cmd.Parameters.AddWithValue("@f", fCode);
        using SqlDataReader r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        return new ExistingRow
        {
            InvoiceId = r.GetInt64(0),
            Mobile = r.IsDBNull(1) ? null : r.GetString(1),
            Status = r.GetString(2),
            RoundId = r.IsDBNull(3) ? null : r.GetInt32(3)
        };
    }

    private static async Task<RoundInfo?> ReadCurrentRoundInfoAsync(SqlConnection cn, SqlTransaction tx, CancellationToken ct)
    {
        RoundRow? row;
        using (SqlCommand q = new(@"
SELECT TOP 1 RoundId, RoundNo, Status, Target, [Count], StartedAt, CompletedAt, ClosedAt
FROM dbo.Rounds ORDER BY CASE WHEN Status='Collecting' THEN 0 ELSE 1 END, RoundNo DESC;", cn, tx))
        using (SqlDataReader r = await q.ExecuteReaderAsync(ct))
        {
            row = await r.ReadAsync(ct) ? MapRound(r) : null;
        }
        if (row == null) return null;
        int held;
        using (SqlCommand q = new("SELECT COUNT(*) FROM dbo.Invoices WHERE Status='Held'", cn, tx))
            held = Convert.ToInt32(await q.ExecuteScalarAsync(ct));
        return new RoundInfo
        {
            RoundId = row.RoundId, RoundNo = row.RoundNo, Status = row.Status, Target = row.Target,
            Count = row.Count, Remaining = row.Target - row.Count, Held = held,
            StartedAt = row.StartedAt, CompletedAt = row.CompletedAt, ClosedAt = row.ClosedAt
        };
    }

    private async Task<RoundInfo> ToInfoAsync(SqlConnection cn, RoundRow row, CancellationToken ct)
    {
        int held;
        if (cn.State != ConnectionState.Open) await cn.OpenAsync(ct);
        using (SqlCommand q = new("SELECT COUNT(*) FROM dbo.Invoices WHERE Status='Held'", cn))
            held = Convert.ToInt32(await q.ExecuteScalarAsync(ct));
        return new RoundInfo
        {
            RoundId = row.RoundId, RoundNo = row.RoundNo, Status = row.Status, Target = row.Target,
            Count = row.Count, Remaining = row.Target - row.Count, Held = held,
            StartedAt = row.StartedAt, CompletedAt = row.CompletedAt, ClosedAt = row.ClosedAt
        };
    }

    private static async Task AuditAsync(SqlConnection cn, SqlTransaction tx, string actor, string action,
        string entity, string? entityId, object details, CancellationToken ct)
    {
        using SqlCommand cmd = new(@"
INSERT INTO dbo.AuditLog(At, Actor, Action, Entity, EntityId, Details)
VALUES(SYSUTCDATETIME(), @actor, @action, @entity, @id, @details);", cn, tx);
        cmd.Parameters.AddWithValue("@actor", actor);
        cmd.Parameters.AddWithValue("@action", action);
        cmd.Parameters.AddWithValue("@entity", entity);
        cmd.Parameters.AddWithValue("@id", (object?)entityId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@details", JsonSerializer.Serialize(details, Json));
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
