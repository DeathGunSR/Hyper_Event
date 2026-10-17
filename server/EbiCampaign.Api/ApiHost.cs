using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace EbiCampaign.Api;

public static class ApiHost
{
    private static readonly JsonSerializerOptions Json = CampaignService.Json;

    public static WebApplication Build(string[] args, IDictionary<string, string?>? overrides = null)
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
                PointsBase = (cfg["Campaign:PointsBase"] ?? "gross").ToLowerInvariant()
            };
            return opt;
        });
        builder.Services.AddSingleton<CampaignService>();

        WebApplication app = builder.Build();
        app.Use(ErrorMiddleware);

        // ---------------- عمومی ----------------
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
