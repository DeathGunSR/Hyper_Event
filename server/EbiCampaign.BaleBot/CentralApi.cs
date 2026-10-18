using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace EbiCampaign.BaleBot;

/// <summary>نتیجهٔ ثبت‌نام در سرویس مرکزی.</summary>
public sealed record RegisterOutcome(bool Ok, string? Error, string? MobileMasked);

/// <summary>وضعیت عضویت از دید سرویس مرکزی.</summary>
public sealed record StatusOutcome(bool Registered, string Status, string StatusText, string Guidance,
    string? MobileMasked);

/// <summary>
/// ارتباط ربات با سرویس مرکزی؛ همهٔ درخواست‌ها با HMAC عامل امضا می‌شوند تا ربات
/// نتواند بدون کلید مشترک به مسیرهای عامل دسترسی پیدا کند.
/// </summary>
public interface ICentralApi
{
    Task<RegisterOutcome> RegisterAsync(string mobile, long baleUserId, string firstName, CancellationToken ct = default);
    Task<string?> LookupMobileAsync(long baleUserId, CancellationToken ct = default);
    Task<StatusOutcome> StatusAsync(string mobile, CancellationToken ct = default);
}

public sealed class CentralApi : ICentralApi
{
    private readonly HttpClient _http;
    private readonly BotOptions _opt;
    private readonly ILogger<CentralApi> _log;

    public CentralApi(HttpClient http, BotOptions opt, ILogger<CentralApi> log)
    {
        _http = http;
        _opt = opt;
        _log = log;
        _http.BaseAddress ??= new Uri(opt.CentralBase.TrimEnd('/') + "/");
        _http.Timeout = TimeSpan.FromSeconds(15);
    }

    public Task<RegisterOutcome> RegisterAsync(string mobile, long baleUserId, string firstName,
        CancellationToken ct = default)
        => PostAsync<RegisterOutcome>("api/v1/agent/bale/register", new
        {
            mobile,
            baleUserId,
            firstName,
            contactShared = true
        }, ct, fallback: new RegisterOutcome(false, "network_error", null));

    public async Task<string?> LookupMobileAsync(long baleUserId, CancellationToken ct = default)
    {
        LookupOutcome outcome = await PostAsync<LookupOutcome>("api/v1/agent/bale/lookup",
            new { baleUserId }, ct, fallback: new LookupOutcome(false, null));
        return outcome.Registered ? outcome.Mobile : null;
    }

    public Task<StatusOutcome> StatusAsync(string mobile, CancellationToken ct = default)
        => PostAsync<StatusOutcome>("api/v1/agent/bale/status", new { mobile }, ct,
            fallback: new StatusOutcome(false, "unknown", "نامعلوم",
                "ارتباط با سرویس مرکزی برقرار نشد؛ این وضعیت به‌معنای عضویت یا عدم عضویت نیست.", null));

    private async Task<T> PostAsync<T>(string path, object payload, CancellationToken ct, T fallback)
    {
        try
        {
            byte[] body = JsonSerializer.SerializeToUtf8Bytes(payload);
            string timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
            string bodyHash = Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant();
            string canonical = _opt.AgentId + "\n" + timestamp + "\nPOST\n/" + path + "\n" + bodyHash;
            string signature = Hmac(_opt.AgentSecret, canonical);

            using HttpRequestMessage req = new(HttpMethod.Post, path);
            req.Headers.Add("X-Agent-Id", _opt.AgentId);
            req.Headers.Add("X-Timestamp", timestamp);
            req.Headers.Add("X-Signature", signature);
            req.Content = new ByteArrayContent(body);
            req.Content.Headers.ContentType = new("application/json");

            using HttpResponseMessage res = await _http.SendAsync(req, ct);
            if (!res.IsSuccessStatusCode)
            {
                _log.LogWarning("central {Path} failed with HTTP {Status}", path, (int)res.StatusCode);
                return fallback;
            }
            T? parsed = await res.Content.ReadFromJsonAsync<T>(cancellationToken: ct);
            return parsed ?? fallback;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _log.LogWarning("central {Path} unreachable: {Type}", path, ex.GetType().Name);
            return fallback;
        }
    }

    private static string Hmac(string secret, string canonical)
    {
        using HMACSHA256 h = new(Encoding.UTF8.GetBytes(secret));
        return Convert.ToHexString(h.ComputeHash(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private sealed record LookupOutcome(bool Registered, string? Mobile);
}
