using System.Text.Json.Serialization;

namespace EbiCampaign.Api;

public sealed class IngestRequest
{
    public string RequestId { get; set; } = "";
    public string SourceId { get; set; } = "";
    public List<IngestInvoice> Invoices { get; set; } = new();
}

public sealed class IngestInvoice
{
    public long FCode { get; set; }
    public long FCodeC { get; set; }
    public int FType { get; set; } = 2;
    public DateOnly FDate { get; set; }
    public string? Jalali { get; set; }
    public string? FTime { get; set; }
    public int UserCode { get; set; }
    public string? Cashier { get; set; }
    public decimal Gross { get; set; }
    public decimal Discount { get; set; }
    public decimal Net { get; set; }
    public string? CustomerCode { get; set; }
    public string? Mobile { get; set; }
    /// <summary>"valid" (پیش‌فرض) یا "review" برای فاکتور مشکوک.</summary>
    public string? Status { get; set; }
    /// <summary>missing | voided | invalid — دلیل بررسی از عامل صندوق.</summary>
    public string? ReviewReason { get; set; }
    /// <summary>شواهد کوتاه تطبیق (منبع، پنجره، روز، نوع) برای حسابرسی.</summary>
    public string? ReviewEvidence { get; set; }
}

public sealed class IngestResponse
{
    public string RequestId { get; set; } = "";
    public int Accepted { get; set; }
    public int Duplicates { get; set; }
    public int Updated { get; set; }
    public int Held { get; set; }
    public int NeedsReview { get; set; }
    public int OutOfScope { get; set; }
    public int Conflicts { get; set; }
    public RoundInfo? Round { get; set; }
    public List<InvoiceResult> Results { get; set; } = new();
    public bool Cached { get; set; }
}

public sealed class InvoiceResult
{
    public long FCode { get; set; }
    /// <summary>accepted | duplicate | updated | held | review | out_of_scope | conflict</summary>
    public string Result { get; set; } = "";
    public long? InvoiceId { get; set; }
    public string? Status { get; set; }
    public int? RoundId { get; set; }
    public string? Message { get; set; }
}

public sealed class RoundInfo
{
    public int RoundId { get; set; }
    public int RoundNo { get; set; }
    public string Status { get; set; } = "";
    public int Target { get; set; }
    public int Count { get; set; }
    public int Remaining { get; set; }
    public int Held { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public DateTime? ClosedAt { get; set; }
}

public sealed class AdminActionRequest
{
    public string? Actor { get; set; }
}

public sealed class VoidRequest
{
    public string? Reason { get; set; }
    public string? Actor { get; set; }
}

public sealed class AssignHeldRequest
{
    public int RoundId { get; set; }
    public int Limit { get; set; } = 1000;
    public string? Actor { get; set; }
}

public sealed class HeldInvoice
{
    public long InvoiceId { get; set; }
    public long FCode { get; set; }
    public long FCodeC { get; set; }
    public DateOnly FDate { get; set; }
    public string? Jalali { get; set; }
    public int UserCode { get; set; }
    public string? Cashier { get; set; }
    public decimal Gross { get; set; }
    public int Points { get; set; }
    public string? Mobile { get; set; }
    public string Status { get; set; } = "";
}

public sealed class InvoiceRow
{
    public long InvoiceId { get; set; }
    public long FCode { get; set; }
    public long FCodeC { get; set; }
    public DateOnly FDate { get; set; }
    public string? Jalali { get; set; }
    public string? FTime { get; set; }
    public int UserCode { get; set; }
    public string? Cashier { get; set; }
    public decimal Gross { get; set; }
    public int Points { get; set; }
    public string Status { get; set; } = "";
    public int? RoundId { get; set; }
    public string? Mobile { get; set; }
    public string? VoidReason { get; set; }
    public string? ReviewReason { get; set; }
    public string? ReviewEvidence { get; set; }
    public DateTime ReceivedAt { get; set; }
}

public sealed class RevalidateResult
{
    public long InvoiceId { get; set; }
    public long FCode { get; set; }
    public string Status { get; set; } = "";
    public int? RoundId { get; set; }
    public int RoundCount { get; set; }
    public bool CountChanged { get; set; }
    public string Message { get; set; } = "";
}

public sealed class VoidResult
{
    public long InvoiceId { get; set; }
    public bool AlreadyVoided { get; set; }
    public bool CountDecremented { get; set; }
    public bool RequiresReview { get; set; }
    public string Message { get; set; } = "";
}

// ---------------- مسیر تصمیم مدیر (بند ۴.۲) ----------------

public sealed class DecisionRequest
{
    /// <summary>valid = تأیید اعتبار فاکتور، void = تأیید ابطال فاکتور.</summary>
    public string? Verdict { get; set; }
    public string? Reason { get; set; }
    public string? Actor { get; set; }
    /// <summary>کلید ایدِمپوتنسی تصمیم؛ تکرار همان کلید تصمیم دوباره اجرا نمی‌شود.</summary>
    public string? DecisionKey { get; set; }
    /// <summary>مسیر ویژهٔ حسابرسی‌شده برای دوره‌ای که نتیجهٔ قرعه‌کشی آن نهایی شده است.</summary>
    public bool Force { get; set; }
}

public sealed class DecisionResult
{
    public long InvoiceId { get; set; }
    public long FCode { get; set; }
    public string Verdict { get; set; } = "";
    public string Status { get; set; } = "";
    public int? RoundId { get; set; }
    public int RoundCount { get; set; }
    public bool CountChanged { get; set; }
    public bool AlreadyDecided { get; set; }
    public bool Forced { get; set; }
    public bool FinalizedRound { get; set; }
    public string Message { get; set; } = "";
}

public sealed class DecisionRow
{
    public long DecisionId { get; set; }
    public string Verdict { get; set; } = "";
    public string Basis { get; set; } = "";
    public string PrevStatus { get; set; } = "";
    public string NewStatus { get; set; } = "";
    public int? PrevRoundId { get; set; }
    public int? NewRoundId { get; set; }
    public int? CountBefore { get; set; }
    public int? CountAfter { get; set; }
    public bool CountEffected { get; set; }
    public bool RoundFinalized { get; set; }
    public bool Forced { get; set; }
    public string Actor { get; set; } = "";
    public string? Reason { get; set; }
    public string? Evidence { get; set; }
    public DateTime DecidedAt { get; set; }
}

public sealed class InvoiceDetail
{
    public InvoiceRow Invoice { get; set; } = new();
    public List<DecisionRow> Decisions { get; set; } = new();
    public bool RoundFinalized { get; set; }
    public int? LotteryRunId { get; set; }
}

// ---------------- موتور قرعه‌کشی (بند ۵) ----------------

public sealed class PrepareLotteryRequest
{
    public string? Actor { get; set; }
    /// <summary>تأیید آگاهانهٔ وجود فاکتورهای بررسی‌نشده؛ آن‌ها در مجموعهٔ ورودی قرار نمی‌گیرند.</summary>
    public bool AcknowledgeReviews { get; set; }
}

public sealed class LotteryEntryInfo
{
    public int EntryNo { get; set; }
    public long InvoiceId { get; set; }
    public long FCode { get; set; }
    public DateOnly FDate { get; set; }
    public string? Mobile { get; set; }
    public int Points { get; set; }
    public long Weight { get; set; }
    public string Eligibility { get; set; } = "";
    public string? BaleStatus { get; set; }
    public string? BaleEvidence { get; set; }
    public DateTime? BaleCheckedAt { get; set; }
}

public sealed class LotteryRunInfo
{
    public int RunId { get; set; }
    public int RoundId { get; set; }
    public int RoundNo { get; set; }
    public string Status { get; set; } = "";
    public string Algorithm { get; set; } = "";
    public string EntriesHash { get; set; } = "";
    public string CommitmentHash { get; set; } = "";
    public string? SeedHex { get; set; }
    public int EntryCount { get; set; }
    public int EligibleCount { get; set; }
    public long TotalWeight { get; set; }
    public int AcknowledgedReviews { get; set; }
    public string? ReviewReason { get; set; }
    public bool ResultsQuestioned { get; set; }
    public long? WinnerInvoiceId { get; set; }
    public long? WinnerFCode { get; set; }
    public int? WinnerEntryNo { get; set; }
    public string? WinnerMobile { get; set; }
    public string? WinnerCustomerCode { get; set; }
    public int? WinnerPoints { get; set; }
    public long? WinnerDrawValue { get; set; }
    public long? WinnerTotalWeight { get; set; }
    public string PreparedBy { get; set; } = "";
    public DateTime PreparedAt { get; set; }
    public DateTime? SelectedAt { get; set; }
    public string? FinalizedBy { get; set; }
    public DateTime? FinalizedAt { get; set; }
    /// <summary>هش عمومی ورودی‌ها (مبنای تعهد و بازتولید عمومی).</summary>
    public string? PublicEntriesHash { get; set; }
    /// <summary>تأیید صریح مدیر برای انتشار عمومی نتیجه؛ تا نبود آن، خروجی عمومی برنده ندارد.</summary>
    public bool PublishApproved { get; set; }
    public string? PublishApprovedBy { get; set; }
    public DateTime? PublishApprovedAt { get; set; }
    /// <summary>نام نمایشی برنده در سایت؛ فقط همان چیزی که جازهٔ انتشار دارد.</summary>
    public string? PublishedDisplayName { get; set; }
    public string? WinnerImageUrl { get; set; }
    public bool WinnerImageApproved { get; set; }
    public DateTime? WinnerImageUpdatedAt { get; set; }
    public List<LotteryEntryInfo>? Entries { get; set; }
    /// <summary>Seed کامل فقط داخل سرور استفاده می‌شود و در پاسخ API منتشر نمی‌شود.</summary>
    [JsonIgnore]
    public string? FullSeedHex { get; set; }
    /// <summary>شمارهٔ کامل برنده؛ فقط برای مسیر مجاز و حسابرسی‌شدهٔ تماس با برنده پر می‌شود.</summary>
    [JsonIgnore]
    public string? WinnerMobileFull { get; set; }
}

public sealed class LotteryVerifyResult
{
    public int RunId { get; set; }
    public string Algorithm { get; set; } = "";
    public bool SeedRevealed { get; set; }
    public bool EntriesHashMatches { get; set; }
    /// <summary>هش عمومی بازتولیدشده از Snapshot و Seed افشاشده.</summary>
    public string? PublicEntriesHash { get; set; }
    public bool PublicEntriesHashMatches { get; set; }
    public bool CommitmentMatches { get; set; }
    public bool WinnerReproduces { get; set; }
    public long? ExpectedWinnerFCode { get; set; }
    public long? RecomputedWinnerFCode { get; set; }
    public int? RecomputedEntryNo { get; set; }
    public string Note { get; set; } = "";
}

// ---------------- API عمومی (بند ۵ و ۶) ----------------
// هیچ‌کدام از این DTOها دادهٔ خصوصی (موبایل کامل، شمارهٔ فاکتور، شناسهٔ مشتری،
// شواهد بررسی، وضعیت بله) را حمل نمی‌کنند.

public sealed class PublicCampaignStatus
{
    /// <summary>Collecting | DrawPending | RequiresReview | ResultPublished | NextRoundPending</summary>
    public string State { get; set; } = "";
    public string StateText { get; set; } = "";
    public int? RoundNo { get; set; }
    public string RoundStatus { get; set; } = "";
    public int Count { get; set; }
    public int Target { get; set; }
    public int Remaining { get; set; }
    public double ProgressPercent { get; set; }
    public long PrizeToman { get; set; }
    public bool AcceptingNewInvoices { get; set; }
    /// <summary>دورهٔ تکمیل‌شده با شروع‌نشدن دورهٔ بعد؛ شمارنده صفر نمی‌شود.</summary>
    public bool AwaitingManagerStart { get; set; }
    public PublicWinnerCard? LastResult { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public sealed class PublicWinnerCard
{
    public int RoundNo { get; set; }
    public DateTime? DrawnAt { get; set; }
    public string? PublishedAt { get; set; }
    public long PrizeToman { get; set; }
    /// <summary>شمارهٔ سانسورشده؛ هرگز شمارهٔ کامل نیست.</summary>
    public string MobileMasked { get; set; } = "";
    public string? DisplayName { get; set; }
    public string? ImageUrl { get; set; }
    public int EntryCount { get; set; }
    public int EligibleCount { get; set; }
    public long TotalWeight { get; set; }
    public string VerificationStatus { get; set; } = "";
}

public sealed class PublicWinnersPage
{
    public int Page { get; set; }
    public int PerPage { get; set; }
    public int Total { get; set; }
    public bool HasMore { get; set; }
    public List<PublicWinnerCard> Winners { get; set; } = new();
}

/// <summary>سند عمومی قرعه‌کشی: قابل بازتولید برای ممیز مستقل و بدون دادهٔ شخصی.</summary>
public sealed class PublicLotteryReceipt
{
    public int RunId { get; set; }
    public int RoundNo { get; set; }
    public string Algorithm { get; set; } = "";
    public string Status { get; set; } = "";
    public int EntryCount { get; set; }
    public int EligibleCount { get; set; }
    public long TotalWeight { get; set; }
    /// <summary>هش عمومی ورودی‌ها؛ مبنای تعهد و زمینهٔ انتخاب.</summary>
    public string PublicEntriesHash { get; set; } = "";
    public string CommitmentHash { get; set; } = "";
    public string? SeedHex { get; set; }
    public bool SeedRevealed { get; set; }
    public long? DrawValue { get; set; }
    public int? WinnerEntryNo { get; set; }
    public string? WinnerPseudonym { get; set; }
    public string MobileMasked { get; set; } = "";
    public string? DisplayName { get; set; }
    public string? ImageUrl { get; set; }
    public DateTime? PreparedAt { get; set; }
    public DateTime? DrawnAt { get; set; }
    public DateTime? FinalizedAt { get; set; }
    public DateTime? PublishedAt { get; set; }
    public bool Verified { get; set; }
    public string VerificationNote { get; set; } = "";
}

public sealed class PublicEntry
{
    public int EntryNo { get; set; }
    public string Pseudonym { get; set; } = "";
    public string Eligibility { get; set; } = "";
    public long Weight { get; set; }
    public bool Winner { get; set; }
}

public sealed class PublicEntriesPage
{
    public int RunId { get; set; }
    public string PublicEntriesHash { get; set; } = "";
    public int Page { get; set; }
    public int PerPage { get; set; }
    public int Total { get; set; }
    public bool HasMore { get; set; }
    public List<PublicEntry> Entries { get; set; } = new();
}

public sealed class PublicVerifyResult
{
    public int RunId { get; set; }
    public int RoundNo { get; set; }
    public string Algorithm { get; set; } = "";
    public bool SeedRevealed { get; set; }
    public bool PublicEntriesHashMatches { get; set; }
    public bool CommitmentMatches { get; set; }
    public bool WinnerReproduces { get; set; }
    public int? WinnerEntryNo { get; set; }
    public string? WinnerPseudonym { get; set; }
    public string Note { get; set; } = "";
}

/// <summary>درخواست تأیید انتشار عمومی نتیجه.</summary>
public sealed class PublishRequest
{
    public string? Actor { get; set; }
    public string? DisplayName { get; set; }
    public string? ImageUrl { get; set; }
    public bool ImageApproved { get; set; }
    /// <summary>تأیید صریح مدیر برای انتشار؛ بدون آن هیچ انتشار عمومی انجام نمی‌شود.</summary>
    public bool Confirm { get; set; }
}

public sealed class WinnerImageRequest
{
    public string? Actor { get; set; }
    public string? ImageUrl { get; set; }
    public bool Approved { get; set; }
}

public sealed class ApiError
{
    public string Code { get; set; } = "";
    public string Message { get; set; } = "";
}

public sealed class ApiException : Exception
{
    public int Status { get; }
    public string Code { get; }

    public ApiException(int status, string code, string message) : base(message)
    {
        Status = status;
        Code = code;
    }
}
