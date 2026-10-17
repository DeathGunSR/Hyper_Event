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
                [$"Auth:Agents:{AgentB}"] = AgentBSecret
            }, services =>
            {
                // در استقرار واقعی، این سرویس با پیکربندی ربات بله جایگزین می‌شود؛
                // در آزمون، ارائه‌دهندهٔ قطعی و بی‌شبکه تزریق می‌شود.
                services.AddSingleton<IBaleMembershipProvider, FakeBaleProvider>();
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
        Check("الگوریتم اعلام‌شده", run.Algorithm == "ebi-lottery-weighted-v1", run.Algorithm);
        Check("Seed پیش از انتخاب افشا نمی‌شود", run.SeedHex == null, run.SeedHex ?? "null");
        Check("تعهد هش منتشر شد", run.CommitmentHash.Length == 64 && run.CommitmentHash != run.EntriesHash,
            run.CommitmentHash);

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
        Check("هش مجموعه از داده‌های Snapshot بازسازی شد", v.EntriesHashMatches, "EntriesHashMatches=false");
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
        int idx = LotteryEngine.PickWeighted(seed, LotteryEngine.Context(receipt.RoundNo, receipt.EntriesHash),
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
            after.SeedRevealed && after.EntriesHashMatches && after.CommitmentMatches && after.WinnerReproduces, "");
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
