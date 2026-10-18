using System.Text.Json;
using System.Text.Json.Serialization;

namespace EbiCampaign.BaleBot;

/// <summary>به‌روزرسانی دریافتی از بله (زیرمجموعهٔ فیلدهای موردنیاز).</summary>
public sealed class BaleUpdate
{
    [JsonPropertyName("update_id")] public long UpdateId { get; init; }
    [JsonPropertyName("message")] public BaleMessage? Message { get; init; }
}

public sealed class BaleMessage
{
    [JsonPropertyName("message_id")] public long MessageId { get; init; }
    [JsonPropertyName("chat")] public BaleChat? Chat { get; init; }
    [JsonPropertyName("from")] public BaleUser? From { get; init; }
    [JsonPropertyName("text")] public string? Text { get; init; }
    [JsonPropertyName("contact")] public BaleContact? Contact { get; init; }
}

public sealed class BaleChat
{
    [JsonPropertyName("id")] public long Id { get; init; }
}

public sealed class BaleUser
{
    [JsonPropertyName("id")] public long Id { get; init; }
    [JsonPropertyName("first_name")] public string? FirstName { get; init; }
    [JsonPropertyName("username")] public string? Username { get; init; }
}

public sealed class BaleContact
{
    [JsonPropertyName("phone_number")] public string? PhoneNumber { get; init; }
    [JsonPropertyName("first_name")] public string? FirstName { get; init; }
    [JsonPropertyName("user_id")] public long? UserId { get; init; }
}

/// <summary>
/// کلاینت Bot API بله. آدرس متدها «/bot{token}/{method}» است و توکن هرگز لاگ نمی‌شود.
/// </summary>
public interface IBaleApi
{
    Task<bool> SendMessageAsync(long chatId, string text, BaleKeyboard? keyboard, CancellationToken ct = default);
    Task<IReadOnlyList<BaleUpdate>> GetUpdatesAsync(long offset, int timeoutSeconds, CancellationToken ct = default);
    Task SetWebhookAsync(string url, CancellationToken ct = default);
}

public sealed class BaleApi : IBaleApi
{
    private readonly HttpClient _http;
    private readonly BotOptions _opt;
    private readonly ILogger<BaleApi> _log;

    public BaleApi(HttpClient http, BotOptions opt, ILogger<BaleApi> log)
    {
        _http = http;
        _opt = opt;
        _log = log;
        _http.BaseAddress ??= new Uri(opt.ApiBase);
        _http.Timeout = TimeSpan.FromSeconds(40);
    }

    public async Task<bool> SendMessageAsync(long chatId, string text, BaleKeyboard? keyboard,
        CancellationToken ct = default)
    {
        if (!_opt.HasToken) return false;
        object payload = keyboard == null
            ? new { chat_id = chatId, text }
            : new { chat_id = chatId, text, reply_markup = keyboard.ToPayload() };

        using HttpResponseMessage res = await _http.PostAsJsonAsync(
            $"/bot{_opt.Token}/sendMessage", payload, ct);
        if (!res.IsSuccessStatusCode)
            _log.LogWarning("sendMessage failed with HTTP {Status}", (int)res.StatusCode);
        return res.IsSuccessStatusCode;
    }

    public async Task<IReadOnlyList<BaleUpdate>> GetUpdatesAsync(long offset, int timeoutSeconds,
        CancellationToken ct = default)
    {
        using HttpResponseMessage res = await _http.PostAsJsonAsync(
            $"/bot{_opt.Token}/getUpdates",
            new { offset, timeout = Math.Clamp(timeoutSeconds, 0, 50) }, ct);
        if (!res.IsSuccessStatusCode) return Array.Empty<BaleUpdate>();

        string body = await res.Content.ReadAsStringAsync(ct);
        BaleUpdatesResponse? parsed = JsonSerializer.Deserialize<BaleUpdatesResponse>(body, Json);
        return parsed?.Result ?? (IReadOnlyList<BaleUpdate>)Array.Empty<BaleUpdate>();
    }

    public async Task SetWebhookAsync(string url, CancellationToken ct = default)
    {
        using HttpResponseMessage res = await _http.PostAsJsonAsync(
            $"/bot{_opt.Token}/setWebhook", new { url }, ct);
        if (!res.IsSuccessStatusCode)
            _log.LogWarning("setWebhook failed with HTTP {Status}", (int)res.StatusCode);
    }

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private sealed class BaleUpdatesResponse
    {
        [JsonPropertyName("ok")] public bool? Ok { get; init; }
        [JsonPropertyName("result")] public List<BaleUpdate>? Result { get; init; }
    }
}

/// <summary>صفحه‌کلید پاسخ سریع؛ فقط کلیدهای مجاز ربات.</summary>
public sealed class BaleKeyboard
{
    private readonly List<List<BaleButton>> _rows = new();

    /// <summary>آیا این صفحه‌کلید دکمهٔ اشتراک‌گذاری شماره دارد؟ (برای آزمون و لاگ)</summary>
    public bool HasContactRequest => _rows.Any(r => r.Any(b => b.RequestContact));

    public static BaleKeyboard Contact() => new BaleKeyboard().Row(new BaleButton("ثبت شماره من", true));

    public static BaleKeyboard Main() => new BaleKeyboard()
        .Row(new BaleButton("ثبت شماره من", true))
        .Row(new BaleButton("قوانین کمپین"), new BaleButton("بررسی وضعیت"));

    public BaleKeyboard Row(params BaleButton[] buttons)
    {
        _rows.Add(buttons.ToList());
        return this;
    }

    public object ToPayload() => new
    {
        keyboard = _rows.Select(r => r.Select(b => b.ToPayload()).ToArray()).ToArray(),
        resize_keyboard = true,
        one_time_keyboard = false
    };
}

public sealed class BaleButton
{
    private readonly string _text;
    private readonly bool _contact;

    public BaleButton(string text, bool requestContact = false)
    {
        _text = text;
        _contact = requestContact;
    }

    public bool RequestContact => _contact;

    public object ToPayload() => _contact
        ? new { text = _text, request_contact = true }
        : new { text = _text };
}
