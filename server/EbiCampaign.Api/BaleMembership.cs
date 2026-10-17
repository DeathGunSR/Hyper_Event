namespace EbiCampaign.Api;

public enum BaleStatus
{
    Member,
    NotMember,
    Unknown
}

/// <summary>نتیجهٔ بررسی عضویت یک شماره در کانال فروشگاه در بله.</summary>
public sealed record BaleCheck(BaleStatus Status, string Evidence, DateTime CheckedAt)
{
    public string Code => Status switch
    {
        BaleStatus.Member => "member",
        BaleStatus.NotMember => "not_member",
        _ => "unknown"
    };
}

public interface IBaleMembershipProvider
{
    string Name { get; }
    Task<BaleCheck> CheckAsync(string mobile, CancellationToken ct = default);
}

/// <summary>
/// پیش‌فرض سامانه: هیچ اتصالی به بله برقرار نیست، پس وضعیت «نامعلوم» برگردانده می‌شود.
/// نتیجهٔ نامعلوم به‌معنای عضویت نیست؛ با سیاست پیش‌فرض، قرعه‌کشی با ورودی نامعلوم
/// انجام نمی‌شود و دوره در وضعیت «نیازمند بررسی مدیریتی» می‌ماند.
/// با پیکربندی ربات بله، این ارائه‌دهنده جایگزین می‌شود.
/// </summary>
public sealed class UnconfiguredBaleProvider : IBaleMembershipProvider
{
    public string Name => "unconfigured";

    public Task<BaleCheck> CheckAsync(string mobile, CancellationToken ct = default)
        => Task.FromResult(new BaleCheck(BaleStatus.Unknown, "provider-not-configured", DateTime.UtcNow));
}