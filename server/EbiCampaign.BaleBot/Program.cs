using System.Text.Json;
using EbiCampaign.BaleBot;

/// <summary>
/// میزبان ربات بله.
///
/// حالت پیش‌فرض: webhook روی مسیر محرمانهٔ پیکربندی‌شده.
/// حالت توسعه/آزمون: «--poll» برای دریافت به‌روزرسانی‌ها با getUpdates.
/// هیچ ارسال پیامکی در این سرویس وجود ندارد.
/// </summary>
WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

BotOptions opt = BotOptions.From(builder.Configuration);
builder.Services.AddSingleton(opt);
builder.Services.AddHttpClient<IBaleApi, BaleApi>();
builder.Services.AddHttpClient<ICentralApi, CentralApi>();
builder.Services.AddSingleton<BotHandler>();

if (!opt.HasToken)
    Console.Error.WriteLine("هشدار: Bale:Token تنظیم نشده است؛ ربات پیامی ارسال نمی‌کند.");

WebApplication app = builder.Build();
BotHandler handler = app.Services.GetRequiredService<BotHandler>();

string webhookPath = string.IsNullOrWhiteSpace(opt.WebhookPath) ? "/bale/webhook" : "/" + opt.WebhookPath.TrimStart('/');

app.MapPost(webhookPath, async (HttpContext ctx) =>
{
    try
    {
        BaleUpdate? update = await JsonSerializer.DeserializeAsync<BaleUpdate>(
            ctx.Request.Body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true },
            ctx.RequestAborted);
        if (update != null)
            await handler.HandleAsync(update, ctx.RequestAborted);
        return Results.Ok(new { ok = true });
    }
    catch (JsonException)
    {
        // به‌روزرسانی نامعتبر هرگز باعث خطای ۵۰۰ نمی‌شود تا بله دوباره تلاش نکند.
        return Results.Ok(new { ok = false, reason = "invalid_update" });
    }
});

app.MapGet("/livez", () => Results.Json(new { ok = true }));

if (args.Contains("--set-webhook") && !string.IsNullOrWhiteSpace(opt.PublicBaseUrl))
{
    IBaleApi bale = app.Services.GetRequiredService<IBaleApi>();
    await bale.SetWebhookAsync(opt.PublicBaseUrl.TrimEnd('/') + webhookPath);
    Console.WriteLine("webhook registered.");
    return;
}

if (args.Contains("--poll"))
{
    IBaleApi bale = app.Services.GetRequiredService<IBaleApi>();
    long offset = 0;
    Console.WriteLine("polling mode started.");
    while (true)
    {
        IReadOnlyList<BaleUpdate> updates = await bale.GetUpdatesAsync(offset, 25);
        foreach (BaleUpdate update in updates)
        {
            offset = Math.Max(offset, update.UpdateId + 1);
            await handler.HandleAsync(update);
        }
    }
}

await app.RunAsync();
