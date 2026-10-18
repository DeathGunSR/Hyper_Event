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
                PrizeToman = long.TryParse(cfg["Campaign:PrizeToman"], out long pz) && pz > 0 ? pz : 10_000_000L,
                RequireBaleMembership = !bool.TryParse(cfg["Campaign:RequireBaleMembership"], out bool rb) || rb
            };
            return opt;
        });
        builder.Services.AddSingleton<CampaignService>();
        builder.Services.AddSingleton<ReviewDecisionService>();

        // بله: بدون توکن/کانال، وضعیت همیشه «نامعلوم» می‌ماند و قرعه‌کشی واجد شرایط اعلام نمی‌کند.
        builder.Services.AddSingleton(sp => BaleOptions.From(builder.Configuration));
        builder.Services.AddHttpClient<IBaleBotApi, BaleBotApi>();
        builder.Services.AddSingleton<BaleService>();
        builder.Services.AddSingleton<IBaleMembershipProvider>(sp =>
            sp.GetRequiredService<BaleOptions>().IsConfigured
                ? ActivatorUtilities.CreateInstance<BaleMembershipProvider>(sp)
                : new UnconfiguredBaleProvider());
        builder.Services.AddSingleton<LotteryService>();
        builder.Services.AddSingleton(new PublicCache());
        builder.Services.AddSingleton(sp => new PublicRateLimiter(
            int.TryParse(builder.Configuration["PublicApi:RateLimitPerMinute"], out int rl) && rl > 0 ? rl : 120));
        builder.Services.AddSingleton<PublicService>();
        configureServices?.Invoke(builder.Services);

        WebApplication app = builder.Build();
        app.Use(ErrorMiddleware);

        // محدودیت نرخ مسیرهای عمومی + تازه‌سازی کش پس از هر تغییر موفق در سرویس مرکزی.
        app.Use(async (ctx, next) =>
        {
            if (ctx.Request.Path.StartsWithSegments("/api/v1/public"))
            {
                PublicRateLimiter limiter = ctx.RequestServices.GetRequiredService<PublicRateLimiter>();
                string clientKey = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
                if (!limiter.TryAcquire(clientKey, out int remaining, out int retryAfter))
                {
                    ctx.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                    ctx.Response.Headers["Retry-After"] = retryAfter.ToString();
                    ctx.Response.ContentType = "application/json; charset=utf-8";
                    await ctx.Response.WriteAsync(JsonSerializer.Serialize(new ApiError
                    {
                        Code = "rate_limited",
                        Message = "تعداد درخواست‌های عمومی بیش از حد مجاز است؛ کمی بعد تلاش کنید."
                    }, Json));
                    return;
                }
                ctx.Response.Headers["X-RateLimit-Limit"] = limiter.Limit.ToString();
                ctx.Response.Headers["X-RateLimit-Remaining"] = remaining.ToString();
            }

            await next();

            if (ctx.Response.StatusCode < 400
                && (HttpMethods.IsPost(ctx.Request.Method) || HttpMethods.IsDelete(ctx.Request.Method))
                && ctx.Request.Path.StartsWithSegments("/api"))
                ctx.RequestServices.GetRequiredService<PublicCache>().Invalidate();
        });

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
            RequireAdmin(ctx, Cap.CampaignManage);
            AdminActionRequest? req = await ReadOptionalJsonAsync<AdminActionRequest>(ctx);
            RoundInfo round = await svc.StartRoundAsync(req?.Actor ?? ActorOf(ctx), ctx.RequestAborted);
            return Results.Json(round, Json);
        });

        app.MapPost("/api/v1/admin/rounds/{id:int}/close", async (HttpContext ctx, int id, CampaignService svc) =>
        {
            RequireAdmin(ctx, Cap.CampaignManage);
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
            RequireAdmin(ctx, Cap.CampaignManage);
            AssignHeldRequest? req = await ReadOptionalJsonAsync<AssignHeldRequest>(ctx);
            if (req == null || req.RoundId <= 0)
                throw new ApiException(400, "round_required", "RoundId الزامی است.");
            int assigned = await svc.AssignHeldAsync(req.RoundId, req.Limit, req.Actor ?? ActorOf(ctx), ctx.RequestAborted);
            return Results.Json(new { assigned }, Json);
        });

        app.MapPost("/api/v1/admin/invoices/{id:long}/void", async (HttpContext ctx, long id, CampaignService svc) =>
        {
            RequireAdmin(ctx, Cap.ReviewDecide);
            VoidRequest? req = await ReadOptionalJsonAsync<VoidRequest>(ctx);
            if (req == null) throw new ApiException(400, "body_required", "بدنهٔ درخواست الزامی است.");
            VoidResult res = await svc.VoidInvoiceAsync(id, req.Reason ?? "", req.Actor ?? ActorOf(ctx), ctx.RequestAborted);
            return Results.Json(res, Json);
        });

        app.MapPost("/api/v1/admin/invoices/{id:long}/revalidate", async (HttpContext ctx, long id, CampaignService svc) =>
        {
            RequireAdmin(ctx, Cap.ReviewDecide);
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

        // ---------------- عمومی: کمپین، برندگان و سند قرعه‌کشی (بندهای ۵ و ۶) ----------------
        app.MapGet("/api/v1/public/campaign/current", async (HttpContext ctx, PublicService svc) =>
        {
            PublicCampaignStatus status = await svc.GetCampaignAsync(ctx.RequestAborted);
            return PublicJson(ctx, status, 5);
        });

        app.MapGet("/api/v1/public/winners", async (HttpContext ctx, PublicService svc) =>
        {
            int page = QueryInt(ctx, "page", 1, 1, 1000);
            int perPage = QueryInt(ctx, "perPage", 20, 1, 50);
            int? roundNo = QueryIntOrNull(ctx, "roundNo", 1, 100000);
            PublicWinnersPage result = await svc.GetWinnersAsync(page, perPage, roundNo, ctx.RequestAborted);
            return PublicJson(ctx, result, 30);
        });

        app.MapGet("/api/v1/public/winners/{roundNo:int}", async (HttpContext ctx, int roundNo, PublicService svc) =>
        {
            PublicWinnerCard? card = await svc.GetWinnerAsync(roundNo, ctx.RequestAborted);
            if (card == null)
                throw new ApiException(404, "not_found", "برای این دوره برندهٔ منتشرشده‌ای وجود ندارد.");
            return PublicJson(ctx, card, 30);
        });

        app.MapGet("/api/v1/public/lottery/{id:int}/receipt", async (HttpContext ctx, int id, PublicService svc) =>
        {
            PublicLotteryReceipt? receipt = await svc.GetReceiptAsync(id, ctx.RequestAborted);
            if (receipt == null) throw new ApiException(404, "not_found", "سند قرعه‌کشی پیدا نشد.");
            return PublicJson(ctx, receipt, 60);
        });

        app.MapGet("/api/v1/public/lottery/{id:int}/entries", async (HttpContext ctx, int id, PublicService svc) =>
        {
            int page = QueryInt(ctx, "page", 1, 1, 100000);
            int perPage = QueryInt(ctx, "perPage", 200, 1, 500);
            PublicEntriesPage entries = await svc.GetEntriesAsync(id, page, perPage, ctx.RequestAborted);
            return PublicJson(ctx, entries, 300);
        });

        app.MapGet("/api/v1/public/lottery/{id:int}/verify", async (HttpContext ctx, int id, PublicService svc) =>
        {
            PublicVerifyResult? result = await svc.GetVerifyAsync(id, ctx.RequestAborted);
            if (result == null) throw new ApiException(404, "not_found", "سند قرعه‌کشی پیدا نشد.");
            return PublicJson(ctx, result, 60);
        });

        // ---------------- بله: ثبت‌نام ربات و بررسی عضویت (بند ۹) ----------------
        app.MapPost("/api/v1/agent/bale/register", async (HttpContext ctx, BaleService svc) =>
        {
            byte[] body = await ReadBodyAsync(ctx, 4096);
            string agentId = RequireAgent(ctx, body);
            BaleRegisterRequest? req = JsonSerializer.Deserialize<BaleRegisterRequest>(body, Json);
            if (req == null) throw new ApiException(400, "body_invalid", "بدنهٔ درخواست قابل خواندن نیست.");

            (BaleRegistration? reg, string? error) = await svc.RegisterAsync(req, agentId, ctx.RequestAborted);
            if (reg == null)
                throw new ApiException(error == "mobile_registered_to_other_account" ? 409 : 400,
                    error ?? "register_failed", error switch
                    {
                        "invalid_mobile" => "شمارهٔ موبایل معتبر نیست.",
                        "invalid_bale_user" => "شناسهٔ حساب بله معتبر نیست.",
                        "contact_not_shared" => "شماره فقط با اشتراک‌گذاری مستقیم کاربر در بله پذیرفته می‌شود.",
                        "mobile_registered_to_other_account" => "این شماره پیش‌تر به حساب بلهٔ دیگری متصل شده است.",
                        _ => "ثبت‌نام انجام نشد."
                    });

            return Results.Json(new
            {
                registered = true,
                baleUserId = reg.BaleUserId,
                mobileMasked = reg.MobileMasked,
                contactShared = reg.ContactShared,
                verifiedAt = reg.VerifiedAt
            }, Json);
        });

        app.MapPost("/api/v1/agent/bale/status", async (HttpContext ctx, BaleService svc) =>
        {
            byte[] body = await ReadBodyAsync(ctx, 2048);
            string agentId = RequireAgent(ctx, body);
            BaleStatusRequest? req = JsonSerializer.Deserialize<BaleStatusRequest>(body, Json);
            if (req == null) throw new ApiException(400, "body_invalid", "بدنهٔ درخواست قابل خواندن نیست.");
            BaleStatusResult status = await svc.GetStatusAsync(req.Mobile, "agent:" + agentId, ctx.RequestAborted);
            return Results.Json(status, Json);
        });

        app.MapPost("/api/v1/agent/bale/lookup", async (HttpContext ctx, BaleService svc) =>
        {
            byte[] body = await ReadBodyAsync(ctx, 1024);
            RequireAgent(ctx, body);
            BaleLookupRequest? req = JsonSerializer.Deserialize<BaleLookupRequest>(body, Json);
            if (req == null || req.BaleUserId <= 0)
                throw new ApiException(400, "body_invalid", "شناسهٔ حساب بله معتبر نیست.");
            string? mobile = await svc.GetMobileByBaleUserAsync(req.BaleUserId, ctx.RequestAborted);
            // فقط برای همان عامل امضاشده؛ شماره کامل در پاسخ عمومی یا لاگ چاپ نمی‌شود.
            return Results.Json(new { registered = mobile != null, mobile }, Json);
        });

        app.MapGet("/api/v1/admin/bale/registrations", async (HttpContext ctx, BaleService svc) =>
        {
            RequireAdmin(ctx, Cap.PrivacyContact);
            int limit = int.TryParse(ctx.Request.Query["limit"], out int l) ? l : 100;
            IReadOnlyList<BaleRegistration> list = await svc.ListAsync(limit, ctx.RequestAborted);
            return Results.Json(new { count = list.Count, registrations = list }, Json);
        });

        app.MapPost("/api/v1/admin/bale/check", async (HttpContext ctx, BaleService svc) =>
        {
            RequireAdmin(ctx, Cap.PrivacyContact);
            BaleStatusRequest? req = await ReadOptionalJsonAsync<BaleStatusRequest>(ctx);
            if (req == null) throw new ApiException(400, "body_required", "بدنهٔ درخواست الزامی است.");
            BaleStatusResult status = await svc.GetStatusAsync(req.Mobile, ActorOf(ctx), ctx.RequestAborted);
            return Results.Json(status, Json);
        });

        // ---------------- مسیر تصمیم مدیر (بند ۴.۲) ----------------
        app.MapGet("/api/v1/admin/invoices/{id:long}/detail", async (HttpContext ctx, long id, ReviewDecisionService svc) =>
        {
            RequireAdmin(ctx);
            return Results.Json(await svc.GetInvoiceDetailAsync(id, ctx.RequestAborted), Json);
        });

        app.MapPost("/api/v1/admin/invoices/{id:long}/decision", async (HttpContext ctx, long id, ReviewDecisionService svc) =>
        {
            RequireAdmin(ctx, Cap.ReviewDecide);
            DecisionRequest? req = await ReadOptionalJsonAsync<DecisionRequest>(ctx);
            if (req == null) throw new ApiException(400, "body_required", "بدنهٔ درخواست الزامی است.");
            if (string.IsNullOrWhiteSpace(req.Actor)) req.Actor = ActorOf(ctx);
            DecisionResult res = await svc.DecideAsync(id, req, req.Actor!, ctx.RequestAborted);
            return Results.Json(res, Json);
        });

        // ---------------- موتور قرعه‌کشی (بند ۵) ----------------
        app.MapPost("/api/v1/admin/rounds/{id:int}/lottery", async (HttpContext ctx, int id, LotteryService svc) =>
        {
            RequireAdmin(ctx, Cap.LotteryRun);
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
            RequireAdmin(ctx, Cap.LotteryRun);
            AdminActionRequest? req = await ReadOptionalJsonAsync<AdminActionRequest>(ctx);
            return Results.Json(await svc.SelectAsync(id, req?.Actor ?? ActorOf(ctx), ctx.RequestAborted), Json);
        });

        app.MapPost("/api/v1/admin/lottery/{id:int}/finalize", async (HttpContext ctx, int id, LotteryService svc) =>
        {
            RequireAdmin(ctx, Cap.LotteryRun);
            AdminActionRequest? req = await ReadOptionalJsonAsync<AdminActionRequest>(ctx);
            return Results.Json(await svc.FinalizeAsync(id, req?.Actor ?? ActorOf(ctx), ctx.RequestAborted), Json);
        });

        app.MapPost("/api/v1/admin/lottery/{id:int}/abort", async (HttpContext ctx, int id, LotteryService svc) =>
        {
            RequireAdmin(ctx, Cap.LotteryRun);
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
            string actor = RequireAdmin(ctx, Cap.PrivacyContact);
            return Results.Json(await svc.WinnerContactAsync(id, actor, ctx.RequestAborted), Json);
        });

        // ---------------- انتشار عمومی نتیجهٔ قرعه‌کشی (فقط با تأیید صریح) ----------------
        app.MapPost("/api/v1/admin/lottery/{id:int}/publish", async (HttpContext ctx, int id, LotteryService svc) =>
        {
            RequireAdmin(ctx, Cap.LotteryPublish);
            PublishRequest? req = await ReadOptionalJsonAsync<PublishRequest>(ctx);
            if (req == null || !req.Confirm)
                throw new ApiException(400, "confirm_required",
                    "انتشار عمومی نتیجه نیازمند تأیید صریح مدیر (confirm=true) است.");
            LotteryRunInfo run = await svc.PublishAsync(id, req.DisplayName ?? "", req.ImageUrl, req.ImageApproved,
                req.Actor ?? ActorOf(ctx), ctx.RequestAborted);
            return Results.Json(run, Json);
        });

        app.MapPost("/api/v1/admin/lottery/{id:int}/unpublish", async (HttpContext ctx, int id, LotteryService svc) =>
        {
            RequireAdmin(ctx, Cap.LotteryPublish);
            VoidRequest? req = await ReadOptionalJsonAsync<VoidRequest>(ctx);
            if (req == null) throw new ApiException(400, "body_required", "بدنهٔ درخواست الزامی است.");
            LotteryRunInfo run = await svc.UnpublishAsync(id, req.Reason ?? "", req.Actor ?? ActorOf(ctx),
                ctx.RequestAborted);
            return Results.Json(run, Json);
        });

        app.MapPost("/api/v1/admin/lottery/{id:int}/image", async (HttpContext ctx, int id, LotteryService svc) =>
        {
            RequireAdmin(ctx, Cap.LotteryPublish);
            WinnerImageRequest? req = await ReadOptionalJsonAsync<WinnerImageRequest>(ctx);
            if (req == null) throw new ApiException(400, "body_required", "بدنهٔ درخواست الزامی است.");
            LotteryRunInfo run = await svc.SetWinnerImageAsync(id, req.ImageUrl, req.Approved,
                req.Actor ?? ActorOf(ctx), ctx.RequestAborted);
            return Results.Json(run, Json);
        });

        return app;
    }

    /// <summary>مجوزهای مستقل سرور‌محور؛ هر مسیر فقط با کلید مدیر کل یا کلید نقش مربوطه باز می‌شود.</summary>
    private static class Cap
    {
        public const string CampaignManage = "campaign.manage";
        public const string ReviewDecide = "review.decide";
        public const string LotteryRun = "lottery.run";
        public const string LotteryPublish = "lottery.publish";
        public const string PrivacyContact = "privacy.contact";
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

    /// <summary>
    /// احراز هویت مدیر. با کلید مدیر کل هر مسیری باز می‌شود؛ با کلید نقش، فقط مسیرهای
    /// همان توانایی (capability). کنترل همیشه سمت سرور است و به پنهان‌کردن دکمه تکیه ندارد.
    /// </summary>
    private static string RequireAdmin(HttpContext ctx, string? capability = null)
    {
        IConfiguration cfg = ctx.RequestServices.GetRequiredService<IConfiguration>();
        string full = cfg["Auth:AdminKey"] ?? "";
        string role = capability == null ? "" : cfg["Auth:Roles:" + capability] ?? "";

        if (string.IsNullOrWhiteSpace(full) && string.IsNullOrWhiteSpace(role))
            throw new ApiException(503, "admin_disabled", "کلید مدیر تنظیم نشده است.");

        string got = ctx.Request.Headers["X-Admin-Key"].ToString();
        bool ok = (!string.IsNullOrWhiteSpace(full) && Security.FixedEquals(full, got))
                  || (!string.IsNullOrWhiteSpace(role) && Security.FixedEquals(role, got));
        if (!ok)
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

    private static IResult PublicJson(HttpContext ctx, object payload, int maxAgeSeconds)
    {
        ctx.Response.Headers["Cache-Control"] = $"public, max-age={maxAgeSeconds}";
        return Results.Json(payload, Json);
    }

    private static int QueryInt(HttpContext ctx, string name, int fallback, int min, int max)
    {
        string raw = ctx.Request.Query[name].ToString();
        if (string.IsNullOrWhiteSpace(raw)) return fallback;
        if (!int.TryParse(raw, out int value) || value < min || value > max)
            throw new ApiException(400, "query_invalid", $"مقدار «{name}» نامعتبر است.");
        return value;
    }

    private static int? QueryIntOrNull(HttpContext ctx, string name, int min, int max)
    {
        string raw = ctx.Request.Query[name].ToString();
        if (string.IsNullOrWhiteSpace(raw)) return null;
        if (!int.TryParse(raw, out int value) || value < min || value > max)
            throw new ApiException(400, "query_invalid", $"مقدار «{name}» نامعتبر است.");
        return value;
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
