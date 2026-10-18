namespace EbiCampaign.BaleBot;

/// <summary>
/// پیکربندی ربات. توکن، کلید عامل و مسیر webhook محرمانه‌اند و فقط از پیکربندی
/// یا متغیر محیطی خوانده می‌شوند؛ در مخزن یا لاگ ثبت نمی‌شوند.
/// </summary>
public sealed class BotOptions
{
    public string Token { get; init; } = "";
    public string ApiBase { get; init; } = "https://tapi.bale.ai";
    public string ChannelUrl { get; init; } = "";
    public string CentralBase { get; init; } = "";
    public string AgentId { get; init; } = "bale-bot";
    public string AgentSecret { get; init; } = "";
    public string WebhookPath { get; init; } = "";
    public string PublicBaseUrl { get; init; } = "";

    public bool HasToken => !string.IsNullOrWhiteSpace(Token);
    public bool HasCentral => !string.IsNullOrWhiteSpace(CentralBase) && !string.IsNullOrWhiteSpace(AgentSecret);

    public static BotOptions From(IConfiguration cfg) => new()
    {
        Token = (cfg["Bale:Token"] ?? "").Trim(),
        ApiBase = string.IsNullOrWhiteSpace(cfg["Bale:ApiBase"]) ? "https://tapi.bale.ai" : cfg["Bale:ApiBase"]!.Trim(),
        ChannelUrl = (cfg["Bale:ChannelUrl"] ?? "").Trim(),
        CentralBase = (cfg["Central:BaseUrl"] ?? "").Trim(),
        AgentId = string.IsNullOrWhiteSpace(cfg["Central:AgentId"]) ? "bale-bot" : cfg["Central:AgentId"]!.Trim(),
        AgentSecret = (cfg["Central:AgentSecret"] ?? "").Trim(),
        WebhookPath = (cfg["Bale:WebhookPath"] ?? "").Trim(),
        PublicBaseUrl = (cfg["Bale:PublicBaseUrl"] ?? "").Trim()
    };
}
