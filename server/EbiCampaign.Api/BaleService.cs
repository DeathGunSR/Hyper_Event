using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.SqlClient;

namespace EbiCampaign.Api;

/// <summary>پیکربندی اتصال بله؛ مقادیر محرمانه فقط از پیکربندی/متغیر محیطی خوانده می‌شوند.</summary>
public sealed class BaleOptions
{
    public string Token { get; init; } = "";
    public string Channel { get; init; } = "";
    public string ApiBase { get; init; } = "https://tapi.bale.ai";
    public bool IsConfigured => !string.IsNullOrWhiteSpace(Token) && !string.IsNullOrWhiteSpace(Channel);

    public static BaleOptions From(IConfiguration cfg) => new()
    {
        Token = (cfg["Bale:Token"] ?? "").Trim(),
        Channel = (cfg["Bale:Channel"] ?? "").Trim(),
        ApiBase = string.IsNullOrWhiteSpace(cfg["Bale:ApiBase"]) ? "https://tapi.bale.ai" : cfg["Bale:ApiBase"]!.Trim()
    };
}

/// <summary>پاسخ خام getChatMember برای تفکیک «عضو»، «غیرعضو» و «نامعلوم».</summary>
public sealed record BaleMemberResult(string Status, bool IsMember, string Evidence)
{
    public static BaleMemberResult Unknown(string evidence) => new("unknown", false, evidence);
}

/// <summary>فراخوانی مستقیم Bot API بله؛ آزمون‌ها می‌توانند پیاده‌سازی جعلی تزریق کنند.</summary>
public interface IBaleBotApi
{
    Task<BaleMemberResult> GetChatMemberAsync(long baleUserId, CancellationToken ct = default);
    Task<bool> SendMessageAsync(long chatId, string text, CancellationToken ct = default);
}

/// <summary>
/// کلاینت واقعی Bot API بله. آدرس هر متد «/bot{token}/{method}» است و توکن هرگز
/// در لاگ یا پاسخ عمومی چاپ نمی‌شود.
/// </summary>
public sealed class BaleBotApi : IBaleBotApi
{
    private readonly HttpClient _http;
    private readonly BaleOptions _opt;
    private readonly ILogger<BaleBotApi> _log;

    public BaleBotApi(HttpClient http, BaleOptions opt, ILogger<BaleBotApi> log)
    {
        _http = http;
        _opt = opt;
        _log = log;
        _http.BaseAddress ??= new Uri(opt.ApiBase);
        _http.Timeout = TimeSpan.FromSeconds(10);
    }

    public async Task<BaleMemberResult> GetChatMemberAsync(long baleUserId, CancellationToken ct = default)
    {
        if (!_opt.IsConfigured) return BaleMemberResult.Unknown("not-configured");
        try
        {
            using HttpResponseMessage res = await _http.PostAsJsonAsync(
                $"/bot{_opt.Token}/getChatMember",
                new { chat_id = _opt.Channel, user_id = baleUserId }, ct);

            string body = await res.Content.ReadAsStringAsync(ct);
            if (!res.IsSuccessStatusCode)
            {
                // ۴۰۰/۴۰۳ یعنی ربات در کانال مدیر نیست یا شناسهٔ کاربر نامعتبر است؛
                // هیچ‌کدام «موفق» تلقی نمی‌شوند.
                return BaleMemberResult.Unknown($"http_{(int)res.StatusCode}");
            }

            BaleChatMemberResponse? parsed = JsonSerializer.Deserialize<BaleChatMemberResponse>(body, Json);
            if (parsed is null || parsed.Ok == false || parsed.Result is null)
                return BaleMemberResult.Unknown(parsed?.Description ?? "empty_response");

            string status = (parsed.Result.Status ?? "").ToLowerInvariant();
            bool member = status switch
            {
                "creator" or "administrator" or "member" => true,
                "restricted" => parsed.Result.IsMember == true,
                _ => false
            };
            string evidence = "status=" + (status.Length == 0 ? "unknown" : status)
                + ";is_member=" + (parsed.Result.IsMember == true ? "true" : "false");
            return new BaleMemberResult(status.Length == 0 ? "unknown" : status, member, evidence);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            // خطا یا Timeout هرگز به‌معنای عضویت نیست.
            _log.LogWarning("Bale getChatMember failed: {Type}", ex.GetType().Name);
            return BaleMemberResult.Unknown("network_error");
        }
    }

    public async Task<bool> SendMessageAsync(long chatId, string text, CancellationToken ct = default)
    {
        if (!_opt.IsConfigured) return false;
        try
        {
            using HttpResponseMessage res = await _http.PostAsJsonAsync(
                $"/bot{_opt.Token}/sendMessage",
                new { chat_id = chatId, text }, ct);
            return res.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _log.LogWarning("Bale sendMessage failed: {Type}", ex.GetType().Name);
            return false;
        }
    }

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private sealed class BaleChatMemberResponse
    {
        [JsonPropertyName("ok")] public bool? Ok { get; init; }
        [JsonPropertyName("description")] public string? Description { get; init; }
        [JsonPropertyName("result")] public BaleChatMember? Result { get; init; }
    }

    private sealed class BaleChatMember
    {
        [JsonPropertyName("status")] public string? Status { get; init; }
        [JsonPropertyName("is_member")] public bool? IsMember { get; init; }
    }
}

/// <summary>یک ثبت‌نام بله؛ هویت حساب بله مستقل از شماره و از فاکتور است.</summary>
public sealed record BaleRegistration(
    long BaleUserId,
    string MobileMasked,
    string FirstName,
    bool ContactShared,
    DateTime RegisteredAt,
    DateTime? VerifiedAt,
    DateTime? LastCheckAt,
    string LastStatus,
    string LastEvidence)
{
    public string StatusText => LastStatus switch
    {
        "member" => "عضو کانال",
        "not_member" => "عضو کانال نیست",
        _ => "نامعلوم"
    };
}

public sealed record BaleRegisterRequest(string? Mobile, long BaleUserId, string? FirstName, bool ContactShared);
public sealed record BaleStatusRequest(string? Mobile);
public sealed record BaleLookupRequest(long BaleUserId);
public sealed record BaleRegistrationView(long BaleUserId, string FirstName, bool ContactShared,
    DateTime RegisteredAt, DateTime? VerifiedAt);
public sealed record BaleStatusResult(
    bool Registered,
    string MobileMasked,
    string Status,
    string StatusText,
    bool Eligible,
    string Channel,
    DateTime CheckedAt,
    string Guidance);

/// <summary>
/// ثبت‌نام و بررسی عضویت. ثبت‌نام فقط از مسیر امضاشدهٔ عامل (ربات) پذیرفته می‌شود و
/// مالکیت شماره تا زمانی که کاربر «اشتراک‌گذاری شماره» را در بله انجام دهد تأیید نمی‌شود.
/// </summary>
public sealed class BaleService
{
    private readonly Db _db;
    private readonly BaleOptions _opt;
    private readonly IBaleBotApi _api;
    private readonly ILogger<BaleService> _log;

    public BaleService(Db db, BaleOptions opt, IBaleBotApi api, ILogger<BaleService> log)
    {
        _db = db;
        _opt = opt;
        _api = api;
        _log = log;
    }

    public async Task<(BaleRegistration? registration, string? error)> RegisterAsync(
        BaleRegisterRequest req, string agentId, CancellationToken ct = default)
    {
        string mobile = Security.NormalizeMobile(req.Mobile);
        if (mobile.Length == 0)
            return (null, "invalid_mobile");
        if (req.BaleUserId <= 0)
            return (null, "invalid_bale_user");
        if (!req.ContactShared)
            return (null, "contact_not_shared");

        await using SqlConnection cn = await _db.OpenAsync(ct);
        // شمارهٔ دیگری که قبلاً به حساب بلهٔ دیگری وصل شده، بازنویسی نمی‌شود؛ ثبت تکراری رد می‌شود.
        using (SqlCommand dup = new(
            "SELECT BaleUserId FROM dbo.BaleRegistration WHERE Mobile = @m AND BaleUserId <> @u", cn))
        {
            dup.Parameters.AddWithValue("@m", mobile);
            dup.Parameters.AddWithValue("@u", req.BaleUserId);
            object? other = await dup.ExecuteScalarAsync(ct);
            if (other != null)
            {
                await AuditWriter.WriteAsync(cn, null, agentId, "bale.register_conflict", "BaleRegistration",
                    mobile, new { baleUserId = req.BaleUserId, conflictWith = Convert.ToInt64(other) }, ct);
                return (null, "mobile_registered_to_other_account");
            }
        }

        using (SqlCommand cmd = new(@"
MERGE dbo.BaleRegistration AS t
USING (SELECT @u AS BaleUserId) AS s ON t.BaleUserId = s.BaleUserId
WHEN MATCHED THEN UPDATE SET
    Mobile = @m,
    FirstName = @f,
    ContactShared = 1,
    VerifiedAt = SYSUTCDATETIME()
WHEN NOT MATCHED THEN
    INSERT(BaleUserId, Mobile, FirstName, ContactShared, RegisteredAt, VerifiedAt)
    VALUES(@u, @m, @f, 1, SYSUTCDATETIME(), SYSUTCDATETIME());", cn))
        {
            cmd.Parameters.AddWithValue("@u", req.BaleUserId);
            cmd.Parameters.AddWithValue("@m", mobile);
            cmd.Parameters.AddWithValue("@f", (object?)(req.FirstName ?? "") ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await AuditWriter.WriteAsync(cn, null, agentId, "bale.registered", "BaleRegistration",
            mobile, new { baleUserId = req.BaleUserId, contactShared = true }, ct);

        BaleRegistration? reg = await ReadRegistrationAsync(cn, mobile, ct);
        return (reg, null);
    }

    /// <summary>شمارهٔ متصل به یک حساب بله؛ فقط برای عامل امضاشده (ربات) استفاده می‌شود.</summary>
    public async Task<string?> GetMobileByBaleUserAsync(long baleUserId, CancellationToken ct = default)
    {
        if (baleUserId <= 0) return null;
        await using SqlConnection cn = await _db.OpenAsync(ct);
        using SqlCommand cmd = new("SELECT TOP (1) Mobile FROM dbo.BaleRegistration WHERE BaleUserId = @u", cn);
        cmd.Parameters.AddWithValue("@u", baleUserId);
        object? value = await cmd.ExecuteScalarAsync(ct);
        return value == null || value == DBNull.Value ? null : Convert.ToString(value);
    }

    public async Task<BaleStatusResult> GetStatusAsync(string? mobileRaw, string actor, CancellationToken ct = default)
    {
        string mobile = Security.NormalizeMobile(mobileRaw);
        if (mobile.Length == 0)
            return new BaleStatusResult(false, "", "unknown", "نامعلوم", false, ChannelLabel, DateTime.UtcNow,
                "شمارهٔ موبایل معتبر نیست.");

        await using SqlConnection cn = await _db.OpenAsync(ct);
        BaleRegistration? reg = await ReadRegistrationAsync(cn, mobile, ct);
        if (reg is null)
            return new BaleStatusResult(false, Security.MaskMobile(mobile), "unknown", "نامعلوم", false,
                ChannelLabel, DateTime.UtcNow,
                "این شماره در بله ثبت نشده است؛ ابتدا ربات را باز کنید و شمارهٔ خود را با «اشتراک‌گذاری شماره» بفرستید.");

        BaleCheck check = await CheckWithProviderAsync(mobile, reg.BaleUserId, actor, ct);
        bool eligible = check.Status == BaleStatus.Member;
        return new BaleStatusResult(true, Security.MaskMobile(mobile), check.Code,
            eligible ? "عضو کانال" : check.Status == BaleStatus.NotMember ? "عضو کانال نیست" : "نامعلوم",
            eligible, ChannelLabel, check.CheckedAt,
            eligible
                ? "عضویت شما در کانال تأیید شد؛ در صورت تکمیل دوره در قرعه‌کشی شرکت می‌کنید."
                : check.Status == BaleStatus.NotMember
                    ? "برای شرکت در قرعه‌کشی، ابتدا در کانال فروشگاه در بله عضو شوید."
                    : "وضعیت عضویت در این لحظه قابل تأیید نبود؛ این وضعیت به‌معنای عضویت نیست. کمی بعد دوباره بررسی کنید.");
    }

    /// <summary>بررسی عضویت با ثبت شاهد، زمان و اقدام‌کننده.</summary>
    public async Task<BaleCheck> CheckWithProviderAsync(string mobile, long? baleUserId, string actor,
        CancellationToken ct = default)
    {
        BaleCheck check;
        if (baleUserId is null or <= 0)
        {
            check = new BaleCheck(BaleStatus.Unknown, "no-bale-registration", DateTime.UtcNow);
        }
        else
        {
            BaleMemberResult result = await _api.GetChatMemberAsync(baleUserId.Value, ct);
            check = new BaleCheck(ToStatus(result), result.Evidence, DateTime.UtcNow);
        }

        await using SqlConnection cn = await _db.OpenAsync(ct);
        using (SqlCommand cmd = new(@"
INSERT INTO dbo.BaleMembershipCheck(Mobile, BaleUserId, Status, Evidence, CheckedAt)
VALUES(@m, @u, @s, @e, SYSUTCDATETIME());", cn))
        {
            cmd.Parameters.AddWithValue("@m", mobile);
            cmd.Parameters.AddWithValue("@u", (object?)baleUserId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@s", check.Code);
            cmd.Parameters.AddWithValue("@e", check.Evidence);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        using (SqlCommand upd = new(@"
UPDATE dbo.BaleRegistration
SET LastCheckAt = SYSUTCDATETIME(), LastStatus = @s, LastEvidence = @e
WHERE Mobile = @m;", cn))
        {
            upd.Parameters.AddWithValue("@m", mobile);
            upd.Parameters.AddWithValue("@s", check.Code);
            upd.Parameters.AddWithValue("@e", check.Evidence);
            await upd.ExecuteNonQueryAsync(ct);
        }
        await AuditWriter.WriteAsync(cn, null, actor, "bale.membership_checked", "BaleRegistration",
            mobile, new { status = check.Code, evidence = check.Evidence }, ct);
        return check;
    }

    public async Task<IReadOnlyList<BaleRegistration>> ListAsync(int limit, CancellationToken ct = default)
    {
        await using SqlConnection cn = await _db.OpenAsync(ct);
        using SqlCommand cmd = new(@"
SELECT TOP (@l) BaleUserId, Mobile, FirstName, ContactShared, RegisteredAt, VerifiedAt, LastCheckAt,
       ISNULL(LastStatus,''), ISNULL(LastEvidence,'')
FROM dbo.BaleRegistration
ORDER BY RegisteredAt DESC;", cn);
        cmd.Parameters.AddWithValue("@l", Math.Clamp(limit, 1, 500));
        using SqlDataReader r = await cmd.ExecuteReaderAsync(ct);
        List<BaleRegistration> list = new();
        while (await r.ReadAsync(ct))
        {
            list.Add(new BaleRegistration(
                r.GetInt64(0), Security.MaskMobile(r.GetString(1)), r.IsDBNull(2) ? "" : r.GetString(2),
                r.GetBoolean(3), r.GetDateTime(4), r.IsDBNull(5) ? null : r.GetDateTime(5),
                r.IsDBNull(6) ? null : r.GetDateTime(6), r.GetString(7), r.GetString(8)));
        }
        return list;
    }

    public string ChannelLabel => _opt.Channel;

    /// <summary>یکسان‌سازی نتیجهٔ Bot API؛ «نامعلوم» هیچ‌وقت عضو شمرده نمی‌شود.</summary>
    internal static BaleStatus ToStatus(BaleMemberResult result)
    {
        if (result.IsMember) return BaleStatus.Member;
        return result.Status is "left" or "kicked" or "restricted"
            ? BaleStatus.NotMember
            : BaleStatus.Unknown;
    }

    private static async Task<BaleRegistration?> ReadRegistrationAsync(SqlConnection cn, string mobile,
        CancellationToken ct)
    {
        using SqlCommand cmd = new(@"
SELECT TOP (1) BaleUserId, Mobile, FirstName, ContactShared, RegisteredAt, VerifiedAt, LastCheckAt,
       ISNULL(LastStatus,''), ISNULL(LastEvidence,'')
FROM dbo.BaleRegistration WHERE Mobile = @m;", cn);
        cmd.Parameters.AddWithValue("@m", mobile);
        using SqlDataReader r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        return new BaleRegistration(r.GetInt64(0), Security.MaskMobile(r.GetString(1)),
            r.IsDBNull(2) ? "" : r.GetString(2), r.GetBoolean(3), r.GetDateTime(4),
            r.IsDBNull(5) ? null : r.GetDateTime(5), r.IsDBNull(6) ? null : r.GetDateTime(6),
            r.GetString(7), r.GetString(8));
    }
}

/// <summary>
/// ارائه‌دهندهٔ واقعی عضویت برای قرعه‌کشی: شناسهٔ بله از ثبت‌نام امضاشده می‌آید و
/// وضعیت در همان لحظه از Bale دریافت می‌شود. بدون ثبت‌نام یا با خطای شبکه «نامعلوم» است.
/// </summary>
public sealed class BaleMembershipProvider : IBaleMembershipProvider
{
    private readonly Db _db;
    private readonly IBaleBotApi _api;
    private readonly ILogger<BaleMembershipProvider> _log;

    public BaleMembershipProvider(Db db, IBaleBotApi api, ILogger<BaleMembershipProvider> log)
    {
        _db = db;
        _api = api;
        _log = log;
    }

    public string Name => "bale-bot";

    public async Task<BaleCheck> CheckAsync(string mobile, CancellationToken ct = default)
    {
        if (!Security.IsMobile(Security.NormalizeMobile(mobile)))
            return new BaleCheck(BaleStatus.Unknown, "invalid-mobile", DateTime.UtcNow);

        long? userId = null;
        try
        {
            await using SqlConnection cn = await _db.OpenAsync(ct);
            using SqlCommand cmd = new(
                "SELECT TOP (1) BaleUserId FROM dbo.BaleRegistration WHERE Mobile = @m", cn);
            cmd.Parameters.AddWithValue("@m", Security.NormalizeMobile(mobile));
            object? value = await cmd.ExecuteScalarAsync(ct);
            if (value != null && value != DBNull.Value) userId = Convert.ToInt64(value);
        }
        catch (Exception ex) when (ex is SqlException or InvalidOperationException)
        {
            _log.LogWarning("Bale registration lookup failed: {Type}", ex.GetType().Name);
            return new BaleCheck(BaleStatus.Unknown, "registration_lookup_failed", DateTime.UtcNow);
        }

        if (userId is null or <= 0)
            return new BaleCheck(BaleStatus.Unknown, "no-bale-registration", DateTime.UtcNow);

        BaleMemberResult result = await _api.GetChatMemberAsync(userId.Value, ct);
        return new BaleCheck(ToStatus(result), result.Evidence, DateTime.UtcNow);
    }

    /// <summary>«عضو»، «غیرعضو» و «نامعلوم» هرگز با هم یکی نمی‌شوند.</summary>
    internal static BaleStatus ToStatus(BaleMemberResult result)
    {
        if (result.IsMember) return BaleStatus.Member;
        // فقط وضعیت‌هایی که خود بله «عضو نبودن» را قطعی می‌گوید غیرعضو شمرده می‌شوند.
        return result.Status is "left" or "kicked" or "restricted"
            ? BaleStatus.NotMember
            : BaleStatus.Unknown;
    }
}
