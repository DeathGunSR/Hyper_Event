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
