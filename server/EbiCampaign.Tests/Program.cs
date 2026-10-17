using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using EbiCampaign.Api;
using Microsoft.AspNetCore.Builder;
using Microsoft.Data.SqlClient;

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
            });

            await ApiHost.InitializeAsync(_app);
            await _app.StartAsync();
            _baseUrl = _app.Urls.First();

            using HttpClient http = new() { BaseAddress = new Uri(_baseUrl), Timeout = TimeSpan.FromSeconds(60) };
            _http = http;

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
    //  helpers
    // ------------------------------------------------------------------
    private static async Task RunAsync(string name, Func<Task> test)
    {
        Console.WriteLine();
        Console.WriteLine("--- " + name + " ---");
        try
        {
            await ResetDataAsync();
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
DELETE FROM dbo.AuditLog;
DELETE FROM dbo.IngestRequests;
DELETE FROM dbo.Invoices;
DELETE FROM dbo.Rounds;
DBCC CHECKIDENT('dbo.Rounds', RESEED, 0);
DBCC CHECKIDENT('dbo.Invoices', RESEED, 0);", cn);
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
