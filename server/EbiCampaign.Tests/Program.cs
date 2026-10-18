using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using EbiCampaign.Api;
using Microsoft.AspNetCore.Builder;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace EbiCampaign.Tests;

internal static class Program
{
    private const string MasterCs =
        "Server=.\\SQLEXPRESS;Database=master;Integrated Security=True;Encrypt=False;TrustServerCertificate=True;Connect Timeout=8";
    private const string DbName = "EbiCampaign_Test";
    private static readonly string TestCs =
        $"Server=.\\SQLEXPRESS;Database={DbName};Integrated Security=True;Encrypt=False;TrustServerCertificate=True;Connect Timeout=8";

    private const string AdminKey = "test-admin-key";
    private const string AgentA = "agent-a";
    private const string AgentASecret = "secret-a";
    private const string AgentB = "agent-b";
    private const string AgentBSecret = "secret-b";
    // کلیدهای نقش: هر توانایی کلید مستقل خود را دارد (بند ۱۱ — کنترل سمت سرور).
    private const string RoleCampaign = "role-campaign-key";
    private const string RoleReview = "role-review-key";
    private const string RoleLottery = "role-lottery-key";
    private const string RolePublish = "role-publish-key";
    private const string RolePrivacy = "role-privacy-key";

    private static WebApplication _app = null!;
    private static HttpClient _http = null!;
    private static string _baseUrl = "";
    private static int _ok;
    private static int _fail;

    /// <summary>موبایل ثابت فاکتورهای ساختگی در آزمون‌ها.</summary>
    private const string SeedMobile = "09120000000";

    /// <summary>ارائه‌دهندهٔ جعلی عضویت بله: فقط در حافظه، برای آزمون رفتار مجاز/غیرمجاز.</summary>
    private static bool _baleReady;
    private static readonly HashSet<string> BaleMembers = new(StringComparer.Ordinal);

    /// <summary>Bot API جعلی بله؛ پاسخ هر حساب در هر آزمون تعیین می‌شود.</summary>
    private static readonly FakeBaleBotApi FakeBale = new();

    private sealed class FakeBaleBotApi : IBaleBotApi
    {
        public Dictionary<long, BaleMemberResult> Results { get; } = new();

        public Task<BaleMemberResult> GetChatMemberAsync(long baleUserId, CancellationToken ct = default)
            => Task.FromResult(Results.TryGetValue(baleUserId, out BaleMemberResult? r)
                ? r : BaleMemberResult.Unknown("test-unknown"));

        public Task<bool> SendMessageAsync(long chatId, string text, CancellationToken ct = default)
            => Task.FromResult(true);
    }

    private sealed class FakeBaleProvider : IBaleMembershipProvider
    {
        public string Name => "test-provider";

        public Task<BaleCheck> CheckAsync(string mobile, CancellationToken ct = default)
        {
            if (!_baleReady)
                return Task.FromResult(new BaleCheck(BaleStatus.Unknown, "test-not-configured", DateTime.UtcNow));
            return Task.FromResult(BaleMembers.Contains(mobile)
                ? new BaleCheck(BaleStatus.Member, "test-member", DateTime.UtcNow)
                : new BaleCheck(BaleStatus.NotMember, "test-not-member", DateTime.UtcNow));
        }
    }

    private static async Task<int> Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        try
        {
            Console.WriteLine("=== Phase 1 — campaign core tests ===");
            await RecreateDatabaseAsync();

            _app = ApiHost.Build(new[] { "--urls", "http://127.0.0.1:0" }, new Dictionary<string, string?>
            {
                ["ConnectionStrings:CampaignDb"] = TestCs,
                ["Campaign:AutoCreateDatabase"] = "false",
                ["Campaign:SourceId"] = "Holoo1",
                ["Campaign:TargetInvoices"] = "1000",
                ["Campaign:StartDate"] = "2026-01-01",
                ["Campaign:PointsPerAmountToman"] = "500000",
                ["Campaign:PointsBase"] = "gross",
                ["Auth:AdminKey"] = AdminKey,
                [$"Auth:Agents:{AgentA}"] = AgentASecret,
                [$"Auth:Agents:{AgentB}"] = AgentBSecret,
                ["Auth:Roles:campaign.manage"] = RoleCampaign,
                ["Auth:Roles:review.decide"] = RoleReview,
                ["Auth:Roles:lottery.run"] = RoleLottery,
                ["Auth:Roles:lottery.publish"] = RolePublish,
                ["Auth:Roles:privacy.contact"] = RolePrivacy,
                ["Campaign:PrizeToman"] = "10000000",
                ["PublicApi:RateLimitPerMinute"] = "120",
                // بله در آزمون «پیکربندی‌شده» است ولی Bot API آن جعلی تزریق می‌شود.
                ["Bale:Token"] = "test-token",
                ["Bale:Channel"] = "@ebimarket"
            }, services =>
            {
                // در استقرار واقعی، این سرویس با پیکربندی ربات بله جایگزین می‌شود؛
                // در آزمون، ارائه‌دهندهٔ قطعی و بی‌شبکه تزریق می‌شود.
                services.AddSingleton<IBaleMembershipProvider, FakeBaleProvider>();
                // Bot API بله همیشه جعلی است تا آزمون‌ها به شبکهٔ خارجی وابسته نباشند.
                services.AddSingleton<IBaleBotApi>(FakeBale);
            });

            await ApiHost.InitializeAsync(_app);
            await _app.StartAsync();
            _baseUrl = _app.Urls.First();

            using HttpClient http = new() { BaseAddress = new Uri(_baseUrl), Timeout = TimeSpan.FromSeconds(60) };
            _http = http;

            // حالت آزمون بار: EBI_LOAD=800x32 dotnet run --project EbiCampaign.Tests
            if (Environment.GetEnvironmentVariable("EBI_LOAD") is { Length: > 0 } loadSpec)
            {
                string[] parts = loadSpec.Split('x');
                int total = int.Parse(parts[0]);
                int concurrency = parts.Length > 1 ? int.Parse(parts[1]) : 32;
                await RunLoadAsync(total, concurrency);
                Console.WriteLine($"نتیجهٔ آزمون بار: {_ok} OK / {_fail} FAIL");
                return _fail == 0 ? 0 : 1;
            }

            await RunAsync("۱. سلامت سرویس و اتصال دیتابیس", HealthAsync);
            await RunAsync("۲. شروع دوره بدون کلید مدیر رد می‌شود", StartRequiresAdminAsync);
            await RunAsync("۳. شروع دوره و جلوگیری از دورهٔ باز تکراری", StartRoundAsync);
            await RunAsync("۴. فاکتور معتبر از هر صندوق‌داری شمرده می‌شود", AnyCashierAsync);
            await RunAsync("۵. همان فاکتور دو بار شمرده نمی‌شود", DuplicateAsync);
            await RunAsync("۶. ارسال مجدد همان RequestId پاسخ کش‌شده می‌دهد", IdempotencyAsync);
            await RunAsync("۷. استفادهٔ مجدد RequestId با بدنهٔ دیگر رد می‌شود", IdempotencyConflictAsync);
            await RunAsync("۸. امضای نامعتبر رد می‌شود", BadSignatureAsync);
            await RunAsync("۹. امضای منقضی رد می‌شود", StaleTimestampAsync);
            await RunAsync("۱۰. فاکتور قبل از شروع کمپین شمرده نمی‌شود", OutOfScopeAsync);
            await RunAsync("۱۱. فاکتور مشکوک ذخیره می‌شود ولی شمرده نمی‌شود", NeedsReviewAsync);
            await RunAsync("۱۲. دو درخواست هم‌زمان برای فاکتور هزارم", ConcurrencyBoundaryAsync);
            await RunAsync("۱۳. فاکتور ۱۰۰۱ در وضعیت انتظار می‌ماند", AfterCompletionAsync);
            await RunAsync("۱۴. بستن دوره و شروع دورهٔ بعد فقط با مجوز", CloseAndStartAsync);
            await RunAsync("۱۵. فاکتورهای Held خودکار به دورهٔ جدید نمی‌روند", HeldNotAutoAssignedAsync);
            await RunAsync("۱۶. تخصیص صریح Held به دوره", AssignHeldAsync);
            await RunAsync("۱۷. ابطال در دورهٔ باز شمارنده را کم می‌کند", VoidCollectingAsync);
            await RunAsync("۱۸. ابطال پس از تکمیل، شمارش تاریخی را تغییر نمی‌دهد", VoidCompletedAsync);
            await RunAsync("۱۹. افزودن موبایل امتیاز را به‌روز می‌کند و تضاد را می‌گیرد", MobileUpdateAsync);
            await RunAsync("۲۰. عامل نمی‌تواند دوره را دستکاری کند", AgentCannotSetRoundAsync);
            await RunAsync("۲۱. منبع نادرست رد می‌شود", SourceMismatchAsync);
            await RunAsync("۲۲. تکرار در همان دسته یک بار شمرده می‌شود", BatchDuplicateAsync);
            await RunAsync("۲۳. دو عامل هم‌زمان یک FCode را تک‌بار ثبت می‌کنند", ConcurrentDuplicateAsync);
            await RunAsync("۲۴. فاکتور Held وقتی دورهٔ باز نیست", NoOpenRoundAsync);
            await RunAsync("۲۵. علامت مشکوک به ابطال از دورهٔ باز حذف می‌کند", ReviewCollectingAsync);
            await RunAsync("۲۶. علامت مشکوک پس از تکمیل شمارش را تغییر نمی‌دهد", ReviewCompletedAsync);
            await RunAsync("۲۷. دلیل بررسی ذخیره و تأیید مجدد در دورهٔ باز", RevalidateOpenAsync);
            await RunAsync("۲۸. تأیید مجدد فاکتور تاریخی شمارش را تغییر نمی‌دهد", RevalidateCompletedAsync);
            await RunAsync("۲۹. تأیید مجدد فاکتور غیربررسی رد می‌شود", RevalidateWrongStatusAsync);

            Console.WriteLine();
            Console.WriteLine("=== Phase 2 — مسیر تصمیم مدیر (بند ۴.۲) ===");
            await RunAsync("۳۰. تصمیم اعتبار در دورهٔ باز شمارنده را دقیق اصلاح می‌کند", DecisionApproveOpenAsync);
            await RunAsync("۳۱. تصمیم اعتبار فاکتور تاریخی شمارش را تغییر نمی‌دهد", DecisionApproveHistoricalAsync);
            await RunAsync("۳۲. تصمیم ابطال دلیل می‌خواهد و شمارنده را کم می‌کند", DecisionVoidAsync);
            await RunAsync("۳۳. تصمیم تکراری با همان DecisionKey دوباره اجرا نمی‌شود", DecisionIdempotentAsync);
            await RunAsync("۳۴. فاکتور خارج از دامنه با تصمیم مدیریتی معتبر نمی‌شود", DecisionOutOfScopeAsync);
            await RunAsync("۳۵. تصمیم روی دورهٔ نهایی‌شده فقط با مسیر ویژه و دلیل", DecisionFinalizedRoundAsync);

            Console.WriteLine();
            Console.WriteLine("=== Phase 2 — موتور قرعه‌کشی (بند ۵) ===");
            await RunAsync("۳۶. قرعه‌کشی دورهٔ ناتمام رد می‌شود", LotteryRequiresCompletedRoundAsync);
            await RunAsync("۳۷. تثبیت مجموعهٔ ورودی‌ها، تعهد هش و تغییرناپذیری Snapshot", LotteryCommitAsync);
            await RunAsync("۳۸. انتخاب وزنی، افشای Seed و راستی‌آزمایی بازتولیدپذیر", LotteryDrawAndVerifyAsync);
            await RunAsync("۳۹. امتیاز صفر و نبود موبایل شانس نمی‌گیرند", LotteryEligibilityAsync);
            await RunAsync("۴۰. عضویت نامعلوم بله قرعه‌کشی را متوقف می‌کند", LotteryBaleUnknownAsync);
            await RunAsync("۴۱. فاکتور بررسینشده بدون تأیید آگاهانه در قرعه‌کشی نمی‌آید", LotteryReviewGateAsync);
            await RunAsync("۴۲. انتخاب همزمان فقط یک برنده می‌سازد", LotteryConcurrentSelectAsync);
            await RunAsync("۴۳. پس از نهایی‌سازی، دورهٔ بعد فقط با دکمهٔ مدیر", NextRoundGatingAsync);
            await RunAsync("۴۴. بار همزمان ورودی‌ها شمارنده را دقیق نگه می‌دارد", ConcurrentIngestAsync);

            Console.WriteLine();
            Console.WriteLine("=== Phase 3 — API عمومی و حریم خصوصی (بندهای ۵ و ۶) ===");
            await RunAsync("۴۵. وضعیت عمومی کمپین با شمارندهٔ واقعی و کش تازه‌شونده", PublicCampaignAsync);
            await RunAsync("۴۶. دورهٔ تکمیل‌شده تا اعلام نتیجه صفر نمی‌شود", PublicCompletedRoundAsync);
            await RunAsync("۴۷. انتشار عمومی نتیجه فقط با تأیید صریح مدیر", PublishRequiresApprovalAsync);
            await RunAsync("۴۸. فهرست برندگان فقط دادهٔ منتشرشده را نمایش می‌دهد", PublicWinnersAsync);
            await RunAsync("۴۹. سند عمومی قرعه‌کشی برای ممیز مستقل بازتولیدپذیر است", PublicAuditKitAsync);
            await RunAsync("۵۰. هیچ مسیر عمومی دادهٔ خصوصی افشا نمی‌کند", NoPublicLeakAsync);
            await RunAsync("۵۱. مجوزهای سرور‌محور از هم جدا هستند", CapabilitySeparationAsync);
            await RunAsync("۵۲. محدودکنندهٔ نرخ مسیرهای عمومی", RateLimiterAsync);

            Console.WriteLine();
            Console.WriteLine("=== Phase 3 — ربات بله و عضویت (بند ۹) ===");
            await RunAsync("۵۳. ثبت‌نام بله فقط از مسیر امضاشدهٔ عامل", BaleRegisterAuthAsync);
            await RunAsync("۵۴. ثبت‌نام بدون اشتراک‌گذاری شماره و با شمارهٔ نامعتبر رد می‌شود", BaleRegisterValidationAsync);
            await RunAsync("۵۵. ثبت‌نام معتبر، جست‌وجو و جلوگیری از شمارهٔ تکراری", BaleRegisterFlowAsync);
            await RunAsync("۵۶. وضعیت عضویت: عضو، غیرعضو و نامعلوم از هم جدا می‌شوند", BaleStatusAsync);
            await RunAsync("۵۷. پنل بله فقط با مجوز حریم خصوصی و با شمارهٔ سانسورشده", BaleAdminAsync);
            await RunAsync("۵۸. تفسیر پاسخ getChatMember و مدیریت خطا", BaleBotApiParsingAsync);
        }
        catch (Exception ex)
        {
            Console.WriteLine("FATAL: " + ex);
            _fail++;
        }
        finally
        {
            try { if (_app != null) await _app.StopAsync(); } catch { }
            try { await DropDatabaseAsync(); } catch { }
        }

        Console.WriteLine();
        Console.WriteLine($"نتیجه: {_ok} OK / {_fail} FAIL");
        Console.WriteLine(_fail == 0 ? "=== همهٔ آزمون‌های فاز یک OK ===" : "=== شکست وجود دارد ===");
        return _fail == 0 ? 0 : 1;
    }

    // ------------------------------------------------------------------
    //  tests
    // ------------------------------------------------------------------
    // ---------------- بله (فاز ۳) ----------------
    private static async Task BaleRegisterAuthAsync()
    {
        string path = "/api/v1/agent/bale/register";
        string body = JsonSerializer.Serialize(new { mobile = "09121234567", baleUserId = 501L, firstName = "آزمون", contactShared = true }, CampaignService.Json);

        HttpResponseMessage unsigned = await _http.PostAsync(path, new StringContent(body, Encoding.UTF8, "application/json"));
        Check("ثبت‌نام بدون امضای عامل = 401", unsigned.StatusCode == HttpStatusCode.Unauthorized,
            await unsigned.Content.ReadAsStringAsync());

        HttpResponseMessage forged = await PostRawSignedAsync(AgentA, "not-the-secret", path, body);
        Check("امضای نامعتبر = 401", forged.StatusCode == HttpStatusCode.Unauthorized,
            await forged.Content.ReadAsStringAsync());

        HttpResponseMessage ok = await PostRawSignedAsync(AgentA, AgentASecret, path, body);
        Check("امضای معتبر پذیرفته می‌شود", ok.StatusCode == HttpStatusCode.OK, await ok.Content.ReadAsStringAsync());
    }

    private static async Task BaleRegisterValidationAsync()
    {
        string path = "/api/v1/agent/bale/register";
        string notShared = JsonSerializer.Serialize(new { mobile = "09121234567", baleUserId = 502L, firstName = "x", contactShared = false }, CampaignService.Json);
        HttpResponseMessage r1 = await PostRawSignedAsync(AgentA, AgentASecret, path, notShared);
        Check("بدون اشتراک‌گذاری شماره = 400", r1.StatusCode == HttpStatusCode.BadRequest, await r1.Content.ReadAsStringAsync());
        Check("کد خطای contact_not_shared", (await r1.Content.ReadAsStringAsync()).Contains("contact_not_shared"), "");

        string badMobile = JsonSerializer.Serialize(new { mobile = "12345", baleUserId = 503L, firstName = "x", contactShared = true }, CampaignService.Json);
        HttpResponseMessage r2 = await PostRawSignedAsync(AgentA, AgentASecret, path, badMobile);
        Check("شمارهٔ نامعتبر = 400", r2.StatusCode == HttpStatusCode.BadRequest, await r2.Content.ReadAsStringAsync());

        string badUser = JsonSerializer.Serialize(new { mobile = "09121234567", baleUserId = 0L, firstName = "x", contactShared = true }, CampaignService.Json);
        HttpResponseMessage r3 = await PostRawSignedAsync(AgentA, AgentASecret, path, badUser);
        Check("شناسهٔ بلهٔ نامعتبر = 400", r3.StatusCode == HttpStatusCode.BadRequest, await r3.Content.ReadAsStringAsync());

        long rows = await CountAsync("SELECT COUNT(*) FROM dbo.BaleRegistration");
        Check("درخواست‌های نامعتبر ثبت‌نام نمی‌سازند", rows == 0, rows.ToString());
    }

    private static async Task BaleRegisterFlowAsync()
    {
        string path = "/api/v1/agent/bale/register";
        string body = JsonSerializer.Serialize(new { mobile = "+98 912 123 4567", baleUserId = 501L, firstName = "آزمون", contactShared = true }, CampaignService.Json);
        HttpResponseMessage r = await PostRawSignedAsync(AgentA, AgentASecret, path, body);
        string json = await r.Content.ReadAsStringAsync();
        Check("ثبت‌نام معتبر = 200", r.StatusCode == HttpStatusCode.OK, json);
        Check("شماره یکسان‌سازی و سانسور شده است", json.Contains("0912***4567") && !json.Contains("+98"), json);
        Check("کل شماره در پاسخ ثبت‌نام نیست", !json.Contains("09121234567"), json);

        string other = JsonSerializer.Serialize(new { mobile = "09121234567", baleUserId = 502L, firstName = "دیگری", contactShared = true }, CampaignService.Json);
        HttpResponseMessage dup = await PostRawSignedAsync(AgentB, AgentBSecret, path, other);
        Check("شمارهٔ متصل به حساب دیگر = 409", dup.StatusCode == HttpStatusCode.Conflict, await dup.Content.ReadAsStringAsync());

        string lookup = JsonSerializer.Serialize(new { baleUserId = 501L }, CampaignService.Json);
        HttpResponseMessage lk = await PostRawSignedAsync(AgentA, AgentASecret, "/api/v1/agent/bale/lookup", lookup);
        string lkJson = await lk.Content.ReadAsStringAsync();
        Check("جست‌وجو شمارهٔ متصل را برمی‌گرداند", lkJson.Contains("09121234567") && lkJson.Contains("true"), lkJson);

        string unknownLookup = JsonSerializer.Serialize(new { baleUserId = 777L }, CampaignService.Json);
        HttpResponseMessage lk2 = await PostRawSignedAsync(AgentA, AgentASecret, "/api/v1/agent/bale/lookup", unknownLookup);
        Check("حساب بدون ثبت‌نام پیدا نمی‌شود", (await lk2.Content.ReadAsStringAsync()).Contains("false"), "");

        Check("ثبت‌نام در حسابرسی ثبت می‌شود", await CountAsync("SELECT COUNT(*) FROM dbo.AuditLog WHERE Action='bale.registered'") == 1, "");
    }

    private static async Task BaleStatusAsync()
    {
        string path = "/api/v1/agent/bale/register";
        foreach ((long id, string mobile) in new[] { (601L, "09121110001"), (602L, "09121110002"), (603L, "09121110003") })
        {
            string body = JsonSerializer.Serialize(new { mobile, baleUserId = id, firstName = "x", contactShared = true }, CampaignService.Json);
            await PostRawSignedAsync(AgentA, AgentASecret, path, body);
        }

        FakeBale.Results[601] = new BaleMemberResult("member", true, "status=member;is_member=false");
        FakeBale.Results[602] = new BaleMemberResult("left", false, "status=left;is_member=false");
        // 603 عمداً نتیجه‌ای ندارد تا «نامعلوم» آزموده شود.

        async Task<(HttpStatusCode code, string json)> StatusAsync(string mobile)
        {
            string body = JsonSerializer.Serialize(new { mobile }, CampaignService.Json);
            HttpResponseMessage r = await PostRawSignedAsync(AgentA, AgentASecret, "/api/v1/agent/bale/status", body);
            return (r.StatusCode, await r.Content.ReadAsStringAsync());
        }

        (HttpStatusCode c1, string j1) = await StatusAsync("09121110001");
        Check("عضو کانال = member و واجد شرایط", c1 == HttpStatusCode.OK && j1.Contains("\"member\"") && j1.Contains("\"eligible\":true"), j1);
        Check("پاسخ وضعیت شمارهٔ کامل ندارد", !j1.Contains("09121110001"), j1);

        (HttpStatusCode c2, string j2) = await StatusAsync("09121110002");
        Check("غیرعضو = not_member و واجد شرایط نیست", c2 == HttpStatusCode.OK && j2.Contains("not_member") && j2.Contains("\"eligible\":false"), j2);

        (HttpStatusCode c3, string j3) = await StatusAsync("09121110003");
        Check("خطای ارائه‌دهنده = unknown", c3 == HttpStatusCode.OK && j3.Contains("\"unknown\"") && j3.Contains("\"eligible\":false"), j3);
        Check("«نامعلوم» هرگز تأیید عضویت نیست", !j3.Contains("عضویت شما در کانال تأیید شد"), j3);

        (HttpStatusCode c4, string j4) = await StatusAsync("09129999999");
        Check("شمارهٔ ثبت‌نشده = registered:false", c4 == HttpStatusCode.OK && j4.Contains("\"registered\":false"), j4);

        Check("هر بررسی عضویت شاهد ثبت می‌کند",
            await CountAsync("SELECT COUNT(*) FROM dbo.BaleMembershipCheck") >= 3, "");
    }

    private static async Task BaleAdminAsync()
    {
        string body = JsonSerializer.Serialize(new { mobile = "09127770001", baleUserId = 701L, firstName = "x", contactShared = true }, CampaignService.Json);
        await PostRawSignedAsync(AgentA, AgentASecret, "/api/v1/agent/bale/register", body);
        FakeBale.Results[701] = new BaleMemberResult("member", true, "status=member");

        HttpResponseMessage noKey = await _http.GetAsync("/api/v1/admin/bale/registrations");
        Check("فهرست ثبت‌نام‌ها بدون کلید = 401", noKey.StatusCode == HttpStatusCode.Unauthorized, "");

        using HttpRequestMessage manage = new(HttpMethod.Get, "/api/v1/admin/bale/registrations");
        manage.Headers.Add("X-Admin-Key", RoleCampaign);
        HttpResponseMessage wrongCap = await _http.SendAsync(manage);
        Check("کلید مدیریت کمپین به حریم خصوصی دسترسی ندارد", wrongCap.StatusCode == HttpStatusCode.Unauthorized, "");

        using HttpRequestMessage good = new(HttpMethod.Get, "/api/v1/admin/bale/registrations");
        good.Headers.Add("X-Admin-Key", RolePrivacy);
        HttpResponseMessage list = await _http.SendAsync(good);
        string json = await list.Content.ReadAsStringAsync();
        Check("فهرست با مجوز حریم خصوصی = 200", list.StatusCode == HttpStatusCode.OK, json);
        Check("فهرست فقط شمارهٔ سانسورشده دارد", json.Contains("0912***0001") && !json.Contains("09127770001"), json);

        HttpResponseMessage check = await AdminPostKeyAsync("/api/v1/admin/bale/check", RolePrivacy, new { mobile = "09127770001" });
        Check("بررسی عضویت از پنل = 200", check.StatusCode == HttpStatusCode.OK, await check.Content.ReadAsStringAsync());

        HttpResponseMessage denied = await AdminPostKeyAsync("/api/v1/admin/bale/check", RolePublish, new { mobile = "09127770001" });
        Check("کلید انتشار نتیجه اجازهٔ بررسی ندارد", denied.StatusCode == HttpStatusCode.Unauthorized, "");
    }

    private static async Task BaleBotApiParsingAsync()
    {
        static BaleBotApi Build(string json, HttpStatusCode code = HttpStatusCode.OK, bool throwNetwork = false)
        {
            StubHandler handler = new(json, code, throwNetwork);
            HttpClient http = new(handler) { BaseAddress = new Uri("https://bale.test") };
            BaleOptions opt = new() { Token = "t", Channel = "@ebimarket", ApiBase = "https://bale.test" };
            return new BaleBotApi(http, opt, NullLogger<BaleBotApi>.Instance);
        }

        BaleMemberResult member = await Build("{\"ok\":true,\"result\":{\"status\":\"member\"}}").GetChatMemberAsync(1);
        Check("پاسخ member به عضویت تفسیر می‌شود", member.IsMember, member.Evidence);

        BaleMemberResult admin = await Build("{\"ok\":true,\"result\":{\"status\":\"administrator\"}}").GetChatMemberAsync(1);
        Check("مدیر کانال هم عضو است", admin.IsMember, admin.Evidence);

        BaleMemberResult restricted = await Build("{\"ok\":true,\"result\":{\"status\":\"restricted\",\"is_member\":true}}").GetChatMemberAsync(1);
        Check("عضو محدودشده با is_member=true عضو است", restricted.IsMember, restricted.Evidence);

        BaleMemberResult left = await Build("{\"ok\":true,\"result\":{\"status\":\"left\"}}").GetChatMemberAsync(1);
        Check("status=left عضو نیست", !left.IsMember && left.Status == "left", left.Evidence);

        BaleMemberResult error = await Build("{\"ok\":false,\"description\":\"Forbidden\"}", HttpStatusCode.Forbidden).GetChatMemberAsync(1);
        Check("خطای HTTP = نامعلوم", !error.IsMember && error.Status == "unknown", error.Evidence);

        BaleMemberResult network = await Build("{}", HttpStatusCode.OK, throwNetwork: true).GetChatMemberAsync(1);
        Check("خطای شبکه = نامعلوم", !network.IsMember && network.Status == "unknown", network.Evidence);

        BaleBotApi notConfigured = new(new HttpClient { BaseAddress = new Uri("https://bale.test") },
            new BaleOptions(), NullLogger<BaleBotApi>.Instance);
        BaleMemberResult none = await notConfigured.GetChatMemberAsync(1);
        Check("بدون پیکربندی، ارائه‌دهنده هیچ‌وقت عضو اعلام نمی‌کند", !none.IsMember, none.Evidence);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly string _json;
        private readonly HttpStatusCode _code;
        private readonly bool _throw;

        public StubHandler(string json, HttpStatusCode code, bool throwNetwork)
        {
            _json = json;
            _code = code;
            _throw = throwNetwork;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (_throw) throw new HttpRequestException("network down");
            return Task.FromResult(new HttpResponseMessage(_code)
            {
                Content = new StringContent(_json, Encoding.UTF8, "application/json")
            });
        }
    }

    private static async Task<HttpResponseMessage> AdminPostKeyAsync(string path, string key, object payload)
    {
        using HttpRequestMessage msg = new(HttpMethod.Post, path);
        msg.Headers.Add("X-Admin-Key", key);
        msg.Content = JsonContent(payload);
        return await _http.SendAsync(msg);
    }

    private static async Task<long> CountAsync(string sql)
    {
        await using SqlConnection cn = new(TestCs);
        await cn.OpenAsync();
        using SqlCommand cmd = new(sql, cn);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    private static async Task HealthAsync()
    {
        HttpResponseMessage r = await _http.GetAsync("/health");
        string body = await r.Content.ReadAsStringAsync();
        Check("health 200", r.StatusCode == HttpStatusCode.OK, body);
        using JsonDocument d = JsonDocument.Parse(body);
        Check("health.ok=true", d.RootElement.GetProperty("ok").GetBoolean(), body);
    }

    private static async Task StartRequiresAdminAsync()
    {
        HttpResponseMessage r = await _http.PostAsync("/api/v1/admin/rounds/start", JsonContent(new { actor = "x" }));
        Check("بدون کلید = 401", r.StatusCode == HttpStatusCode.Unauthorized, await r.Content.ReadAsStringAsync());
    }

    private static async Task StartRoundAsync()
    {
        HttpResponseMessage r = await AdminPostAsync("/api/v1/admin/rounds/start", new { actor = "owner" });
        Check("شروع دوره = 200", r.StatusCode == HttpStatusCode.OK, await r.Content.ReadAsStringAsync());
        RoundInfo round = await ReadJsonAsync<RoundInfo>(r);
        Check("شمارهٔ دوره = 1", round.RoundNo == 1, round.RoundNo.ToString());
        Check("وضعیت Collecting", round.Status == "Collecting", round.Status);

        HttpResponseMessage again = await AdminPostAsync("/api/v1/admin/rounds/start", new { actor = "owner" });
        Check("دورهٔ باز تکراری = 409", again.StatusCode == HttpStatusCode.Conflict, await again.Content.ReadAsStringAsync());
    }

    private static async Task AnyCashierAsync()
    {
        await StartFreshRoundAsync();
        IngestResponse r1 = await IngestAsync(AgentA, AgentASecret, NewRequest("any-1",
            Invoice(1001, userCode: 9, cashier: "خانم ايماني")));
        IngestResponse r2 = await IngestAsync(AgentB, AgentBSecret, NewRequest("any-2",
            Invoice(1002, userCode: 5, cashier: "خانم م رحيمي")));
        Check("صندوق‌دار ۹ پذیرفته شد", r1.Accepted == 1, Dump(r1));
        Check("صندوق‌دار ۵ پذیرفته شد", r2.Accepted == 1, Dump(r2));
        RoundInfo round = await CurrentRoundAsync();
        Check("شمارنده = 2", round.Count == 2, round.Count.ToString());
    }

    private static async Task DuplicateAsync()
    {
        await StartFreshRoundAsync();
        IngestInvoice inv = Invoice(1101);
        IngestResponse first = await IngestAsync(AgentA, AgentASecret, NewRequest("dup-1", inv));
        IngestResponse second = await IngestAsync(AgentA, AgentASecret, NewRequest("dup-2", inv));
        Check("اولی پذیرفته", first.Accepted == 1, Dump(first));
        Check("دومی تکراری", second.Duplicates == 1 && second.Results[0].Result == "duplicate", Dump(second));
        RoundInfo round = await CurrentRoundAsync();
        Check("شمارنده = 1", round.Count == 1, round.Count.ToString());
    }

    private static async Task IdempotencyAsync()
    {
        await StartFreshRoundAsync();
        IngestRequest req = NewRequest("idem-1", Invoice(1201));
        IngestResponse first = await IngestAsync(AgentA, AgentASecret, req);
        IngestResponse second = await IngestAsync(AgentA, AgentASecret, req);
        Check("پاسخ دوم Cached", second.Cached, Dump(second));
        Check("دوباره شمرده نشد", (await CurrentRoundAsync()).Count == 1, "");
    }

    private static async Task IdempotencyConflictAsync()
    {
        await StartFreshRoundAsync();
        await IngestAsync(AgentA, AgentASecret, NewRequest("idem-2", Invoice(1301)));
        HttpResponseMessage r = await PostIngestRawAsync(AgentA, AgentASecret, "idem-2", NewRequest("idem-2", Invoice(1302)));
        Check("بدنهٔ متفاوت با همان RequestId = 409", r.StatusCode == HttpStatusCode.Conflict, await r.Content.ReadAsStringAsync());
    }

    private static async Task BadSignatureAsync()
    {
        await StartFreshRoundAsync();
        HttpResponseMessage r = await PostIngestRawAsync(AgentA, "wrong-secret", "sig-1", NewRequest("sig-1", Invoice(1401)));
        Check("امضای غلط = 401", r.StatusCode == HttpStatusCode.Unauthorized, await r.Content.ReadAsStringAsync());
    }

    private static async Task StaleTimestampAsync()
    {
        await StartFreshRoundAsync();
        string body = JsonSerializer.Serialize(NewRequest("stale-1", Invoice(1501)), CampaignService.Json);
        long ts = DateTimeOffset.UtcNow.AddMinutes(-30).ToUnixTimeSeconds();
        string hash = Security.Sha256Hex(Encoding.UTF8.GetBytes(body));
        string canonical = Security.Canonical(AgentA, ts.ToString(), "POST", "/api/v1/ingest/invoices", hash);
        using HttpRequestMessage msg = new(HttpMethod.Post, "/api/v1/ingest/invoices");
        msg.Headers.Add("X-Agent-Id", AgentA);
        msg.Headers.Add("X-Timestamp", ts.ToString());
        msg.Headers.Add("X-Signature", Security.HmacHex(AgentASecret, canonical));
        msg.Content = new StringContent(body, Encoding.UTF8, "application/json");
        HttpResponseMessage r = await _http.SendAsync(msg);
        Check("زمان قدیمی = 401", r.StatusCode == HttpStatusCode.Unauthorized, await r.Content.ReadAsStringAsync());
    }

    private static async Task OutOfScopeAsync()
    {
        await StartFreshRoundAsync();
        IngestInvoice inv = Invoice(1601);
        inv.FDate = new DateOnly(2025, 12, 31);
        IngestResponse r = await IngestAsync(AgentA, AgentASecret, NewRequest("old-1", inv));
        Check("OutOfScope", r.OutOfScope == 1 && r.Results[0].Status == "OutOfScope", Dump(r));
        Check("شمارنده 0", (await CurrentRoundAsync()).Count == 0, "");
    }

    private static async Task NeedsReviewAsync()
    {
        await StartFreshRoundAsync();
        IngestInvoice inv = Invoice(1701);
        inv.Status = "review";
        IngestResponse r = await IngestAsync(AgentA, AgentASecret, NewRequest("rev-1", inv));
        Check("NeedsReview", r.NeedsReview == 1, Dump(r));
        Check("شمارنده 0", (await CurrentRoundAsync()).Count == 0, "");
        HttpResponseMessage list = await AdminGetAsync("/api/v1/admin/invoices?status=NeedsReview");
        string body = await list.Content.ReadAsStringAsync();
        Check("در فهرست بررسی دیده می‌شود", body.Contains("\"fCode\":1701"), body);
    }

    private static async Task ConcurrencyBoundaryAsync()
    {
        RoundInfo round = await StartFreshRoundAsync();
        await SeedValidAsync(round.RoundId, 999);

        Task<IngestResponse> t1 = IngestAsync(AgentA, AgentASecret, NewRequest("bound-1", Invoice(1801, mobile: "09120001801")));
        Task<IngestResponse> t2 = IngestAsync(AgentB, AgentBSecret, NewRequest("bound-2", Invoice(1802, mobile: "09120001802")));
        IngestResponse[] results = await Task.WhenAll(t1, t2);

        RoundInfo after = await CurrentRoundAsync();
        Check("شمارنده دقیقاً 1000", after.Count == 1000, after.Count.ToString());
        Check("وضعیت Completed", after.Status == "Completed", after.Status);
        int accepted = results.Sum(x => x.Accepted);
        int held = results.Sum(x => x.Held);
        Check("فقط یکی وارد دوره شد", accepted == 1, $"accepted={accepted}, held={held}");
        Check("دیگری Held شد", held == 1, $"accepted={accepted}, held={held}");
    }

    private static async Task AfterCompletionAsync()
    {
        RoundInfo round = await StartFreshRoundAsync();
        await SeedValidAsync(round.RoundId, 1000, complete: true);
        IngestResponse r = await IngestAsync(AgentA, AgentASecret, NewRequest("after-1", Invoice(1901)));
        Check("Held", r.Held == 1 && r.Results[0].Status == "Held", Dump(r));
        RoundInfo after = await CurrentRoundAsync();
        Check("هنوز همان دوره", after.RoundNo == 1, after.RoundNo.ToString());
        Check("شمارنده 1000", after.Count == 1000, after.Count.ToString());
    }

    private static async Task CloseAndStartAsync()
    {
        RoundInfo round = await StartFreshRoundAsync();
        HttpResponseMessage closeEarly = await AdminPostAsync($"/api/v1/admin/rounds/{round.RoundId}/close", new { actor = "owner" });
        Check("بستن دورهٔ ناتمام = 409", closeEarly.StatusCode == HttpStatusCode.Conflict, await closeEarly.Content.ReadAsStringAsync());

        await SeedValidAsync(round.RoundId, 1000, complete: true);
        HttpResponseMessage close = await AdminPostAsync($"/api/v1/admin/rounds/{round.RoundId}/close", new { actor = "owner" });
        Check("بستن = 200", close.StatusCode == HttpStatusCode.OK, await close.Content.ReadAsStringAsync());

        HttpResponseMessage start = await AdminPostAsync("/api/v1/admin/rounds/start", new { actor = "owner" });
        RoundInfo next = await ReadJsonAsync<RoundInfo>(start);
        Check("دورهٔ 2 باز شد", next.RoundNo == 2 && next.Status == "Collecting", $"no={next.RoundNo} status={next.Status}");

        HttpResponseMessage again = await AdminPostAsync("/api/v1/admin/rounds/start", new { actor = "owner" });
        Check("شروع مجدد = 409", again.StatusCode == HttpStatusCode.Conflict, await again.Content.ReadAsStringAsync());
    }

    private static async Task HeldNotAutoAssignedAsync()
    {
        RoundInfo round = await StartFreshRoundAsync();
        await SeedValidAsync(round.RoundId, 1000, complete: true);
        await IngestAsync(AgentA, AgentASecret, NewRequest("h-1", Invoice(2001)));
        await IngestAsync(AgentA, AgentASecret, NewRequest("h-2", Invoice(2002)));
        await AdminPostAsync($"/api/v1/admin/rounds/{round.RoundId}/close", new { actor = "owner" });
        HttpResponseMessage start = await AdminPostAsync("/api/v1/admin/rounds/start", new { actor = "owner" });
        RoundInfo next = await ReadJsonAsync<RoundInfo>(start);
        Check("دورهٔ جدید با 0 شروع شد", next.Count == 0, next.Count.ToString());
        Check("Held ها منتظر ماندند", next.Held == 2, next.Held.ToString());
    }

    private static async Task AssignHeldAsync()
    {
        RoundInfo round = await StartFreshRoundAsync();
        await SeedValidAsync(round.RoundId, 1000, complete: true);
        await IngestAsync(AgentA, AgentASecret, NewRequest("ha-1", Invoice(2101)));
        await IngestAsync(AgentA, AgentASecret, NewRequest("ha-2", Invoice(2102)));
        await AdminPostAsync($"/api/v1/admin/rounds/{round.RoundId}/close", new { actor = "owner" });
        RoundInfo next = await ReadJsonAsync<RoundInfo>(await AdminPostAsync("/api/v1/admin/rounds/start", new { actor = "owner" }));

        HttpResponseMessage assign = await AdminPostAsync("/api/v1/admin/held/assign",
            new { roundId = next.RoundId, limit = 10, actor = "owner" });
        string body = await assign.Content.ReadAsStringAsync();
        Check("تخصیص 2 فاکتور", assign.StatusCode == HttpStatusCode.OK && body.Contains("\"assigned\":2"), body);
        RoundInfo after = await CurrentRoundAsync();
        Check("شمارنده دورهٔ جدید = 2", after.Count == 2, after.Count.ToString());
        Check("Held صفر شد", after.Held == 0, after.Held.ToString());
    }

    private static async Task VoidCollectingAsync()
    {
        RoundInfo round = await StartFreshRoundAsync();
        IngestResponse r = await IngestAsync(AgentA, AgentASecret, NewRequest("v-1", Invoice(2201)));
        long id = r.Results[0].InvoiceId!.Value;
        HttpResponseMessage v = await AdminPostAsync($"/api/v1/admin/invoices/{id}/void",
            new { reason = "آزمون ابطال", actor = "owner" });
        string body = await v.Content.ReadAsStringAsync();
        Check("ابطال 200", v.StatusCode == HttpStatusCode.OK, body);
        Check("شمارنده کم شد", body.Contains("\"countDecremented\":true"), body);
        RoundInfo after = await CurrentRoundAsync();
        Check("شمارنده = 0", after.Count == 0, after.Count.ToString());
    }

    private static async Task VoidCompletedAsync()
    {
        RoundInfo round = await StartFreshRoundAsync();
        await SeedValidAsync(round.RoundId, 1000, complete: true);
        HttpResponseMessage list = await AdminGetAsync($"/api/v1/admin/invoices?roundId={round.RoundId}&limit=1");
        List<InvoiceRow> rows = await ReadJsonAsync<List<InvoiceRow>>(list);
        long id = rows[0].InvoiceId;
        HttpResponseMessage v = await AdminPostAsync($"/api/v1/admin/invoices/{id}/void",
            new { reason = "ابطال پس از تکمیل", actor = "owner" });
        string body = await v.Content.ReadAsStringAsync();
        Check("نیازمند بررسی", body.Contains("\"requiresReview\":true"), body);
        Check("شمارش تغییر نکرد", body.Contains("\"countDecremented\":false"), body);
        Check("شمارنده همچنان 1000", (await CurrentRoundAsync()).Count == 1000, "");
    }

    private static async Task MobileUpdateAsync()
    {
        await StartFreshRoundAsync();
        IngestInvoice noMobile = Invoice(2301, mobile: "");
        IngestResponse first = await IngestAsync(AgentA, AgentASecret, NewRequest("m-1", noMobile));
        Check("بدون موبایل امتیاز 0", first.Results[0].Status == "Valid", Dump(first));

        IngestInvoice withMobile = Invoice(2301, mobile: "09121232301");
        IngestResponse second = await IngestAsync(AgentA, AgentASecret, NewRequest("m-2", withMobile));
        Check("به‌روزرسانی موبایل", second.Updated == 1 && second.Results[0].Result == "updated", Dump(second));

        IngestInvoice conflict = Invoice(2301, mobile: "09129999999");
        IngestResponse third = await IngestAsync(AgentA, AgentASecret, NewRequest("m-3", conflict));
        Check("تضاد شماره گرفته شد", third.Conflicts == 1 && third.Results[0].Result == "conflict", Dump(third));

        HttpResponseMessage list = await AdminGetAsync("/api/v1/admin/invoices?limit=5");
        string body = await list.Content.ReadAsStringAsync();
        Check("موبایل سانسور شده", body.Contains("0912***2301"), body);
    }

    private static async Task AgentCannotSetRoundAsync()
    {
        RoundInfo round = await StartFreshRoundAsync();
        string body = "{\"requestId\":\"evil-1\",\"sourceId\":\"Holoo1\",\"invoices\":[{\"fCode\":2401,\"fCodeC\":1,\"fType\":2,\"fDate\":\"2026-02-01\",\"userCode\":9,\"gross\":5000000,\"discount\":0,\"net\":5000000,\"mobile\":\"09120002401\",\"roundId\":999,\"status\":\"Valid\"}]}";
        HttpResponseMessage r = await PostRawSignedAsync(AgentA, AgentASecret, "/api/v1/ingest/invoices", body);
        Check("درخواست پذیرفته شد", r.StatusCode == HttpStatusCode.OK, await r.Content.ReadAsStringAsync());
        IngestResponse res = await ReadJsonAsync<IngestResponse>(r);
        Check("دورهٔ سرور اعمال شد", res.Results[0].RoundId == round.RoundId, Dump(res));
    }

    private static async Task SourceMismatchAsync()
    {
        await StartFreshRoundAsync();
        IngestRequest req = NewRequest("src-1", Invoice(2501));
        req.SourceId = "OtherDb";
        HttpResponseMessage r = await PostIngestRawAsync(AgentA, AgentASecret, "src-1", req);
        Check("منبع نادرست = 400", r.StatusCode == HttpStatusCode.BadRequest, await r.Content.ReadAsStringAsync());
    }

    private static async Task BatchDuplicateAsync()
    {
        await StartFreshRoundAsync();
        IngestRequest req = NewRequest("batch-1", Invoice(2601), Invoice(2601));
        IngestResponse r = await IngestAsync(AgentA, AgentASecret, req);
        Check("یکی پذیرفته", r.Accepted == 1, Dump(r));
        Check("یکی تکراری", r.Duplicates == 1, Dump(r));
        Check("شمارنده 1", (await CurrentRoundAsync()).Count == 1, "");
    }

    private static async Task ConcurrentDuplicateAsync()
    {
        await StartFreshRoundAsync();
        IngestInvoice inv = Invoice(2701);
        Task<IngestResponse> a = IngestAsync(AgentA, AgentASecret, NewRequest("cd-1", inv));
        Task<IngestResponse> b = IngestAsync(AgentB, AgentBSecret, NewRequest("cd-2", inv));
        IngestResponse[] rs = await Task.WhenAll(a, b);
        Check("فقط یک رکورد", (await CurrentRoundAsync()).Count == 1, "");
        Check("یکی پذیرفته و یکی تکراری", rs.Sum(x => x.Accepted) == 1 && rs.Sum(x => x.Duplicates) == 1,
            string.Join(" | ", rs.Select(Dump)));
    }

    private static async Task NoOpenRoundAsync()
    {
        RoundInfo round = await StartFreshRoundAsync();
        await SeedValidAsync(round.RoundId, 1000, complete: true);
        await AdminPostAsync($"/api/v1/admin/rounds/{round.RoundId}/close", new { actor = "owner" });
        IngestResponse r = await IngestAsync(AgentA, AgentASecret, NewRequest("noround-1", Invoice(2801)));
        Check("Held بدون دورهٔ باز", r.Held == 1, Dump(r));
    }

    private static async Task ReviewCollectingAsync()
    {
        await StartFreshRoundAsync();
        IngestResponse first = await IngestAsync(AgentA, AgentASecret, NewRequest("rv-1", Invoice(2901)));
        Check("ابتدا پذیرفته شد", first.Accepted == 1, Dump(first));

        IngestInvoice suspect = Invoice(2901);
        suspect.Status = "review";
        IngestResponse second = await IngestAsync(AgentA, AgentASecret, NewRequest("rv-2", suspect));
        Check("مشکوک ثبت شد", second.NeedsReview == 1 && second.Results[0].Result == "review", Dump(second));
        RoundInfo after = await CurrentRoundAsync();
        Check("شمارنده صفر شد", after.Count == 0, after.Count.ToString());

        IngestResponse third = await IngestAsync(AgentA, AgentASecret, NewRequest("rv-3", suspect));
        Check("تکرار علامت، شمارش را تغییر نمی‌دهد", third.Duplicates == 1, Dump(third));
        Check("شمارنده همچنان صفر", (await CurrentRoundAsync()).Count == 0, "");

        HttpResponseMessage list = await AdminGetAsync("/api/v1/admin/invoices?status=NeedsReview&limit=5");
        Check("در فهرست بررسی است", (await list.Content.ReadAsStringAsync()).Contains("\"fCode\":2901"), "");
    }

    private static async Task ReviewCompletedAsync()
    {
        RoundInfo round = await StartFreshRoundAsync();
        await SeedValidAsync(round.RoundId, 1000, complete: true);

        IngestInvoice suspect = Invoice(500001);
        suspect.Status = "review";
        IngestResponse r = await IngestAsync(AgentA, AgentASecret, NewRequest("rv-4", suspect));
        Check("مشکوک پس از تکمیل ثبت شد", r.NeedsReview == 1 && r.Results[0].Result == "review", Dump(r));
        Check("دورهٔ تاریخی حفظ شد", r.Results[0].RoundId == round.RoundId, Dump(r));
        Check("شمارش تاریخی تغییر نکرد", (await CurrentRoundAsync()).Count == 1000, "");
    }

    private static async Task RevalidateOpenAsync()
    {
        await StartFreshRoundAsync();
        IngestResponse first = await IngestAsync(AgentA, AgentASecret, NewRequest("rvo-1", Invoice(3001)));
        long id = first.Results[0].InvoiceId!.Value;

        IngestInvoice suspect = Invoice(3001);
        suspect.Status = "review";
        suspect.ReviewReason = "missing";
        suspect.ReviewEvidence = "source=Holoo1;day=2026-02-01;checks=2";
        IngestResponse second = await IngestAsync(AgentA, AgentASecret, NewRequest("rvo-2", suspect));
        Check("دلیل بررسی و شواهد", second.NeedsReview == 1 && second.Results[0].Result == "review", Dump(second));

        HttpResponseMessage list = await AdminGetAsync("/api/v1/admin/invoices?status=NeedsReview&limit=5");
        string body = await list.Content.ReadAsStringAsync();
        Check("دلیل در فهرست مدیر دیده می‌شود", body.Contains("\"reviewReason\":\"missing\""), body);
        Check("شواهد در فهرست مدیر دیده می‌شود", body.Contains("checks=2"), body);

        HttpResponseMessage rv = await AdminPostAsync($"/api/v1/admin/invoices/{id}/revalidate", new { actor = "owner" });
        RevalidateResult rr = await ReadJsonAsync<RevalidateResult>(rv);
        Check("تأیید مجدد 200", rv.StatusCode == HttpStatusCode.OK, body);
        Check("وضعیت Valid شد", rr.Status == "Valid" && rr.CountChanged, DumpRevalidate(rr));
        Check("شمارنده دوره = 1", (await CurrentRoundAsync()).Count == 1, "");
    }

    private static async Task RevalidateCompletedAsync()
    {
        RoundInfo round = await StartFreshRoundAsync();
        await SeedValidAsync(round.RoundId, 1000, complete: true);

        IngestInvoice suspect = Invoice(500003);
        suspect.Status = "review";
        suspect.ReviewReason = "voided";
        suspect.ReviewEvidence = "source=Holoo1;type=3";
        IngestResponse rev = await IngestAsync(AgentA, AgentASecret, NewRequest("rvc-1", suspect));
        Check("فاکتور تاریخی بررسی شد", rev.NeedsReview == 1, Dump(rev));

        long id = rev.Results[0].InvoiceId!.Value;
        HttpResponseMessage rv = await AdminPostAsync($"/api/v1/admin/invoices/{id}/revalidate", new { actor = "owner" });
        RevalidateResult rr = await ReadJsonAsync<RevalidateResult>(rv);
        Check("تأیید مجدد تاریخی 200", rv.StatusCode == HttpStatusCode.OK, DumpRevalidate(rr));
        Check("شمارش تاریخی تغییر نکرد", !rr.CountChanged && rr.RoundCount == 1000, DumpRevalidate(rr));
        Check("شمارنده همچنان 1000", (await CurrentRoundAsync()).Count == 1000, "");
    }

    private static async Task RevalidateWrongStatusAsync()
    {
        await StartFreshRoundAsync();
        IngestResponse first = await IngestAsync(AgentA, AgentASecret, NewRequest("rvw-1", Invoice(3002)));
        long id = first.Results[0].InvoiceId!.Value;
        HttpResponseMessage rv = await AdminPostAsync($"/api/v1/admin/invoices/{id}/revalidate", new { actor = "owner" });
        Check("تأیید مجدد غیربررسی = 409", rv.StatusCode == HttpStatusCode.Conflict, await rv.Content.ReadAsStringAsync());
    }

    // ------------------------------------------------------------------
    //  Phase 2 — مسیر تصمیم مدیر (بند ۴.۲)
    // ------------------------------------------------------------------
    private static async Task DecisionApproveOpenAsync()
    {
        await StartFreshRoundAsync();
        IngestInvoice suspect = Invoice(3101);
        suspect.Status = "review";
        suspect.ReviewReason = "missing";
        suspect.ReviewEvidence = "source=Holoo1;day=2026-02-01;checks=2";
        IngestResponse ing = await IngestAsync(AgentA, AgentASecret, NewRequest("dec-open-1", suspect));
        long id = ing.Results[0].InvoiceId!.Value;
        Check("فاکتور مشکوک ثبت شد", ing.NeedsReview == 1, Dump(ing));

        HttpResponseMessage r = await AdminPostAsync($"/api/v1/admin/invoices/{id}/decision",
            new { verdict = "valid", actor = "manager-1", reason = "تأیید از تاریخچهٔ هلو", decisionKey = "dk-open-1" });
        DecisionResult d = await ReadJsonAsync<DecisionResult>(r);
        Check("تصمیم اعتبار = 200", r.StatusCode == HttpStatusCode.OK, DumpDecision(d));
        Check("وضعیت Valid و شمارش +1", d.Status == "Valid" && d.CountChanged && d.RoundCount == 1, DumpDecision(d));
        Check("شمارندهٔ دوره = 1", (await CurrentRoundAsync()).Count == 1, "");

        InvoiceDetail detail = await InvoiceDetailAsync(id);
        Check("تاریخچه یک تصمیم دارد", detail.Decisions.Count == 1, detail.Decisions.Count.ToString());
        Check("هویت و مبنا و زمان ثبت شد",
            detail.Decisions[0].Actor == "manager-1" && detail.Decisions[0].Basis == "manager"
            && detail.Decisions[0].DecidedAt.Year >= 2026,
            detail.Decisions[0].Basis + "/" + detail.Decisions[0].Actor);
        Check("اثر شمارشی در تاریخچه ثبت شد",
            detail.Decisions[0].CountEffected && detail.Decisions[0].CountBefore == 0
            && detail.Decisions[0].CountAfter == 1 && detail.Decisions[0].PrevStatus == "NeedsReview",
            $"{detail.Decisions[0].CountBefore}→{detail.Decisions[0].CountAfter} از {detail.Decisions[0].PrevStatus}");
        Check("شواهد تطبیق در تاریخچه باقی ماند",
            detail.Decisions[0].Evidence != null && detail.Decisions[0].Evidence!.Contains("checks=2"),
            detail.Decisions[0].Evidence ?? "-");
        Check("دورهٔ نهایی‌نشده و فراگرفت ندارد", !detail.RoundFinalized && detail.LotteryRunId == null, "");
        Check("سابقهٔ تصمیم در حسابرسی ثبت شد", await CountAuditAsync("invoice.decision") == 1, "");
    }

    private static async Task DecisionApproveHistoricalAsync()
    {
        RoundInfo round = await StartFreshRoundAsync();
        await SeedValidAsync(round.RoundId, 1000, complete: true);

        IngestInvoice suspect = Invoice(500010);
        suspect.Status = "review";
        suspect.ReviewReason = "voided";
        IngestResponse ing = await IngestAsync(AgentA, AgentASecret, NewRequest("dec-hist-1", suspect));
        long id = ing.Results[0].InvoiceId!.Value;
        Check("فاکتور تاریخی برچسب دوره را حفظ کرد", ing.Results[0].RoundId == round.RoundId, Dump(ing));

        HttpResponseMessage r = await AdminPostAsync($"/api/v1/admin/invoices/{id}/decision",
            new { verdict = "valid", actor = "manager-2", reason = "در تاریخچهٔ هلو معتبر است", decisionKey = "dk-hist-1" });
        DecisionResult d = await ReadJsonAsync<DecisionResult>(r);
        Check("اعتبار تاریخی = 200", r.StatusCode == HttpStatusCode.OK, DumpDecision(d));
        Check("شمارش تاریخی تغییر نکرد", !d.CountChanged && d.RoundCount == 1000, DumpDecision(d));
        Check("شمارندهٔ دوره همچنان 1000", (await CurrentRoundAsync()).Count == 1000, "");
        Check("شمارهٔ فاکتور در پاسخ برگشت", d.FCode == 500010, d.FCode.ToString());
    }

    private static async Task DecisionVoidAsync()
    {
        await StartFreshRoundAsync();
        IngestResponse ing = await IngestAsync(AgentA, AgentASecret, NewRequest("dec-void-1", Invoice(3201)));
        long id = ing.Results[0].InvoiceId!.Value;
        Check("شمارنده = 1", (await CurrentRoundAsync()).Count == 1, "");

        HttpResponseMessage noReason = await AdminPostAsync($"/api/v1/admin/invoices/{id}/decision",
            new { verdict = "void", actor = "manager-3" });
        Check("ابطال بدون دلیل = 400", noReason.StatusCode == HttpStatusCode.BadRequest,
            await noReason.Content.ReadAsStringAsync());
        Check("شمارنده تغییر نکرد", (await CurrentRoundAsync()).Count == 1, "");

        HttpResponseMessage ok = await AdminPostAsync($"/api/v1/admin/invoices/{id}/decision",
            new { verdict = "void", actor = "manager-3", reason = "در هلو ابطال شده است", decisionKey = "dk-void-1" });
        DecisionResult d = await ReadJsonAsync<DecisionResult>(ok);
        Check("تصمیم ابطال = 200", ok.StatusCode == HttpStatusCode.OK, DumpDecision(d));
        Check("وضعیت Voided و شمارش -1", d.Status == "Voided" && d.CountChanged, DumpDecision(d));
        Check("شمارنده صفر شد", (await CurrentRoundAsync()).Count == 0, "");

        InvoiceDetail detail = await InvoiceDetailAsync(id);
        Check("دلیل و ابطال‌کننده در فاکتور ثبت شد",
            detail.Invoice.VoidReason == "در هلو ابطال شده است" && detail.Invoice.Status == "Voided",
            detail.Invoice.VoidReason ?? "-");
        Check("تاریخچه تصمیم ابطال ثبت شد",
            detail.Decisions.Count == 1 && detail.Decisions[0].Verdict == "void"
            && detail.Decisions[0].NewStatus == "Voided",
            detail.Decisions.Count.ToString());

        HttpResponseMessage bad = await AdminPostAsync($"/api/v1/admin/invoices/{id}/decision", new { verdict = "maybe" });
        Check("verdict نامعتبر = 400", bad.StatusCode == HttpStatusCode.BadRequest, await bad.Content.ReadAsStringAsync());
    }

    private static async Task DecisionIdempotentAsync()
    {
        await StartFreshRoundAsync();
        IngestInvoice suspect = Invoice(3301);
        suspect.Status = "review";
        suspect.ReviewReason = "invalid";
        IngestResponse ing = await IngestAsync(AgentA, AgentASecret, NewRequest("dec-idem-1", suspect));
        long id = ing.Results[0].InvoiceId!.Value;
        IngestInvoice suspect2 = Invoice(3302);
        suspect2.Status = "review";
        suspect2.ReviewReason = "invalid";
        IngestResponse other = await IngestAsync(AgentA, AgentASecret, NewRequest("dec-idem-2", suspect2));
        long otherId = other.Results[0].InvoiceId!.Value;

        object payload = new
        {
            verdict = "valid", actor = "manager-4", reason = "تأیید شد", decisionKey = "dk-idem-1"
        };
        DecisionResult first = await ReadJsonAsync<DecisionResult>(
            await AdminPostAsync($"/api/v1/admin/invoices/{id}/decision", payload));
        Check("اجرای اول تصمیم", !first.AlreadyDecided && first.CountChanged, DumpDecision(first));

        DecisionResult second = await ReadJsonAsync<DecisionResult>(
            await AdminPostAsync($"/api/v1/admin/invoices/{id}/decision", payload));
        Check("اجرای دوم علامت‌دار AlreadyDecided", second.AlreadyDecided && !second.CountChanged, DumpDecision(second));
        Check("شمارنده دو بار زیاد نشد", (await CurrentRoundAsync()).Count == 1, "");
        Check("فقط یک رکورد تصمیم ثبت شد", (await InvoiceDetailAsync(id)).Decisions.Count == 1, "");

        HttpResponseMessage reuse = await AdminPostAsync($"/api/v1/admin/invoices/{otherId}/decision", payload);
        Check("همان DecisionKey برای فاکتور دیگر = 409", reuse.StatusCode == HttpStatusCode.Conflict,
            await reuse.Content.ReadAsStringAsync());
    }

    private static async Task DecisionOutOfScopeAsync()
    {
        await StartFreshRoundAsync();
        IngestInvoice old = Invoice(3401);
        old.FDate = new DateOnly(2025, 12, 31);
        IngestResponse ing = await IngestAsync(AgentA, AgentASecret, NewRequest("dec-oos-1", old));
        long id = ing.Results[0].InvoiceId!.Value;
        Check("فاکتور خارج از دامنه ذخیره شد", ing.OutOfScope == 1, Dump(ing));

        HttpResponseMessage r = await AdminPostAsync($"/api/v1/admin/invoices/{id}/decision",
            new { verdict = "valid", actor = "m", reason = "تأیید دستی", decisionKey = "dk-oos-1" });
        Check("تصمیم روی خارج از دامنه = 409", r.StatusCode == HttpStatusCode.Conflict,
            await r.Content.ReadAsStringAsync());
        Check("شمارنده دست‌نخورده", (await CurrentRoundAsync()).Count == 0, "");
    }

    private static async Task DecisionFinalizedRoundAsync()
    {
        RoundInfo round = await StartFreshRoundAsync();
        await SeedValidAsync(round.RoundId, 1000, complete: true);
        _baleReady = true;
        BaleMembers.Add(SeedMobile);
        LotteryRunInfo run = await PrepareLotteryAsync(round.RoundId);
        await AdminPostAsync($"/api/v1/admin/lottery/{run.RunId}/select", new { actor = "owner" });
        LotteryRunInfo fin = await ReadJsonAsync<LotteryRunInfo>(
            await AdminPostAsync($"/api/v1/admin/lottery/{run.RunId}/finalize", new { actor = "owner" }));
        Check("دورهٔ آزمایشی نهایی شد", fin.Status == "Finalized", fin.Status);

        IngestInvoice suspect = Invoice(500020);
        suspect.Status = "review";
        suspect.ReviewReason = "voided";
        IngestResponse ing = await IngestAsync(AgentA, AgentASecret, NewRequest("dec-fin-1", suspect));
        long id = ing.Results[0].InvoiceId!.Value;

        HttpResponseMessage blocked = await AdminPostAsync($"/api/v1/admin/invoices/{id}/decision",
            new { verdict = "valid", actor = "manager-5", reason = "اصلاح" });
        Check("تغییر پس از نهایی‌سازی بدون force = 409", blocked.StatusCode == HttpStatusCode.Conflict,
            await blocked.Content.ReadAsStringAsync());

        HttpResponseMessage noReason = await AdminPostAsync($"/api/v1/admin/invoices/{id}/decision",
            new { verdict = "valid", actor = "manager-5", force = true });
        Check("مسیر ویژه بدون دلیل = 400", noReason.StatusCode == HttpStatusCode.BadRequest,
            await noReason.Content.ReadAsStringAsync());

        HttpResponseMessage forced = await AdminPostAsync($"/api/v1/admin/invoices/{id}/decision",
            new { verdict = "valid", actor = "owner", reason = "اصلاح مستند پس از نهایی‌سازی", force = true, decisionKey = "dk-forced-1" });
        DecisionResult d = await ReadJsonAsync<DecisionResult>(forced);
        Check("مسیر ویژهٔ حسابرسی‌شده = 200", forced.StatusCode == HttpStatusCode.OK, DumpDecision(d));
        Check("پرچم Forced و FinalizedRound ثبت شد", d.Forced && d.FinalizedRound, DumpDecision(d));
        Check("شمارش دورهٔ نهایی تغییر نکرد", !d.CountChanged && d.RoundCount == 1000, DumpDecision(d));

        InvoiceDetail detail = await InvoiceDetailAsync(id);
        Check("تاریخچه مبنا را forced ثبت کرد",
            detail.Decisions.Count == 1 && detail.Decisions[0].Basis == "forced" && detail.Decisions[0].Forced,
            detail.Decisions.Count == 1 ? detail.Decisions[0].Basis : "-");
        Check("فراگرفت نهایی به عنوان مرجع برگشت", detail.LotteryRunId == run.RunId && detail.RoundFinalized, "");

        LotteryRunInfo after = await GetRunAsync(run.RunId);
        Check("نتیجهٔ دوره برای بازبینی علامت‌گذاری شد", after.ResultsQuestioned, "ResultsQuestioned=false");
        Check("سابقهٔ مسیر ویژه در حسابرسی ثبت شد", await CountAuditAsync("invoice.decision.forced") == 1, "");
        Check("برندهٔ نهایی جابه‌جا نشد", after.WinnerFCode == fin.WinnerFCode, "");
    }

    // ------------------------------------------------------------------
    //  Phase 2 — موتور قرعه‌کشی (بند ۵)
    // ------------------------------------------------------------------
    private static async Task LotteryRequiresCompletedRoundAsync()
    {
        RoundInfo round = await StartFreshRoundAsync();
        HttpResponseMessage r = await AdminPostAsync($"/api/v1/admin/rounds/{round.RoundId}/lottery", new { actor = "owner" });
        Check("قرعه‌کشی دورهٔ ناتمام = 409", r.StatusCode == HttpStatusCode.Conflict,
            await r.Content.ReadAsStringAsync());
        Check("کد خطای round_not_completed", (await r.Content.ReadAsStringAsync()).Contains("round_not_completed"), "");
        HttpResponseMessage list = await AdminGetAsync("/api/v1/admin/lottery");
        Check("فراگرفتی ساخته نشد", (await list.Content.ReadAsStringAsync()).Trim() == "[]", "");
    }

    private static async Task LotteryCommitAsync()
    {
        RoundInfo round = await StartFreshRoundAsync();
        await SeedValidAsync(round.RoundId, 1000, complete: true);
        _baleReady = true;
        BaleMembers.Add(SeedMobile);

        LotteryRunInfo run = await PrepareLotteryAsync(round.RoundId);
        Check("وضعیت Committed", run.Status == "Committed", run.Status + "/" + run.ReviewReason);
        Check("هر فاکتور یک ورودی مستقل", run.EntryCount == 1000 && run.EligibleCount == 1000,
            $"entries={run.EntryCount} eligible={run.EligibleCount}");
        Check("وزن کل = مجموع امتیازها", run.TotalWeight == 1000, run.TotalWeight.ToString());
        Check("الگوریتم اعلام‌شده", run.Algorithm == "ebi-lottery-weighted-v2", run.Algorithm);
        Check("Seed پیش از انتخاب افشا نمی‌شود", run.SeedHex == null, run.SeedHex ?? "null");
        Check("تعهد هش منتشر شد", run.CommitmentHash.Length == 64 && run.CommitmentHash != run.EntriesHash,
            run.CommitmentHash);
        Check("هش عمومی جدا از هش داخلی محرمانه است",
            run.PublicEntriesHash is { Length: 64 } && run.PublicEntriesHash != run.EntriesHash,
            run.PublicEntriesHash ?? "null");

        LotteryRunInfo again = await PrepareLotteryAsync(round.RoundId);
        Check("تثبیت تکراری همان فراگرفت را برمی‌گرداند", again.RunId == run.RunId, $"{again.RunId}/{run.RunId}");

        LotteryRunInfo full = await GetRunAsync(run.RunId);
        Check("Snapshot شامل ورودی‌هاست", full.Entries != null && full.Entries.Count == 1000, "");
        Check("شمارهٔ موبایل در Snapshot پوشانده شده",
            full.Entries!.All(e => e.Mobile == null || e.Mobile.Contains("***")), "");

        bool blocked = false;
        try
        {
            await ExecuteSqlAsync("UPDATE dbo.LotteryEntries SET Points=99 WHERE RunId=@r", ("@r", run.RunId));
        }
        catch (SqlException) { blocked = true; }
        Check("تغییر ورودی‌های تثبیت‌شده رد می‌شود", blocked, "UPDATE روی Snapshot پذیرفته شد");

        LotteryVerifyResult v = await ReadJsonAsync<LotteryVerifyResult>(
            await AdminGetAsync($"/api/v1/admin/lottery/{run.RunId}/verify"));
        Check("هش داخلی مجموعه از داده‌های Snapshot بازسازی شد", v.EntriesHashMatches, "EntriesHashMatches=false");
        Check("پیش از افشای Seed هش عمومی بازتولید نمی‌شود", !v.PublicEntriesHashMatches, "");
        Check("پیش از انتخاب Seed افشا نشده", !v.SeedRevealed && !v.CommitmentMatches, "");
        Check("سابقهٔ تثبیت در حسابرسی ثبت شد", await CountAuditAsync("lottery.prepare") == 1, "");
    }

    private static async Task LotteryDrawAndVerifyAsync()
    {
        RoundInfo round = await StartFreshRoundAsync();
        await SeedValidAsync(round.RoundId, 1000, complete: true);
        _baleReady = true;
        BaleMembers.Add(SeedMobile);
        LotteryRunInfo prepared = await PrepareLotteryAsync(round.RoundId);

        HttpResponseMessage r = await AdminPostAsync($"/api/v1/admin/lottery/{prepared.RunId}/select", new { actor = "owner" });
        LotteryRunInfo run = await ReadJsonAsync<LotteryRunInfo>(r);
        Check("انتخاب برنده = 200", r.StatusCode == HttpStatusCode.OK, DumpRun(run));
        Check("وضعیت Selected", run.Status == "Selected", run.Status);
        Check("Seed پس از انتخاب افشا شد", run.SeedHex is { Length: 64 }, run.SeedHex ?? "null");
        Check("ورودی برنده ثبت شد", run.WinnerFCode.HasValue && run.WinnerEntryNo.HasValue && run.WinnerPoints == 1,
            DumpRun(run));
        Check("شمارهٔ برنده در پاسخ پوشانده شد", run.WinnerMobile != null && run.WinnerMobile.Contains("***"),
            run.WinnerMobile ?? "-");

        LotteryRunInfo second = await ReadJsonAsync<LotteryRunInfo>(
            await AdminPostAsync($"/api/v1/admin/lottery/{prepared.RunId}/select", new { actor = "other" }));
        Check("انتخاب دوباره برنده را تغییر نمی‌دهد",
            second.WinnerEntryNo == run.WinnerEntryNo && second.WinnerFCode == run.WinnerFCode, DumpRun(second));

        LotteryVerifyResult v = await ReadJsonAsync<LotteryVerifyResult>(
            await AdminGetAsync($"/api/v1/admin/lottery/{prepared.RunId}/verify"));
        Check("تعهد با Seed افشاشده مطابقت دارد", v.CommitmentMatches, "CommitmentMatches=false");
        Check("برنده با Seed بازتولید شد", v.WinnerReproduces && v.RecomputedWinnerFCode == run.WinnerFCode,
            $"{v.RecomputedWinnerFCode}/{run.WinnerFCode}");

        // بازتولید مستقل فقط از سند حسابرسی (بدون دسترسی به پایگاه داده)
        LotteryRunInfo receipt = await GetRunAsync(prepared.RunId);
        List<LotteryEntryInfo> eligible = receipt.Entries!
            .Where(e => e.Eligibility == "eligible" && e.Weight > 0)
            .OrderBy(e => e.EntryNo).ToList();
        byte[] seed = Convert.FromHexString(run.SeedHex!);
        int idx = LotteryEngine.PickWeighted(seed, LotteryEngine.Context(receipt.RoundNo, receipt.PublicEntriesHash!),
            eligible.Select(e => e.Weight).ToList(), out long draw, out long total);
        Check("سند حسابرسی برای بازتولید کافی است",
            eligible[idx].EntryNo == run.WinnerEntryNo && draw == run.WinnerDrawValue && total == run.WinnerTotalWeight,
            $"entry={eligible[idx].EntryNo} draw={draw} total={total}");
        Check("سابقهٔ انتخاب در حسابرسی ثبت شد", await CountAuditAsync("lottery.select") == 1, "");

        LotteryRunInfo fin = await ReadJsonAsync<LotteryRunInfo>(
            await AdminPostAsync($"/api/v1/admin/lottery/{prepared.RunId}/finalize", new { actor = "owner" }));
        Check("نهایی‌سازی نتیجه = 200", fin.Status == "Finalized" && fin.FinalizedAt.HasValue, DumpRun(fin));

        HttpResponseMessage wc = await AdminGetAsync($"/api/v1/admin/lottery/{prepared.RunId}/winner-contact");
        string wcBody = await wc.Content.ReadAsStringAsync();
        Check("شمارهٔ کامل برنده با مسیر مجاز", wc.StatusCode == HttpStatusCode.OK
            && wcBody.Contains($"\"winnerMobile\":\"{SeedMobile}\""), wcBody);
        Check("هر دسترسی به شماره در حسابرسی ثبت شد", await CountAuditAsync("lottery.winner_contact") == 1, "");

        LotteryVerifyResult after = await ReadJsonAsync<LotteryVerifyResult>(
            await AdminGetAsync($"/api/v1/admin/lottery/{prepared.RunId}/verify"));
        Check("راستی‌آزمایی پس از نهایی‌سازی کامل است",
            after.SeedRevealed && after.EntriesHashMatches && after.PublicEntriesHashMatches
            && after.CommitmentMatches && after.WinnerReproduces, "");
    }

    private static async Task LotteryEligibilityAsync()
    {
        RoundInfo round = await StartFreshRoundAsync();
        await SeedShapeAsync(round.RoundId, 0, 998, _ => (1, SeedMobile));
        await SeedShapeAsync(round.RoundId, 998, 1, _ => (0, SeedMobile));       // امتیاز صفر
        await SeedShapeAsync(round.RoundId, 999, 1, _ => (5, null));             // بدون موبایل
        await MarkRoundCompleteAsync(round.RoundId, 1000);
        _baleReady = true;
        BaleMembers.Add(SeedMobile);

        LotteryRunInfo run = await PrepareLotteryAsync(round.RoundId);
        Check("همهٔ ۱۰۰۰ فاکتور در Snapshot هستند", run.EntryCount == 1000, run.EntryCount.ToString());
        Check("فقط ورودی‌های واجد شرایط شانس دارند", run.EligibleCount == 998, run.EligibleCount.ToString());
        Check("وزن کل امتیاز صفر و ورودی بی‌موبایل را نشمرد", run.TotalWeight == 998, run.TotalWeight.ToString());

        LotteryRunInfo full = await GetRunAsync(run.RunId);
        LotteryEntryInfo zero = full.Entries!.Single(e => e.FCode == 700998);
        LotteryEntryInfo noMobile = full.Entries!.Single(e => e.FCode == 700999);
        Check("وزن هر ورودی واجد شرایط = امتیازش",
            full.Entries!.Count(e => e.Eligibility == "eligible" && e.Weight == e.Points) == 998, "");
        Check("امتیاز صفر → zero_weight با وزن صفر",
            zero.Eligibility == "zero_weight" && zero.Weight == 0, $"{zero.Eligibility}/{zero.Weight}");
        Check("نبود موبایل → no_mobile با وزن صفر",
            noMobile.Eligibility == "no_mobile" && noMobile.Weight == 0, $"{noMobile.Eligibility}/{noMobile.Weight}");

        LotteryRunInfo drawn = await ReadJsonAsync<LotteryRunInfo>(
            await AdminPostAsync($"/api/v1/admin/lottery/{run.RunId}/select", new { actor = "owner" }));
        Check("برنده از ورودی‌های واجد شرایط است",
            drawn.WinnerFCode != 700998 && drawn.WinnerFCode != 700999 && drawn.WinnerPoints == 1,
            DumpRun(drawn));
    }

    private static async Task LotteryBaleUnknownAsync()
    {
        RoundInfo round = await StartFreshRoundAsync();
        await SeedValidAsync(round.RoundId, 1000, complete: true);
        _baleReady = false; // ارائه‌دهندهٔ تنظیم‌نشده → نامعلوم

        LotteryRunInfo run = await PrepareLotteryAsync(round.RoundId);
        Check("عضویت نامعلوم → نیازمند بررسی مدیریتی",
            run.Status == "RequiresReview" && run.ReviewReason == "bale_membership_unverified",
            run.Status + "/" + run.ReviewReason);
        Check("هیچ ورودی واجد شرایطی ساخته نشد", run.EligibleCount == 0 && run.TotalWeight == 0,
            $"{run.EligibleCount}/{run.TotalWeight}");
        Check("وضعیت عضویت در Snapshot ثبت شد",
            (await GetRunAsync(run.RunId)).Entries!.All(e => e.BaleStatus == "unknown"), "");

        HttpResponseMessage sel = await AdminPostAsync($"/api/v1/admin/lottery/{run.RunId}/select", new { actor = "owner" });
        Check("انتخاب با عضویت نامعلوم = 409", sel.StatusCode == HttpStatusCode.Conflict,
            await sel.Content.ReadAsStringAsync());

        HttpResponseMessage ab = await AdminPostAsync($"/api/v1/admin/lottery/{run.RunId}/abort", new { actor = "owner" });
        Check("لغو بدون دلیل = 400", ab.StatusCode == HttpStatusCode.BadRequest, await ab.Content.ReadAsStringAsync());
        HttpResponseMessage ab2 = await AdminPostAsync($"/api/v1/admin/lottery/{run.RunId}/abort",
            new { actor = "owner", reason = "رفع ابهام عضویت بله" });
        LotteryRunInfo aborted = await ReadJsonAsync<LotteryRunInfo>(ab2);
        Check("لغو با دلیل = 200", aborted.Status == "Aborted", aborted.Status);
        Check("سابقهٔ لغو در حسابرسی ثبت شد", await CountAuditAsync("lottery.abort") == 1, "");

        _baleReady = true;
        BaleMembers.Add(SeedMobile);
        LotteryRunInfo retry = await PrepareLotteryAsync(round.RoundId);
        Check("پس از لغو، فراگرفت جدید ساخته می‌شود",
            retry.Status == "Committed" && retry.RunId != run.RunId, $"{retry.Status}/{retry.RunId}");
    }

    private static async Task LotteryReviewGateAsync()
    {
        RoundInfo round = await StartFreshRoundAsync();
        await SeedValidAsync(round.RoundId, 1000, complete: false);
        await MarkRoundCompleteAsync(round.RoundId, 1000);

        // فاکتور موجود در همان دورهٔ تاریخی مشکوک علامت می‌خورد تا برچسب دوره حفظ شود.
        IngestInvoice suspect = Invoice(500030);
        suspect.Status = "review";
        suspect.ReviewReason = "missing";
        IngestResponse ing = await IngestAsync(AgentA, AgentASecret, NewRequest("gate-1", suspect));
        Check("فاکتور بررسینشده در دوره ثبت شد",
            ing.NeedsReview == 1 && ing.Results[0].RoundId == round.RoundId, Dump(ing));
        _baleReady = true;
        BaleMembers.Add(SeedMobile);

        HttpResponseMessage blocked = await AdminPostAsync($"/api/v1/admin/rounds/{round.RoundId}/lottery",
            new { actor = "owner", acknowledgeReviews = false });
        Check("قرعه‌کشی با بررسی حل‌نشده = 409", blocked.StatusCode == HttpStatusCode.Conflict,
            await blocked.Content.ReadAsStringAsync());
        Check("کد خطای unresolved_reviews", (await blocked.Content.ReadAsStringAsync()).Contains("unresolved_reviews"), "");

        LotteryRunInfo run = await PrepareLotteryAsync(round.RoundId, acknowledge: true);
        Check("با تأیید آگاهانه، قرعه‌کشی انجام می‌شود", run.Status == "Committed", run.Status);
        Check("تأیید آگاهانه در سند ثبت شد", run.AcknowledgedReviews == 1, run.AcknowledgedReviews.ToString());
        LotteryRunInfo full = await GetRunAsync(run.RunId);
        Check("فاکتور بررسینشده وارد مجموعهٔ قرعه‌کشی نشد",
            full.Entries!.All(e => e.FCode != 500030) && full.EntryCount == 999, full.EntryCount.ToString());
    }

    private static async Task LotteryConcurrentSelectAsync()
    {
        RoundInfo round = await StartFreshRoundAsync();
        await SeedValidAsync(round.RoundId, 1000, complete: true);
        _baleReady = true;
        BaleMembers.Add(SeedMobile);
        LotteryRunInfo prepared = await PrepareLotteryAsync(round.RoundId);

        HttpResponseMessage[] responses = await Task.WhenAll(Enumerable.Range(0, 4).Select(i =>
            AdminPostAsync($"/api/v1/admin/lottery/{prepared.RunId}/select", new { actor = "owner-" + i })));
        List<LotteryRunInfo> runs = new();
        foreach (HttpResponseMessage r in responses)
        {
            Check("انتخاب همزمان = 200", r.StatusCode == HttpStatusCode.OK, await r.Content.ReadAsStringAsync());
            runs.Add(await ReadJsonAsync<LotteryRunInfo>(r));
        }
        Check("همهٔ درخواست‌ها یک ورودی برنده دادند",
            runs.All(x => x.WinnerEntryNo == runs[0].WinnerEntryNo && x.WinnerFCode == runs[0].WinnerFCode),
            string.Join(" | ", runs.Select(DumpRun)));
        Check("فقط یک رکورد انتخاب در حسابرسی", await CountAuditAsync("lottery.select") == 1, "");
        LotteryRunInfo persisted = await GetRunAsync(prepared.RunId);
        Check("برندهٔ ثبت‌شده همان برندهٔ انتخابی است",
            persisted.WinnerEntryNo == runs[0].WinnerEntryNo, DumpRun(persisted));
    }

    private static async Task NextRoundGatingAsync()
    {
        RoundInfo round = await StartFreshRoundAsync();
        await SeedValidAsync(round.RoundId, 1000, complete: true);
        _baleReady = true;
        BaleMembers.Add(SeedMobile);
        LotteryRunInfo run = await PrepareLotteryAsync(round.RoundId);
        await AdminPostAsync($"/api/v1/admin/lottery/{run.RunId}/select", new { actor = "owner" });
        await AdminPostAsync($"/api/v1/admin/lottery/{run.RunId}/finalize", new { actor = "owner" });

        IngestResponse held = await IngestAsync(AgentA, AgentASecret, NewRequest("next-1", Invoice(4200)));
        Check("فاکتور پس از پایان دوره در انتظار می‌ماند", held.Held == 1 && held.Accepted == 0, Dump(held));
        HttpResponseMessage heldList = await AdminGetAsync("/api/v1/admin/held");
        Check("فاکتور انتظار در گزارش مدیر دیده می‌شود",
            (await heldList.Content.ReadAsStringAsync()).Contains("\"fCode\":4200"), "");

        LotteryRunInfo again = await PrepareLotteryAsync(round.RoundId);
        Check("دورهٔ نهایی‌شده قرعه‌کشی دوم نمی‌سازد",
            again.RunId == run.RunId && again.Status == "Finalized", $"{again.RunId}/{again.Status}");

        HttpResponseMessage closed = await AdminPostAsync($"/api/v1/admin/rounds/{round.RoundId}/close", new { actor = "owner" });
        Check("بستن دورهٔ نهایی‌شده = 200", closed.StatusCode == HttpStatusCode.OK,
            await closed.Content.ReadAsStringAsync());
        RoundInfo next = await StartFreshRoundAsync();
        Check("دورهٔ جدید با شمارندهٔ صفر", next.Count == 0 && next.RoundNo == round.RoundNo + 1,
            $"count={next.Count} no={next.RoundNo}");
        HttpResponseMessage heldList2 = await AdminGetAsync("/api/v1/admin/held");
        Check("انتظار خودکار به دورهٔ جدید منتقل نشد",
            (await heldList2.Content.ReadAsStringAsync()).Contains("\"fCode\":4200"), "");

        HttpResponseMessage assign = await AdminPostAsync("/api/v1/admin/held/assign",
            new { roundId = next.RoundId, limit = 100, actor = "owner" });
        string assignBody = await assign.Content.ReadAsStringAsync();
        Check("تخصیص صریح انتظار به دورهٔ جدید", assignBody.Contains("\"assigned\":1"), assignBody);
        Check("شمارندهٔ دورهٔ جدید 1 شد", (await CurrentRoundAsync()).Count == 1, "");
    }

    private static async Task ConcurrentIngestAsync()
    {
        await StartFreshRoundAsync();
        Task<IngestResponse>[] tasks = Enumerable.Range(0, 10)
            .Select(i => IngestAsync(i % 2 == 0 ? AgentA : AgentB, i % 2 == 0 ? AgentASecret : AgentBSecret,
                NewRequest("load-" + i, Invoice(4300 + i, userCode: 5 + i, cashier: "صندوق " + (i + 1)))))
            .ToArray();
        IngestResponse[] results = await Task.WhenAll(tasks);
        Check("همهٔ ۱۰ درخواست همزمان پذیرفته شد", results.Sum(x => x.Accepted) == 10,
            string.Join(" | ", results.Select(Dump)));
        Check("شمارنده دقیقاً ۱۰ شد", (await CurrentRoundAsync()).Count == 10, "");

        Task<HttpResponseMessage>[] health = Enumerable.Range(0, 5).Select(_ => _http.GetAsync("/health")).ToArray();
        HttpResponseMessage[] healths = await Task.WhenAll(health);
        Check("پرس‌وجوهای خواندنی همزمان 200", healths.All(x => x.StatusCode == HttpStatusCode.OK), "");
    }

    // ------------------------------------------------------------------
    //  Phase 3 — API عمومی، حریم خصوصی و مجوزها
    // ------------------------------------------------------------------
    private static async Task PublicCampaignAsync()
    {
        RoundInfo round = await StartFreshRoundAsync();
        await IngestAsync(AgentA, AgentASecret, NewRequest("pub-1", Invoice(5101)));
        Check("شمارندهٔ سرور = 1", (await CurrentRoundAsync()).Count == 1, "");

        HttpResponseMessage r = await PublicGetAsync("/api/v1/public/campaign/current");
        PublicCampaignStatus s = await ReadPublicAsync<PublicCampaignStatus>(r);
        Check("وضعیت عمومی 200", r.StatusCode == HttpStatusCode.OK, await r.Content.ReadAsStringAsync());
        Check("وضعیت Collecting و شمارهٔ دوره", s.State == "Collecting" && s.RoundNo == round.RoundNo,
            s.State + "/" + s.RoundNo);
        Check("شمارندهٔ واقعی و باقی‌مانده", s.Count == 1 && s.Target == 1000 && s.Remaining == 999,
            $"{s.Count}/{s.Target}/{s.Remaining}");
        Check("درصد پیشرفت", Math.Abs(s.ProgressPercent - 0.1) < 0.001, s.ProgressPercent.ToString(CultureInfo.InvariantCulture));
        Check("جایزهٔ رسمی دوره", s.PrizeToman == 10_000_000, s.PrizeToman.ToString());
        Check("پذیرش فاکتور جدید فقط در دورهٔ باز", s.AcceptingNewInvoices && !s.AwaitingManagerStart, "");
        Check("نتیجهٔ عمومی هنوز ندارد", s.LastResult == null, "");
        Check("کش عمومی اعلام می‌شود",
            r.Headers.CacheControl?.MaxAge == TimeSpan.FromSeconds(5), r.Headers.CacheControl?.ToString() ?? "-");

        await IngestAsync(AgentA, AgentASecret, NewRequest("pub-2", Invoice(5102)));
        PublicCampaignStatus after = await ReadPublicAsync<PublicCampaignStatus>(
            await PublicGetAsync("/api/v1/public/campaign/current"));
        Check("کش پس از تغییر داده تازه شد", after.Count == 2, after.Count.ToString());
    }

    private static async Task PublicCompletedRoundAsync()
    {
        RoundInfo round = await StartFreshRoundAsync();
        await SeedValidAsync(round.RoundId, 1000, complete: true);

        PublicCampaignStatus s = await ReadPublicAsync<PublicCampaignStatus>(
            await PublicGetAsync("/api/v1/public/campaign/current"));
        Check("دورهٔ تکمیل‌شده وضعیت DrawPending", s.State == "DrawPending", s.State + "/" + s.RoundStatus);
        Check("شمارندهٔ دورهٔ تکمیل‌شده صفر نشد", s.Count == 1000 && s.Remaining == 0 && s.ProgressPercent == 100,
            $"{s.Count}/{s.Remaining}");
        Check("دورهٔ بعد منتظر شروع مدیر است", s.AwaitingManagerStart && !s.AcceptingNewInvoices, "");

        PublicWinnersPage winners = await ReadPublicAsync<PublicWinnersPage>(
            await PublicGetAsync("/api/v1/public/winners"));
        Check("برندهٔ منتشرنشده در فهرست عمومی نیست", winners.Total == 0 && winners.Winners.Count == 0,
            winners.Total.ToString());
    }

    private static async Task PublishRequiresApprovalAsync()
    {
        RoundInfo round = await StartFreshRoundAsync();
        await SeedValidAsync(round.RoundId, 1000, complete: true);
        _baleReady = true;
        BaleMembers.Add(SeedMobile);
        int runId = await FinalizeRunAsync(round.RoundId);

        HttpResponseMessage noConfirm = await AdminPostAsync($"/api/v1/admin/lottery/{runId}/publish",
            new { actor = "owner", displayName = "خانم م." });
        Check("انتشار بدون تأیید صریح = 400", noConfirm.StatusCode == HttpStatusCode.BadRequest,
            await noConfirm.Content.ReadAsStringAsync());

        HttpResponseMessage badImage = await AdminPostAsync($"/api/v1/admin/lottery/{runId}/image",
            new { actor = "owner", imageUrl = "https://ebimarket.ir/uploads/winner.php", approved = true });
        Check("پسوند غیرتصویری رد شد", badImage.StatusCode == HttpStatusCode.BadRequest,
            await badImage.Content.ReadAsStringAsync());
        HttpResponseMessage noConsent = await AdminPostAsync($"/api/v1/admin/lottery/{runId}/image",
            new { actor = "owner", imageUrl = "https://ebimarket.ir/uploads/winner.jpg", approved = false });
        Check("تصویر بدون تأیید رضایت رد شد", noConsent.StatusCode == HttpStatusCode.BadRequest,
            await noConsent.Content.ReadAsStringAsync());

        LotteryRunInfo published = await PublishAsync(runId, "خانم م.",
            imageUrl: "https://ebimarket.ir/wp-content/uploads/2026/02/winner-1.jpg", imageApproved: true);
        Check("انتشار با تأیید صریح 200", published.PublishApproved && published.PublishApprovedAt.HasValue, "");
        Check("تصویر تأییدشده ثبت شد",
            published.WinnerImageApproved && published.WinnerImageUrl!.EndsWith(".jpg"),
            published.WinnerImageUrl ?? "-");

        PublicCampaignStatus s = await ReadPublicAsync<PublicCampaignStatus>(
            await PublicGetAsync("/api/v1/public/campaign/current"));
        Check("وضعیت عمومی ResultPublished", s.State == "ResultPublished", s.State);
        Check("آخرین نتیجهٔ منتشرشده نمایش داده می‌شود",
            s.LastResult != null && s.LastResult.RoundNo == round.RoundNo, s.LastResult?.RoundNo.ToString() ?? "-");
        Check("شمارهٔ برنده در نتیجهٔ عمومی سانسورشده است",
            s.LastResult!.MobileMasked.Contains("***", StringComparison.Ordinal)
            && s.LastResult.ImageUrl != null,
            s.LastResult.MobileMasked);
        Check("وضعیت راستی‌آزمایی قابل بازتولید", s.LastResult.VerificationStatus == "recomputable",
            s.LastResult.VerificationStatus);
        Check("گزارش انتشار در حسابرسی ثبت شد", await CountAuditAsync("lottery.publish") == 1, "");

        HttpResponseMessage noReason = await AdminPostAsync($"/api/v1/admin/lottery/{runId}/unpublish",
            new { actor = "owner" });
        Check("لغو انتشار بدون دلیل = 400", noReason.StatusCode == HttpStatusCode.BadRequest, "");
        LotteryRunInfo unpublished = await ReadJsonAsync<LotteryRunInfo>(
            await AdminPostAsync($"/api/v1/admin/lottery/{runId}/unpublish",
                new { actor = "owner", reason = "بازبینی نتیجه" }));
        Check("لغو انتشار 200", !unpublished.PublishApproved, "");
        Check("حسابرسی لغو انتشار ثبت شد", await CountAuditAsync("lottery.unpublish") == 1, "");
        PublicCampaignStatus back = await ReadPublicAsync<PublicCampaignStatus>(
            await PublicGetAsync("/api/v1/public/campaign/current"));
        Check("وضعیت عمومی به DrawPending برگشت", back.State == "DrawPending" && back.LastResult == null, back.State);
    }

    private static async Task PublicWinnersAsync()
    {
        RoundInfo round = await StartFreshRoundAsync();
        await SeedValidAsync(round.RoundId, 1000, complete: true);
        _baleReady = true;
        BaleMembers.Add(SeedMobile);
        int runId = await FinalizeRunAsync(round.RoundId);
        await PublishAsync(runId, "خانم م.");

        PublicWinnersPage page = await ReadPublicAsync<PublicWinnersPage>(
            await PublicGetAsync("/api/v1/public/winners?page=1&perPage=10"));
        Check("برندهٔ منتشرشده در فهرست", page.Total == 1 && page.Winners.Count == 1 && !page.HasMore,
            $"{page.Total}/{page.Winners.Count}");
        PublicWinnerCard card = page.Winners[0];
        Check("شمارهٔ سانسورشده", card.MobileMasked == "0912***0000", card.MobileMasked);
        Check("نام نمایشی تأییدشده", card.DisplayName == "خانم م.", card.DisplayName ?? "-");
        Check("تصویر تأییدنشده منتشر نمی‌شود", card.ImageUrl == null, card.ImageUrl ?? "-");
        Check("جایزه و تاریخ قرعه‌کشی", card.PrizeToman == 10_000_000 && card.DrawnAt.HasValue, "");
        Check("ورودی و وزن در کارت", card.EntryCount == 1000 && card.EligibleCount == 1000 && card.TotalWeight == 1000,
            $"{card.EntryCount}/{card.EligibleCount}/{card.TotalWeight}");

        PublicWinnerCard byRound = await ReadPublicAsync<PublicWinnerCard>(
            await PublicGetAsync($"/api/v1/public/winners/{round.RoundNo}"));
        Check("مسیر برندهٔ یک دوره", byRound.RoundNo == round.RoundNo, byRound.RoundNo.ToString());
        HttpResponseMessage missing = await PublicGetAsync("/api/v1/public/winners/9999");
        Check("دورهٔ بدون برندهٔ منتشرشده = 404", missing.StatusCode == HttpStatusCode.NotFound, "");
        HttpResponseMessage badPerPage = await PublicGetAsync("/api/v1/public/winners?perPage=500");
        Check("perPage بیش از حد = 400", badPerPage.StatusCode == HttpStatusCode.BadRequest, "");
        HttpResponseMessage badPage = await PublicGetAsync("/api/v1/public/winners?page=0");
        Check("page نامعتبر = 400", badPage.StatusCode == HttpStatusCode.BadRequest, "");
        PublicWinnersPage filtered = await ReadPublicAsync<PublicWinnersPage>(
            await PublicGetAsync("/api/v1/public/winners?roundNo=42"));
        Check("فیلتر دورهٔ ناموجود فهرست را خالی می‌کند", filtered.Total == 0, filtered.Total.ToString());
    }

    private static async Task PublicAuditKitAsync()
    {
        RoundInfo round = await StartFreshRoundAsync();
        await SeedValidAsync(round.RoundId, 1000, complete: true);
        _baleReady = true;
        BaleMembers.Add(SeedMobile);
        int runId = await FinalizeRunAsync(round.RoundId);

        HttpResponseMessage before = await PublicGetAsync($"/api/v1/public/lottery/{runId}/receipt");
        Check("سند پیش از انتشار عمومی = 404", before.StatusCode == HttpStatusCode.NotFound, "");
        await PublishAsync(runId, "خانم م.");

        PublicLotteryReceipt receipt = await ReadPublicAsync<PublicLotteryReceipt>(
            await PublicGetAsync($"/api/v1/public/lottery/{runId}/receipt"));
        Check("سند عمومی 200", receipt.RunId == runId && receipt.Status == "Finalized", receipt.Status);
        Check("Seed افشا شده و هش عمومی موجود",
            receipt.SeedRevealed && receipt.SeedHex is { Length: 64 } && receipt.PublicEntriesHash.Length == 64, "");
        Check("راستی‌آزمایی سند سبز است", receipt.Verified, receipt.VerificationNote);
        Check("شناسهٔ مستعار برنده در سند", receipt.WinnerPseudonym is { Length: 12 },
            receipt.WinnerPseudonym ?? "-");

        PublicEntriesPage p1 = await ReadPublicAsync<PublicEntriesPage>(
            await PublicGetAsync($"/api/v1/public/lottery/{runId}/entries?page=1&perPage=500"));
        PublicEntriesPage p2 = await ReadPublicAsync<PublicEntriesPage>(
            await PublicGetAsync($"/api/v1/public/lottery/{runId}/entries?page=2&perPage=500"));
        List<PublicEntry> all = p1.Entries.Concat(p2.Entries).OrderBy(e => e.EntryNo).ToList();
        Check("کل ۱۰۰۰ ورودی در دو صفحه منتشر شد", all.Count == 1000 && p1.Total == 1000 && !p2.HasMore,
            $"{all.Count}/{p1.Total}");
        Check("هر ورودی شناسهٔ مستعار دارد", all.All(e => e.Pseudonym.Length == 12 && e.EntryNo >= 1), "");

        string recomputedHash = LotteryEngine.PublicEntriesHash(all.Select(e =>
            LotteryEngine.PublicEntryLine(e.EntryNo, e.Pseudonym, e.Eligibility, e.Weight)));
        Check("هش عمومی از فهرست منتشرشده بازتولید شد",
            recomputedHash == p1.PublicEntriesHash, recomputedHash);

        string recomputedCommitment = Security.Sha256Hex(
            $"{receipt.Algorithm}|round={receipt.RoundNo}|entries={receipt.PublicEntriesHash}|seed={receipt.SeedHex}");
        Check("تعهد از سند عمومی بازتولید شد", recomputedCommitment == receipt.CommitmentHash, recomputedCommitment);

        List<PublicEntry> eligible = all.Where(e => e.Eligibility == "eligible" && e.Weight > 0).ToList();
        byte[] seed = Convert.FromHexString(receipt.SeedHex!);
        int idx = LotteryEngine.PickWeighted(seed,
            LotteryEngine.Context(receipt.RoundNo, p1.PublicEntriesHash),
            eligible.Select(e => e.Weight).ToList(), out long draw, out long total);
        Check("انتخاب وزنی از دادهٔ عمومی بازتولید شد",
            eligible[idx].EntryNo == receipt.WinnerEntryNo && draw == receipt.DrawValue && total == receipt.TotalWeight,
            $"entry={eligible[idx].EntryNo} draw={draw} total={total}");

        PublicVerifyResult verify = await ReadPublicAsync<PublicVerifyResult>(
            await PublicGetAsync($"/api/v1/public/lottery/{runId}/verify"));
        Check("مسیر تأیید عمومی همهٔ بررسی‌ها را سبز می‌دهد",
            verify.PublicEntriesHashMatches && verify.CommitmentMatches && verify.WinnerReproduces, verify.Note);
    }

    private static async Task NoPublicLeakAsync()
    {
        RoundInfo round = await StartFreshRoundAsync();
        await SeedValidAsync(round.RoundId, 1000, complete: true);
        _baleReady = true;
        BaleMembers.Add(SeedMobile);
        int runId = await FinalizeRunAsync(round.RoundId);
        await PublishAsync(runId, "خانم م.", imageUrl: "https://ebimarket.ir/uploads/w1.jpg", imageApproved: true);
        LotteryRunInfo admin = await GetRunAsync(runId, entries: false);

        string[] paths =
        {
            "/api/v1/public/campaign/current",
            "/api/v1/public/winners?perPage=20",
            $"/api/v1/public/winners/{round.RoundNo}",
            $"/api/v1/public/lottery/{runId}/receipt",
            $"/api/v1/public/lottery/{runId}/verify",
            $"/api/v1/public/lottery/{runId}/entries?perPage=500&page=1"
        };
        StringBuilder bodies = new();
        foreach (string path in paths)
            bodies.Append(await (await PublicGetAsync(path)).Content.ReadAsStringAsync()).Append('\n');
        string text = bodies.ToString();

        Check("شمارهٔ کامل برنده افشا نمی‌شود", !text.Contains(SeedMobile, StringComparison.Ordinal), "");
        Check("شناسهٔ فاکتور و مشتری افشا نمی‌شود",
            !text.Contains("\"invoiceId\"", StringComparison.Ordinal)
            && !text.Contains("\"fCode\"", StringComparison.Ordinal)
            && !text.Contains("\"customerCode\"", StringComparison.Ordinal), "");
        Check("شواهد بررسی و وضعیت بله افشا نمی‌شود",
            !text.Contains("\"reviewReason\"", StringComparison.Ordinal)
            && !text.Contains("\"reviewEvidence\"", StringComparison.Ordinal)
            && !text.Contains("\"baleStatus\"", StringComparison.Ordinal)
            && !text.Contains("\"baleEvidence\"", StringComparison.Ordinal), "");
        Check("هش داخلی مجموعهٔ محرمانه افشا نمی‌شود",
            !text.Contains("\"entriesHash\"", StringComparison.Ordinal)
            && !text.Contains(admin.EntriesHash, StringComparison.Ordinal), "");
        Check("جزئیات داخلی حسابرسی افشا نمی‌شود",
            !text.Contains("\"preparedBy\"", StringComparison.Ordinal)
            && !text.Contains("\"seedHex\":null", StringComparison.Ordinal), "");
        Check("کنترل مثبت: برنده با شمارهٔ سانسورشده نمایش داده می‌شود",
            text.Contains("0912***0000", StringComparison.Ordinal), "");
    }

    private static async Task CapabilitySeparationAsync()
    {
        RoundInfo round = await StartFreshRoundAsync();
        await SeedValidAsync(round.RoundId, 1000, complete: true);
        _baleReady = true;
        BaleMembers.Add(SeedMobile);

        HttpResponseMessage startDenied = await AdminPostWithKeyAsync("/api/v1/admin/rounds/start", RoleLottery,
            new { actor = "x" });
        Check("نقش قرعه‌کشی نمی‌تواند دورهٔ جدید شروع کند", startDenied.StatusCode == HttpStatusCode.Unauthorized,
            await startDenied.Content.ReadAsStringAsync());

        HttpResponseMessage prep = await AdminPostWithKeyAsync($"/api/v1/admin/rounds/{round.RoundId}/lottery",
            RoleLottery, new { actor = "lottery-runner" });
        Check("نقش lottery.run می‌تواند مجموعه را تثبیت کند", prep.StatusCode == HttpStatusCode.OK,
            await prep.Content.ReadAsStringAsync());
        LotteryRunInfo run = await ReadJsonAsync<LotteryRunInfo>(prep);

        HttpResponseMessage publishDenied = await AdminPostWithKeyAsync($"/api/v1/admin/lottery/{run.RunId}/publish",
            RoleLottery, new { actor = "x", confirm = true });
        Check("نقش lottery.run اجازهٔ انتشار عمومی ندارد",
            publishDenied.StatusCode == HttpStatusCode.Unauthorized, "");
        HttpResponseMessage contactDenied = await AdminGetWithKeyAsync(
            $"/api/v1/admin/lottery/{run.RunId}/winner-contact", RoleLottery);
        Check("نقش lottery.run اجازهٔ شمارهٔ کامل برنده ندارد",
            contactDenied.StatusCode == HttpStatusCode.Unauthorized, "");

        HttpResponseMessage select = await AdminPostWithKeyAsync($"/api/v1/admin/lottery/{run.RunId}/select",
            RoleLottery, new { actor = "lottery-runner" });
        Check("نقش lottery.run انتخاب برنده را می‌تواند", select.StatusCode == HttpStatusCode.OK,
            await select.Content.ReadAsStringAsync());
        HttpResponseMessage fin = await AdminPostWithKeyAsync($"/api/v1/admin/lottery/{run.RunId}/finalize",
            RoleLottery, new { actor = "lottery-runner" });
        Check("نقش lottery.run نهایی‌سازی را می‌تواند", fin.StatusCode == HttpStatusCode.OK, "");

        HttpResponseMessage publishWithRole = await AdminPostWithKeyAsync(
            $"/api/v1/admin/lottery/{run.RunId}/publish", RolePublish, new { actor = "publisher", confirm = true });
        Check("نقش lottery.publish انتشار را می‌تواند", publishWithRole.StatusCode == HttpStatusCode.OK,
            await publishWithRole.Content.ReadAsStringAsync());
        HttpResponseMessage selectWithPublishRole = await AdminPostWithKeyAsync(
            $"/api/v1/admin/lottery/{run.RunId}/select", RolePublish, new { actor = "publisher" });
        Check("نقش lottery.publish اجازهٔ اجرای قرعه‌کشی ندارد",
            selectWithPublishRole.StatusCode == HttpStatusCode.Unauthorized, "");

        HttpResponseMessage contact = await AdminGetWithKeyAsync(
            $"/api/v1/admin/lottery/{run.RunId}/winner-contact", RolePrivacy);
        string contactBody = await contact.Content.ReadAsStringAsync();
        Check("نقش privacy.contact شمارهٔ کامل را می‌گیرد",
            contact.StatusCode == HttpStatusCode.OK && contactBody.Contains(SeedMobile, StringComparison.Ordinal), "");
        HttpResponseMessage publishWithPrivacyRole = await AdminPostWithKeyAsync(
            $"/api/v1/admin/lottery/{run.RunId}/publish", RolePrivacy, new { actor = "privacy", confirm = true });
        Check("نقش privacy.contact اجازهٔ انتشار ندارد",
            publishWithPrivacyRole.StatusCode == HttpStatusCode.Unauthorized, "");

        HttpResponseMessage decisionDenied = await AdminPostWithKeyAsync(
            $"/api/v1/admin/invoices/1/decision", RoleLottery, new { verdict = "valid" });
        Check("نقش قرعه‌کشی اجازهٔ تصمیم روی فاکتور ندارد",
            decisionDenied.StatusCode == HttpStatusCode.Unauthorized, "");
        HttpResponseMessage decisionAllowed = await AdminPostWithKeyAsync(
            $"/api/v1/admin/invoices/1/decision", RoleReview, new { verdict = "valid", actor = "reviewer" });
        Check("نقش review.decide به مسیر تصمیم دسترسی دارد",
            decisionAllowed.StatusCode != HttpStatusCode.Unauthorized, await decisionAllowed.Content.ReadAsStringAsync());
    }

    private static async Task RateLimiterAsync()
    {
        PublicRateLimiter limiter = new(3, TimeSpan.FromMilliseconds(300));
        bool a = limiter.TryAcquire("client-1", out int rem1, out _);
        bool b = limiter.TryAcquire("client-1", out int rem2, out _);
        bool c = limiter.TryAcquire("client-1", out int rem3, out _);
        Check("سه درخواست اول مجاز", a && b && c, "");
        Check("سرآمد مصرف‌شده کاهش می‌یابد", rem1 == 2 && rem2 == 1 && rem3 == 0, $"{rem1}/{rem2}/{rem3}");
        Check("درخواست چهارم در همان پنجره رد می‌شود", !limiter.TryAcquire("client-1", out _, out int retry),
            "");
        Check("زمان انتظار اعلام می‌شود", retry >= 1, retry.ToString());
        Check("کليد کلاینت دیگر مستقل است", limiter.TryAcquire("client-2", out _, out _), "");

        await Task.Delay(400);
        Check("پنجرهٔ ثابت پس از گذشت زمان بازنشانی می‌شود", limiter.TryAcquire("client-1", out _, out _), "");

        HttpResponseMessage live = await PublicGetAsync("/api/v1/public/campaign/current");
        Check("سرآمد محدودیت نرخ در پاسخ عمومی هست",
            live.Headers.Contains("X-RateLimit-Limit") && live.Headers.Contains("X-RateLimit-Remaining"), "");
    }

    // ------------------------------------------------------------------
    //  ۴.۳ — آزمون بار (نگهداشت دقیق شمارنده زیر فشار همزمانی)
    // ------------------------------------------------------------------
    private static async Task RunLoadAsync(int total, int concurrency)
    {
        Console.WriteLine($"=== آزمون بار: {total} ارسال همزمان با پنجرهٔ {concurrency} ===");
        await StartFreshRoundAsync();

        using SemaphoreSlim gate = new(concurrency);
        List<double> latencies = new();
        object sync = new();
        int accepted = 0;
        int failed = 0;

        Stopwatch wall = Stopwatch.StartNew();
        Task[] senders = Enumerable.Range(0, total).Select(async i =>
        {
            await gate.WaitAsync();
            try
            {
                Stopwatch one = Stopwatch.StartNew();
                try
                {
                    IngestResponse res = await IngestAsync(AgentA, AgentASecret,
                        NewRequest($"load-{i}", Invoice(900000 + i, userCode: 9, cashier: "بار " + (i % 4 + 1))));
                    Interlocked.Add(ref accepted, res.Accepted);
                }
                catch
                {
                    Interlocked.Increment(ref failed);
                }
                one.Stop();
                lock (sync) latencies.Add(one.Elapsed.TotalMilliseconds);
            }
            finally
            {
                gate.Release();
            }
        }).ToArray();
        await Task.WhenAll(senders);
        wall.Stop();

        latencies.Sort();
        double p50 = latencies[latencies.Count / 2];
        double p95 = latencies[(int)(latencies.Count * 0.95)];
        double p99 = latencies[(int)(latencies.Count * 0.99)];
        double rate = total / wall.Elapsed.TotalSeconds;

        Check("هیچ ارسالی شکست نخورد", failed == 0, failed.ToString());
        Check("همهٔ فاکتورها پذیرفته شد", accepted == total, accepted.ToString());
        Check("شمارندهٔ دوره دقیق است", (await CurrentRoundAsync()).Count == total, "");
        Check("تعداد رکوردهای دیتابیس دقیق است", await CountInvoicesAsync() == total, "");
        Console.WriteLine(Inv($"اندازه‌گیری نوشتن: {total} ارسال در {wall.Elapsed.TotalSeconds:F2}s = {rate:F0} درخواست/ثانیه | p50={p50:F1}ms p95={p95:F1}ms p99={p99:F1}ms"));

        // خواندن متعادل: پرس‌وجوی وضعیت دوره با همزمانی معمول داشبورد/پنل
        (int okReads, double readSeconds) = await ProbeAsync("/health", 200, 8);
        Check("۲۰۰ پرس‌وجوی وضعیت با همزمانی ۸ همه 200", okReads == 200, okReads.ToString());
        Console.WriteLine(Inv($"اندازه‌گیری خواندن: 200 درخواست /health با همزمانی ۸ در {readSeconds:F2}s = {200 / readSeconds:F0} درخواست/ثانیه"));

        // کاوش اشباع: کاوشگر سبک بدون دیتابیس باید زیر فشار بماند
        (int okLive, double liveSeconds) = await ProbeAsync("/livez", 400, 200);
        Check("کاوشگر سبک زیر ۴۰۰ درخواست همزمان پایدار است", okLive == 400, okLive.ToString());
        Console.WriteLine(Inv($"اندازه‌گیری کاوشگر: 400 درخواست /livez با همزمانی ۲۰۰ در {liveSeconds:F2}s = {400 / liveSeconds:F0} درخواست/ثانیه"));

        // اندازه‌گیری اطلاعی از فشار نامتعارف روی مسیر وابسته به دیتابیس (سقف مخزن اتصال)
        (int okHealth, double healthSeconds) = await ProbeAsync("/health", 400, 200);
        Console.WriteLine(Inv($"کاوش اشباع: 400 درخواست /health با همزمانی ۲۰۰ → {okHealth} پاسخ 200، {400 - okHealth} پاسخ ناموفق در {healthSeconds:F2}s (سقف مخزن اتصال SQL)"));
    }

    private static string Inv(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);

    private static async Task<(int ok, double seconds)> ProbeAsync(string path, int total, int concurrency)
    {
        using SemaphoreSlim gate = new(concurrency);
        int ok = 0;
        Stopwatch sw = Stopwatch.StartNew();
        await Task.WhenAll(Enumerable.Range(0, total).Select(async _ =>
        {
            await gate.WaitAsync();
            try
            {
                HttpResponseMessage r = await _http.GetAsync(path);
                if (r.StatusCode == HttpStatusCode.OK) Interlocked.Increment(ref ok);
            }
            catch { }
            finally { gate.Release(); }
        }));
        sw.Stop();
        return (ok, sw.Elapsed.TotalSeconds);
    }

    private static async Task<int> CountInvoicesAsync()
    {
        await using SqlConnection cn = new(TestCs);
        await cn.OpenAsync();
        using SqlCommand cmd = new("SELECT COUNT(*) FROM dbo.Invoices", cn);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    // ------------------------------------------------------------------
    //  helpers
    // ------------------------------------------------------------------
    /// <summary>هر سه مرحلهٔ قرعه‌کشی: تثبیت، انتخاب و نهایی‌سازی.</summary>
    private static async Task<int> FinalizeRunAsync(int roundId)
    {
        LotteryRunInfo run = await PrepareLotteryAsync(roundId);
        HttpResponseMessage selected = await AdminPostAsync($"/api/v1/admin/lottery/{run.RunId}/select",
            new { actor = "owner" });
        if (selected.StatusCode != HttpStatusCode.OK)
            throw new InvalidOperationException(await selected.Content.ReadAsStringAsync());
        HttpResponseMessage fin = await AdminPostAsync($"/api/v1/admin/lottery/{run.RunId}/finalize",
            new { actor = "owner" });
        if (fin.StatusCode != HttpStatusCode.OK)
            throw new InvalidOperationException(await fin.Content.ReadAsStringAsync());
        return run.RunId;
    }

    private static async Task<LotteryRunInfo> PublishAsync(int runId, string displayName, string? imageUrl = null,
        bool imageApproved = false)
    {
        HttpResponseMessage r = await AdminPostAsync($"/api/v1/admin/lottery/{runId}/publish", new
        {
            actor = "owner", confirm = true, displayName, imageUrl, imageApproved
        });
        string body = await r.Content.ReadAsStringAsync();
        if (r.StatusCode != HttpStatusCode.OK) throw new InvalidOperationException(body);
        return JsonSerializer.Deserialize<LotteryRunInfo>(body, CampaignService.Json)!;
    }

    /// <summary>درخواست عمومی بدون هیچ هدر احراز هویت.</summary>
    private static Task<HttpResponseMessage> PublicGetAsync(string path) => _http.GetAsync(path);

    private static async Task<T> ReadPublicAsync<T>(HttpResponseMessage r)
    {
        string body = await r.Content.ReadAsStringAsync();
        if (r.StatusCode != HttpStatusCode.OK) throw new InvalidOperationException($"{(int)r.StatusCode}: {body}");
        return JsonSerializer.Deserialize<T>(body, CampaignService.Json)
            ?? throw new InvalidOperationException("پاسخ عمومی خالی: " + body);
    }

    private static async Task<HttpResponseMessage> AdminPostWithKeyAsync(string path, string key, object payload)
    {
        using HttpRequestMessage msg = new(HttpMethod.Post, path);
        msg.Headers.Add("X-Admin-Key", key);
        msg.Content = JsonContent(payload);
        return await _http.SendAsync(msg);
    }

    private static async Task<HttpResponseMessage> AdminGetWithKeyAsync(string path, string key)
    {
        using HttpRequestMessage msg = new(HttpMethod.Get, path);
        msg.Headers.Add("X-Admin-Key", key);
        return await _http.SendAsync(msg);
    }

    private static async Task<InvoiceDetail> InvoiceDetailAsync(long invoiceId)
    {
        HttpResponseMessage r = await AdminGetAsync($"/api/v1/admin/invoices/{invoiceId}/detail");
        string body = await r.Content.ReadAsStringAsync();
        if (r.StatusCode != HttpStatusCode.OK) throw new InvalidOperationException(body);
        return JsonSerializer.Deserialize<InvoiceDetail>(body, CampaignService.Json)!;
    }

    private static async Task<LotteryRunInfo> PrepareLotteryAsync(int roundId, bool acknowledge = false)
    {
        HttpResponseMessage r = await AdminPostAsync($"/api/v1/admin/rounds/{roundId}/lottery",
            new { actor = "owner", acknowledgeReviews = acknowledge });
        string body = await r.Content.ReadAsStringAsync();
        if (r.StatusCode != HttpStatusCode.OK) throw new InvalidOperationException(body);
        return JsonSerializer.Deserialize<LotteryRunInfo>(body, CampaignService.Json)!;
    }

    private static async Task<LotteryRunInfo> GetRunAsync(int runId, bool entries = true)
    {
        HttpResponseMessage r = await AdminGetAsync($"/api/v1/admin/lottery/{runId}?entries={(entries ? "true" : "false")}");
        string body = await r.Content.ReadAsStringAsync();
        if (r.StatusCode != HttpStatusCode.OK) throw new InvalidOperationException(body);
        return JsonSerializer.Deserialize<LotteryRunInfo>(body, CampaignService.Json)!;
    }

    private static async Task<int> CountAuditAsync(string action)
    {
        await using SqlConnection cn = new(TestCs);
        await cn.OpenAsync();
        using SqlCommand cmd = new("SELECT COUNT(*) FROM dbo.AuditLog WHERE Action=@a", cn);
        cmd.Parameters.AddWithValue("@a", action);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    private static async Task ExecuteSqlAsync(string sql, params (string name, object value)[] args)
    {
        await using SqlConnection cn = new(TestCs);
        await cn.OpenAsync();
        using SqlCommand cmd = new(sql, cn);
        foreach ((string name, object value) in args) cmd.Parameters.AddWithValue(name, value);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>درج فاکتورهای ساختگی با امتیاز/موبایل دلخواه برای آزمون قرعه‌کشی.</summary>
    private static async Task SeedShapeAsync(int roundId, int startIndex, int count,
        Func<int, (int points, string? mobile)> shape)
    {
        if (count <= 0) return;
        await using SqlConnection cn = new(TestCs);
        await cn.OpenAsync();
        using SqlTransaction tx = (SqlTransaction)await cn.BeginTransactionAsync();
        const int batch = 100;
        for (int start = 0; start < count; start += batch)
        {
            int n = Math.Min(batch, count - start);
            StringBuilder sql = new(@"INSERT INTO dbo.Invoices(SourceId, FCode, FCodeC, FType, FDate, Jalali, FTime,
    UserCode, Cashier, Gross, Discount, Net, CustomerCode, Mobile, Points, Status, RoundId, AgentId, ReceivedAt) VALUES");
            using SqlCommand cmd = new(sql.ToString(), cn, tx);
            cmd.Parameters.AddWithValue("@rid", roundId);
            for (int i = 0; i < n; i++)
            {
                int idx = startIndex + start + i;
                (int points, string? mobile) = shape(idx);
                sql.Append(i == 0 ? "" : ",");
                sql.Append($"('Holoo1', {700000 + idx}, {idx}, 2, '2026-02-01', '1404/11/12', '12:00', 9, N'صندوق آزمون', " +
                           $"5000000, 0, 5000000, '00001', {(mobile == null ? "NULL" : "@m" + i)}, {points}, 'Valid', @rid, 'seed', SYSUTCDATETIME())");
                if (mobile != null) cmd.Parameters.AddWithValue("@m" + i, mobile);
            }
            cmd.CommandText = sql.ToString();
            await cmd.ExecuteNonQueryAsync();
        }
        await tx.CommitAsync();
    }

    private static Task MarkRoundCompleteAsync(int roundId, int count)
        => ExecuteSqlAsync(@"UPDATE dbo.Rounds SET [Count]=@n, Status='Completed', CompletedAt=SYSUTCDATETIME()
WHERE RoundId=@rid;", ("@n", count), ("@rid", roundId));

    private static string DumpDecision(DecisionResult d)
        => $"verdict={d.Verdict} status={d.Status} round={d.RoundId} count={d.RoundCount} changed={d.CountChanged} "
           + $"already={d.AlreadyDecided} forced={d.Forced} finalized={d.FinalizedRound}";

    private static string DumpRun(LotteryRunInfo r)
        => $"run={r.RunId} status={r.Status} entries={r.EntryCount} eligible={r.EligibleCount} weight={r.TotalWeight} "
           + $"winner={r.WinnerFCode}/{r.WinnerEntryNo} draw={r.WinnerDrawValue} total={r.WinnerTotalWeight} "
           + $"seed={(r.SeedHex == null ? "hidden" : "revealed")}";

    private static async Task RunAsync(string name, Func<Task> test)
    {
        Console.WriteLine();
        Console.WriteLine("--- " + name + " ---");
        try
        {
            await ResetDataAsync();
            _baleReady = false;
            BaleMembers.Clear();
            FakeBale.Results.Clear();
            await test();
        }
        catch (Exception ex)
        {
            _fail++;
            Console.WriteLine("[FAIL] استثنا: " + ex.Message);
        }
    }

    private static void Check(string name, bool ok, string detail)
    {
        if (ok) { _ok++; Console.WriteLine("[OK]   " + name); }
        else { _fail++; Console.WriteLine("[FAIL] " + name + " — " + detail); }
    }

    private static IngestRequest NewRequest(string requestId, params IngestInvoice[] invoices)
        => new() { RequestId = requestId, SourceId = "Holoo1", Invoices = invoices.ToList() };

    private static IngestInvoice Invoice(long fCode, int userCode = 9, string cashier = "صندوق ۱", string mobile = "09120000000")
        => new()
        {
            FCode = fCode,
            FCodeC = fCode % 100000,
            FType = 2,
            FDate = new DateOnly(2026, 2, 1),
            Jalali = "1404/11/12",
            FTime = "12:00",
            UserCode = userCode,
            Cashier = cashier,
            Gross = 5_000_000m,
            Discount = 0,
            Net = 5_000_000m,
            CustomerCode = "00001",
            Mobile = mobile,
            Status = "valid"
        };

    private static async Task<IngestResponse> IngestAsync(string agent, string secret, IngestRequest req)
    {
        HttpResponseMessage r = await PostIngestRawAsync(agent, secret, req.RequestId, req);
        string body = await r.Content.ReadAsStringAsync();
        if (r.StatusCode != HttpStatusCode.OK)
            throw new InvalidOperationException($"ingest failed {(int)r.StatusCode}: {body}");
        return JsonSerializer.Deserialize<IngestResponse>(body, CampaignService.Json)!;
    }

    private static async Task<HttpResponseMessage> PostIngestRawAsync(string agent, string secret, string requestId, IngestRequest req)
    {
        string body = JsonSerializer.Serialize(req, CampaignService.Json);
        return await PostRawSignedAsync(agent, secret, "/api/v1/ingest/invoices", body);
    }

    private static async Task<HttpResponseMessage> PostRawSignedAsync(string agent, string secret, string path, string body)
    {
        long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        string hash = Security.Sha256Hex(Encoding.UTF8.GetBytes(body));
        string canonical = Security.Canonical(agent, ts.ToString(), "POST", path, hash);
        using HttpRequestMessage msg = new(HttpMethod.Post, path);
        msg.Headers.Add("X-Agent-Id", agent);
        msg.Headers.Add("X-Timestamp", ts.ToString());
        msg.Headers.Add("X-Signature", Security.HmacHex(secret, canonical));
        msg.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return await _http.SendAsync(msg);
    }

    private static async Task<HttpResponseMessage> AdminPostAsync(string path, object payload)
    {
        using HttpRequestMessage msg = new(HttpMethod.Post, path);
        msg.Headers.Add("X-Admin-Key", AdminKey);
        msg.Content = JsonContent(payload);
        return await _http.SendAsync(msg);
    }

    private static async Task<HttpResponseMessage> AdminGetAsync(string path)
    {
        using HttpRequestMessage msg = new(HttpMethod.Get, path);
        msg.Headers.Add("X-Admin-Key", AdminKey);
        return await _http.SendAsync(msg);
    }

    private static StringContent JsonContent(object payload)
        => new(JsonSerializer.Serialize(payload, CampaignService.Json), Encoding.UTF8, "application/json");

    private static async Task<T> ReadJsonAsync<T>(HttpResponseMessage r)
    {
        string body = await r.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<T>(body, CampaignService.Json)
            ?? throw new InvalidOperationException("پاسخ خالی: " + body);
    }

    private static async Task<RoundInfo> CurrentRoundAsync()
    {
        HttpResponseMessage r = await _http.GetAsync("/health");
        using JsonDocument d = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
        JsonElement round = d.RootElement.GetProperty("round");
        return JsonSerializer.Deserialize<RoundInfo>(round.GetRawText(), CampaignService.Json)!;
    }

    private static async Task<RoundInfo> StartFreshRoundAsync()
    {
        HttpResponseMessage r = await AdminPostAsync("/api/v1/admin/rounds/start", new { actor = "owner" });
        string body = await r.Content.ReadAsStringAsync();
        if (r.StatusCode != HttpStatusCode.OK) throw new InvalidOperationException(body);
        return JsonSerializer.Deserialize<RoundInfo>(body, CampaignService.Json)!;
    }

    private static async Task ResetDataAsync()
    {
        await using SqlConnection cn = new(TestCs);
        await cn.OpenAsync();
        using SqlCommand cmd = new(@"
DELETE FROM dbo.InvoiceDecisions;
DELETE FROM dbo.BaleMembershipCheck;
DELETE FROM dbo.BaleRegistration;
DELETE FROM dbo.LotteryRuns;      /* آبشار به LotteryEntries */
DELETE FROM dbo.AuditLog;
DELETE FROM dbo.IngestRequests;
DELETE FROM dbo.Invoices;
DELETE FROM dbo.Rounds;
DBCC CHECKIDENT('dbo.Rounds', RESEED, 0);
DBCC CHECKIDENT('dbo.Invoices', RESEED, 0);
DBCC CHECKIDENT('dbo.InvoiceDecisions', RESEED, 0);
DBCC CHECKIDENT('dbo.LotteryRuns', RESEED, 0);", cn);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task SeedValidAsync(int roundId, int count, bool complete = false)
    {
        if (count <= 0) return;
        await using SqlConnection cn = new(TestCs);
        await cn.OpenAsync();
        using SqlCommand cmd = new(@"
;WITH n AS (
    SELECT TOP (@n) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS i
    FROM sys.all_objects a CROSS JOIN sys.all_objects b
)
INSERT INTO dbo.Invoices(SourceId, FCode, FCodeC, FType, FDate, Jalali, FTime, UserCode, Cashier,
    Gross, Discount, Net, CustomerCode, Mobile, Points, Status, RoundId, AgentId, ReceivedAt)
SELECT 'Holoo1', 500000 + i, i, 2, '2026-02-01', '1404/11/12', '12:00', 9, N'صندوق آزمون',
    5000000, 0, 5000000, '00001', '09120000000', 1, 'Valid', @rid, 'seed', SYSUTCDATETIME()
FROM n;

UPDATE dbo.Rounds SET [Count]=@n,
    Status = CASE WHEN @complete=1 THEN 'Completed' ELSE Status END,
    CompletedAt = CASE WHEN @complete=1 THEN SYSUTCDATETIME() ELSE CompletedAt END
WHERE RoundId=@rid;", cn);
        cmd.Parameters.AddWithValue("@n", count);
        cmd.Parameters.AddWithValue("@rid", roundId);
        cmd.Parameters.AddWithValue("@complete", complete ? 1 : 0);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task RecreateDatabaseAsync()
    {
        SqlConnection.ClearAllPools();
        await using (SqlConnection cn = new(MasterCs))
        {
            await cn.OpenAsync();
            using SqlCommand cmd = new($@"
IF DB_ID(N'{DbName}') IS NOT NULL
BEGIN
    ALTER DATABASE [{DbName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
    DROP DATABASE [{DbName}];
END;", cn);
            await cmd.ExecuteNonQueryAsync();
        }

        await using SqlConnection cn2 = new(MasterCs);
        await cn2.OpenAsync();
        using SqlCommand create = new($"CREATE DATABASE [{DbName}];", cn2);
        await create.ExecuteNonQueryAsync();
    }

    private static async Task DropDatabaseAsync()
    {
        SqlConnection.ClearAllPools();
        await using SqlConnection cn = new(MasterCs);
        await cn.OpenAsync();
        using SqlCommand cmd = new($@"
IF DB_ID(N'{DbName}') IS NOT NULL
BEGIN
    ALTER DATABASE [{DbName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
    DROP DATABASE [{DbName}];
END;", cn);
        await cmd.ExecuteNonQueryAsync();
    }

    private static string Dump(IngestResponse r)
        => $"accepted={r.Accepted} dup={r.Duplicates} upd={r.Updated} held={r.Held} review={r.NeedsReview} oos={r.OutOfScope} conflict={r.Conflicts}"
           + (r.Results.Count > 0 ? $" | {r.Results[0].Result}: {r.Results[0].Message}" : "");

    private static string DumpRevalidate(RevalidateResult r)
        => $"status={r.Status} round={r.RoundId} count={r.RoundCount} changed={r.CountChanged}";
}
