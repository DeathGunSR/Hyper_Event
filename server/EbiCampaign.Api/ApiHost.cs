using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace EbiCampaign.Api;

public static class ApiHost
{
    private static readonly JsonSerializerOptions Json = CampaignService.Json;

    /// <param name="configureServices">
    /// جایگزینی اختیاری سرویس‌ها برای آزمون (مثلاً ارائه‌دهندهٔ عضویت بله).
    /// در استقرار واقعی هیچ جایگزینی تزریق نمی‌شود.
    /// </param>
    public static WebApplication Build(string[] args, IDictionary<string, string?>? overrides = null,
        Action<IServiceCollection>? configureServices = null)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
        if (overrides != null) builder.Configuration.AddInMemoryCollection(overrides);

        string cs = builder.Configuration.GetConnectionString("CampaignDb")
            ?? throw new InvalidOperationException("ConnectionStrings:CampaignDb تنظیم نشده است.");

        builder.Services.AddSingleton(new Db(cs));
        builder.Services.AddSingleton(sp =>
        {
            IConfiguration cfg = builder.Configuration;
            CampaignOptions opt = new()
            {
                SourceId = cfg["Campaign:SourceId"] ?? "Holoo1",
                TargetInvoices = int.TryParse(cfg["Campaign:TargetInvoices"], out int t) && t > 0 ? t : 1000,
                StartDate = DateOnly.TryParseExact(cfg["Campaign:StartDate"], "yyyy-MM-dd",
                    System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out DateOnly d)
                    ? d : new DateOnly(2026, 1, 1),
                PointsPerAmountToman = decimal.TryParse(cfg["Campaign:PointsPerAmountToman"], out decimal p) && p > 0 ? p : 500000m,
                PointsBase = (cfg["Campaign:PointsBase"] ?? "gross").ToLowerInvariant(),
                RequireBaleMembership = !bool.TryParse(cfg["Campaign:RequireBaleMembership"], out bool rb) || rb
            };
            return opt;
        });
        builder.Services.AddSingleton<CampaignService>();
        builder.Services.AddSingleton<ReviewDecisionService>();
        // پیش‌فرض: هیچ اتصال بله‌ای پیکربندی نشده است؛ سرویس جایگزین فقط برای آزمون تزریق می‌شود.
        builder.Services.AddSingleton<IBaleMembershipProvider, UnconfiguredBaleProvider>();
        builder.Services.AddSingleton<LotteryService>();
        configureServices?.Invoke(builder.Services);

        WebApplication app = builder.Build();
        app.Use(ErrorMiddleware);

        // ---------------- عمومی ----------------
        /// <summary>زیست‌سنجی سبک بدون دست‌زدن به دیتابیس؛ مناسب کاوشگر بار متعادل‌کننده.</summary>
        app.MapGet("/livez", () => Results.Json(new { ok = true }, Json));

        /// <summary>آمادگی وابستگی‌ها؛ وقتی دیتابیس در دسترس نباشد 503 می‌دهد.</summary>
        app.MapGet("/readyz", async (Db db) =>
        {
            try
            {
                await using SqlConnection cn = await db.OpenAsync();
                using SqlCommand cmd = new("SELECT 1", cn);
                await cmd.ExecuteScalarAsync();
                return Results.Json(new { ok = true }, Json);
            }
            catch (Exception ex)
            {
                return Results.Json(new { ok = false, error = ex.GetType().Name }, Json, statusCode: 503);
            }
        });

        app.MapGet("/health", async (Db db) =>
        {
            try
            {
                await using SqlConnection cn = await db.OpenAsync();
                using SqlCommand cmd = new("SELECT ISNULL(MAX(Version),0) FROM dbo.SchemaVersions", cn);
                int version = Convert.ToInt32(await cmd.ExecuteScalarAsync());
                CampaignService svc = app.Services.GetRequiredService<CampaignService>();
                RoundInfo? round = await svc.GetCurrentRoundAsync();
                return Results.Json(new { ok = true, schemaVersion = version, round }, Json);
            }
            catch (Exception ex)
            {
                return Results.Json(new { ok = false, error = ex.GetType().Name }, Json, statusCode: 503);
            }
        });

        // ---------------- عامل همگام‌سازی ----------------
        app.MapPost("/api/v1/ingest/invoices", async (HttpContext ctx, CampaignService svc) =>
        {
            byte[] body = await ReadBodyAsync(ctx, 1_000_000);
            string agentId = RequireAgent(ctx, body);
            IngestRequest? req = JsonSerializer.Deserialize<IngestRequest>(body, Json);
            if (req == null) throw new ApiException(400, "body_invalid", "بدنهٔ درخواست قابل خواندن نیست.");
            string hash = Security.Sha256Hex(body);
            IngestResponse res = await svc.IngestAsync(agentId, req, hash, ctx.RequestAborted);
            return Results.Json(res, Json);
        });

        app.MapGet("/api/v1/rounds/current", async (HttpContext ctx, CampaignService svc) =>
        {
            byte[] body = await ReadBodyAsync(ctx, 4096);
            RequireAgent(ctx, body);
            RoundInfo? round = await svc.GetCurrentRoundAsync(ctx.RequestAborted);
            return Results.Json(new { round }, Json);
        });

        // ---------------- مدیر ----------------
        app.MapGet("/api/v1/admin/rounds", async (HttpContext ctx, CampaignService svc) =>
        {
            RequireAdmin(ctx);
            return Results.Json(await svc.GetRoundsAsync(ctx.RequestAborted), Json);
        });

        app.MapPost("/api/v1/admin/rounds/start", async (HttpContext ctx, CampaignService svc) =>
        {
            RequireAdmin(ctx);
            AdminActionRequest? req = await ReadOptionalJsonAsync<AdminActionRequest>(ctx);
            RoundInfo round = await svc.StartRoundAsync(req?.Actor ?? ActorOf(ctx), ctx.RequestAborted);
            return Results.Json(round, Json);
        });

        app.MapPost("/api/v1/admin/rounds/{id:int}/close", async (HttpContext ctx, int id, CampaignService svc) =>
        {
            RequireAdmin(ctx);
            AdminActionRequest? req = await ReadOptionalJsonAsync<AdminActionRequest>(ctx);
            RoundInfo round = await svc.CloseRoundAsync(id, req?.Actor ?? ActorOf(ctx), ctx.RequestAborted);
            return Results.Json(round, Json);
        });

        app.MapGet("/api/v1/admin/held", async (HttpContext ctx, CampaignService svc) =>
        {
            RequireAdmin(ctx);
            int limit = int.TryParse(ctx.Request.Query["limit"], out int l) ? l : 200;
            return Results.Json(await svc.GetHeldAsync(limit, ctx.RequestAborted), Json);
        });

        app.MapPost("/api/v1/admin/held/assign", async (HttpContext ctx, CampaignService svc) =>
        {
            RequireAdmin(ctx);
            AssignHeldRequest? req = await ReadOptionalJsonAsync<AssignHeldRequest>(ctx);
            if (req == null || req.RoundId <= 0)
                throw new ApiException(400, "round_required", "RoundId الزامی است.");
            int assigned = await svc.AssignHeldAsync(req.RoundId, req.Limit, req.Actor ?? ActorOf(ctx), ctx.RequestAborted);
            return Results.Json(new { assigned }, Json);
        });

        app.MapPost("/api/v1/admin/invoices/{id:long}/void", async (HttpContext ctx, long id, CampaignService svc) =>
        {
            RequireAdmin(ctx);
            VoidRequest? req = await ReadOptionalJsonAsync<VoidRequest>(ctx);
            if (req == null) throw new ApiException(400, "body_required", "بدنهٔ درخواست الزامی است.");
            VoidResult res = await svc.VoidInvoiceAsync(id, req.Reason ?? "", req.Actor ?? ActorOf(ctx), ctx.RequestAborted);
            return Results.Json(res, Json);
        });

        app.MapPost("/api/v1/admin/invoices/{id:long}/revalidate", async (HttpContext ctx, long id, CampaignService svc) =>
        {
            RequireAdmin(ctx);
            AdminActionRequest? req = await ReadOptionalJsonAsync<AdminActionRequest>(ctx);
            RevalidateResult res = await svc.RevalidateInvoiceAsync(id, req?.Actor ?? ActorOf(ctx), ctx.RequestAborted);
            return Results.Json(res, Json);
        });

        app.MapGet("/api/v1/admin/invoices", async (HttpContext ctx, CampaignService svc) =>
        {
            RequireAdmin(ctx);
            int? roundId = int.TryParse(ctx.Request.Query["roundId"], out int r) ? r : null;
            string? status = ctx.Request.Query["status"];
            int limit = int.TryParse(ctx.Request.Query["limit"], out int l) ? l : 200;
            return Results.Json(await svc.GetInvoicesAsync(roundId, status, limit, ctx.RequestAborted), Json);
        });

        // ---------------- مسیر تصمیم مدیر (بند ۴.۲) ----------------
        app.MapGet("/api/v1/admin/invoices/{id:long}/detail", async (HttpContext ctx, long id, ReviewDecisionService svc) =>
        {
            RequireAdmin(ctx);
            return Results.Json(await svc.GetInvoiceDetailAsync(id, ctx.RequestAborted), Json);
        });

        app.MapPost("/api/v1/admin/invoices/{id:long}/decision", async (HttpContext ctx, long id, ReviewDecisionService svc) =>
        {
            RequireAdmin(ctx);
            DecisionRequest? req = await ReadOptionalJsonAsync<DecisionRequest>(ctx);
            if (req == null) throw new ApiException(400, "body_required", "بدنهٔ درخواست الزامی است.");
            if (string.IsNullOrWhiteSpace(req.Actor)) req.Actor = ActorOf(ctx);
            DecisionResult res = await svc.DecideAsync(id, req, req.Actor!, ctx.RequestAborted);
            return Results.Json(res, Json);
        });

        // ---------------- موتور قرعه‌کشی (بند ۵) ----------------
        app.MapPost("/api/v1/admin/rounds/{id:int}/lottery", async (HttpContext ctx, int id, LotteryService svc) =>
        {
            RequireAdmin(ctx);
            PrepareLotteryRequest? req = await ReadOptionalJsonAsync<PrepareLotteryRequest>(ctx);
            LotteryRunInfo run = await svc.PrepareAsync(id, req?.AcknowledgeReviews ?? false,
                req?.Actor ?? ActorOf(ctx), ctx.RequestAborted);
            return Results.Json(run, Json);
        });

        app.MapGet("/api/v1/admin/lottery", async (HttpContext ctx, LotteryService svc) =>
        {
            RequireAdmin(ctx);
            int limit = int.TryParse(ctx.Request.Query["limit"], out int l) ? l : 50;
            return Results.Json(await svc.ListRunsAsync(limit, ctx.RequestAborted), Json);
        });

        app.MapGet("/api/v1/admin/lottery/{id:int}", async (HttpContext ctx, int id, LotteryService svc) =>
        {
            RequireAdmin(ctx);
            bool entries = !string.Equals(ctx.Request.Query["entries"], "false", StringComparison.OrdinalIgnoreCase);
            return Results.Json(await svc.GetRunAsync(id, entries, ctx.RequestAborted), Json);
        });

        app.MapPost("/api/v1/admin/lottery/{id:int}/select", async (HttpContext ctx, int id, LotteryService svc) =>
        {
            RequireAdmin(ctx);
            AdminActionRequest? req = await ReadOptionalJsonAsync<AdminActionRequest>(ctx);
            return Results.Json(await svc.SelectAsync(id, req?.Actor ?? ActorOf(ctx), ctx.RequestAborted), Json);
        });

        app.MapPost("/api/v1/admin/lottery/{id:int}/finalize", async (HttpContext ctx, int id, LotteryService svc) =>
        {
            RequireAdmin(ctx);
            AdminActionRequest? req = await ReadOptionalJsonAsync<AdminActionRequest>(ctx);
            return Results.Json(await svc.FinalizeAsync(id, req?.Actor ?? ActorOf(ctx), ctx.RequestAborted), Json);
        });

        app.MapPost("/api/v1/admin/lottery/{id:int}/abort", async (HttpContext ctx, int id, LotteryService svc) =>
        {
            RequireAdmin(ctx);
            VoidRequest? req = await ReadOptionalJsonAsync<VoidRequest>(ctx);
            if (req == null) throw new ApiException(400, "body_required", "بدنهٔ درخواست الزامی است.");
            return Results.Json(await svc.AbortAsync(id, req.Reason ?? "", req.Actor ?? ActorOf(ctx),
                ctx.RequestAborted), Json);
        });

        app.MapGet("/api/v1/admin/lottery/{id:int}/receipt", async (HttpContext ctx, int id, LotteryService svc) =>
        {
            RequireAdmin(ctx);
            return Results.Json(await svc.ReceiptAsync(id, ctx.RequestAborted), Json);
        });

        app.MapGet("/api/v1/admin/lottery/{id:int}/verify", async (HttpContext ctx, int id, LotteryService svc) =>
        {
            RequireAdmin(ctx);
            return Results.Json(await svc.VerifyAsync(id, ctx.RequestAborted), Json);
        });

        /// <summary>
        /// شمارهٔ کامل برنده برای تماس/ارسال جایزه؛ هر دسترسی در AuditLog ثبت می‌شود.
        /// </summary>
        app.MapGet("/api/v1/admin/lottery/{id:int}/winner-contact", async (HttpContext ctx, int id, LotteryService svc) =>
        {
            string actor = RequireAdmin(ctx);
            return Results.Json(await svc.WinnerContactAsync(id, actor, ctx.RequestAborted), Json);
        });

        return app;
    }

    /// <summary>ساخت دیتابیس (در صورت مجاز بودن) و اجرای مهاجرت‌ها.</summary>
    public static async Task InitializeAsync(WebApplication app, CancellationToken ct = default)
    {
        IConfiguration cfg = app.Configuration;
        string cs = cfg.GetConnectionString("CampaignDb")
            ?? throw new InvalidOperationException("ConnectionStrings:CampaignDb تنظیم نشده است.");

        if (bool.TryParse(cfg["Campaign:AutoCreateDatabase"], out bool auto) && auto)
            await Db.EnsureDatabaseAsync(cs, ct);

        await Db.MigrateAsync(cs, ct);
    }

    // ------------------------------------------------------------------
    private static async Task ErrorMiddleware(HttpContext ctx, Func<Task> next)
    {
        try
        {
            await next();
        }
        catch (ApiException ex)
        {
            if (ctx.Response.HasStarted) throw;
            ctx.Response.StatusCode = ex.Status;
            ctx.Response.ContentType = "application/json; charset=utf-8";
            await ctx.Response.WriteAsync(JsonSerializer.Serialize(new ApiError { Code = ex.Code, Message = ex.Message }, Json));
        }
        catch (Exception ex)
        {
            if (ctx.Response.HasStarted) throw;
            ILogger logger = ctx.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("api");
            logger.LogError(ex, "unhandled error on {Path}", ctx.Request.Path);
            ctx.Response.StatusCode = 500;
            ctx.Response.ContentType = "application/json; charset=utf-8";
            await ctx.Response.WriteAsync(JsonSerializer.Serialize(
                new ApiError { Code = "internal_error", Message = "خطای داخلی سرور." }, Json));
        }
    }

    private static string RequireAdmin(HttpContext ctx)
    {
        string expected = ctx.RequestServices.GetRequiredService<IConfiguration>()["Auth:AdminKey"] ?? "";
        if (string.IsNullOrWhiteSpace(expected))
            throw new ApiException(503, "admin_disabled", "کلید مدیر تنظیم نشده است.");
        string got = ctx.Request.Headers["X-Admin-Key"].ToString();
        if (!Security.FixedEquals(expected, got))
            throw new ApiException(401, "admin_unauthorized", "کلید مدیر نامعتبر است.");
        return ActorOf(ctx);
    }

    private static string RequireAgent(HttpContext ctx, byte[] body)
    {
        IConfiguration cfg = ctx.RequestServices.GetRequiredService<IConfiguration>();
        string agentId = ctx.Request.Headers["X-Agent-Id"].ToString().Trim();
        string timestamp = ctx.Request.Headers["X-Timestamp"].ToString().Trim();
        string signature = ctx.Request.Headers["X-Signature"].ToString().Trim();

        if (agentId.Length == 0) throw new ApiException(401, "agent_required", "شناسهٔ عامل ارسال نشده است.");
        if (!Security.TimestampFresh(timestamp))
            throw new ApiException(401, "timestamp_invalid", "زمان امضا نامعتبر یا قدیمی است.");

        string? secret = cfg["Auth:Agents:" + agentId];
        if (string.IsNullOrWhiteSpace(secret))
            throw new ApiException(401, "agent_unknown", "عامل ناشناخته است.");

        string canonical = Security.Canonical(agentId, timestamp, ctx.Request.Method, ctx.Request.Path,
            Security.Sha256Hex(body));
        if (!Security.Verify(secret, canonical, signature))
            throw new ApiException(401, "signature_invalid", "امضای درخواست نامعتبر است.");

        return agentId;
    }

    private static string ActorOf(HttpContext ctx)
    {
        string actor = ctx.Request.Headers["X-Actor"].ToString().Trim();
        if (actor.Length == 0) actor = "admin";
        return actor.Length > 100 ? actor[..100] : actor;
    }

    private static async Task<byte[]> ReadBodyAsync(HttpContext ctx, int maxBytes)
    {
        if (ctx.Request.ContentLength.HasValue && ctx.Request.ContentLength.Value > maxBytes)
            throw new ApiException(413, "body_too_large", "حجم درخواست بیش از حد مجاز است.");
        using MemoryStream ms = new();
        await ctx.Request.Body.CopyToAsync(ms, ctx.RequestAborted);
        if (ms.Length > maxBytes) throw new ApiException(413, "body_too_large", "حجم درخواست بیش از حد مجاز است.");
        return ms.ToArray();
    }

    private static async Task<T?> ReadOptionalJsonAsync<T>(HttpContext ctx) where T : class
    {
        byte[] body = await ReadBodyAsync(ctx, 16_384);
        if (body.Length == 0) return null;
        return JsonSerializer.Deserialize<T>(body, Json);
    }
}
