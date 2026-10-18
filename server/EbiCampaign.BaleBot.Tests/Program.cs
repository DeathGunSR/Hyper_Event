using EbiCampaign.BaleBot;
using Microsoft.Extensions.Logging.Abstractions;

// آزمون‌های ربات بله با پاسخ‌دهندهٔ جعلی؛ هیچ اتصال واقعی به بله یا سرویس مرکزی برقرار نمی‌شود.
int ok = 0, fail = 0;

void Check(string name, bool cond, string detail = "")
{
    if (cond) { ok++; Console.WriteLine($"[OK]   {name}"); return; }
    fail++;
    Console.WriteLine($"[FAIL] {name}" + (detail.Length > 0 ? $" — {detail}" : ""));
}

static BaleUpdate Text(long chatId, long userId, string text) => new()
{
    UpdateId = 1,
    Message = new BaleMessage
    {
        Chat = new BaleChat { Id = chatId },
        From = new BaleUser { Id = userId, FirstName = "آزمون" },
        Text = text
    }
};

static BaleUpdate Contact(long chatId, long userId, string phone) => new()
{
    UpdateId = 2,
    Message = new BaleMessage
    {
        Chat = new BaleChat { Id = chatId },
        From = new BaleUser { Id = userId, FirstName = "آزمون" },
        Contact = new BaleContact { PhoneNumber = phone, UserId = userId }
    }
};

static BotHandler Handler(FakeBale bale, FakeCentral central, string channel = "https://ble.ir/ebimarket") =>
    new(bale, central, new BotOptions
    {
        Token = "test",
        CentralBase = "http://central.test",
        AgentSecret = "secret",
        ChannelUrl = channel
    }, NullLogger<BotHandler>.Instance);

Console.WriteLine("=== آزمون ربات بله (Mock) ===\n");

// ۱) شروع
{
    FakeBale bale = new();
    FakeCentral central = new();
    await Handler(bale, central).HandleAsync(Text(10, 100, "/start"));
    Check("شروع، پیام معرفی می‌فرستد", bale.Sent.Count == 1 && bale.Sent[0].Text.Contains("کمپین"));
    Check("شروع، بدون فراخوانی سرویس مرکزی است", central.RegisterCalls == 0 && central.StatusCalls == 0);
    Check("شروع، دکمهٔ اشتراک‌گذاری شماره دارد", bale.Sent[0].HasContactButton);
    Check("پیام معرفی شامل قوانین عضویت است", bale.Sent[0].Text.Contains("کانال فروشگاه"));
}

// ۲) قوانین
{
    FakeBale bale = new();
    FakeCentral central = new();
    await Handler(bale, central).HandleAsync(Text(11, 101, BotHandler.BtnRules));
    Check("دکمهٔ قوانین، متن قوانین را می‌فرستد",
        bale.Sent.Count == 1 && bale.Sent[0].Text.Contains("۱۰۰۰ فاکتور"));
    Check("قوانین، نامعلوم را عضویت نمی‌داند", bale.Sent[0].Text.Contains("نامعلوم"));
}

// ۳) ثبت شماره از طریق contact
{
    FakeBale bale = new();
    FakeCentral central = new();
    central.RegisterOutcome = new RegisterOutcome(true, null, "0912***4567");
    central.StatusOutcome = new StatusOutcome(true, "member", "عضو کانال", "عضویت تأیید شد.", "0912***4567");
    await Handler(bale, central).HandleAsync(Contact(12, 102, "+989121234567"));
    Check("شمارهٔ contact به‌صورت 09 یکسان‌سازی و ثبت می‌شود",
        central.LastRegisteredMobile == "09121234567", central.LastRegisteredMobile ?? "null");
    Check("شناسهٔ حساب بله ارسال می‌شود", central.LastBaleUserId == 102L, central.LastBaleUserId.ToString());
    Check("ثبت‌نام موفق، شمارهٔ سانسورشده را نشان می‌دهد",
        bale.Sent.Count == 1 && bale.Sent[0].Text.Contains("0912***4567"));
    Check("وضعیت عضویت از سرویس مرکزی خوانده می‌شود", central.StatusCalls == 1);
}

// ۴) متن شماره به‌عنوان ثبت‌نام پذیرفته نمی‌شود
{
    FakeBale bale = new();
    FakeCentral central = new();
    await Handler(bale, central).HandleAsync(Text(13, 103, "09121234567"));
    Check("شمارهٔ تایپ‌شده در متن باعث ثبت‌نام نمی‌شود", central.RegisterCalls == 0);
    Check("ربات کاربر را به دکمهٔ اشتراک‌گذاری راهنمایی می‌کند",
        bale.Sent.Count == 1 && bale.Sent[0].Text.Contains("اشتراک‌گذاری"));
}

// ۵) contact نامعتبر
{
    FakeBale bale = new();
    FakeCentral central = new();
    await Handler(bale, central).HandleAsync(Contact(14, 104, "12345"));
    Check("شمارهٔ نامعتبر در contact ثبت نمی‌شود", central.RegisterCalls == 0);
    Check("پیام خطای شمارهٔ نامعتبر ارسال می‌شود",
        bale.Sent.Count == 1 && bale.Sent[0].Text.Contains("معتبر نیست"));
}

// ۶) تکرار سریع
{
    FakeBale bale = new();
    FakeCentral central = new();
    central.RegisterOutcome = new RegisterOutcome(true, null, "0935***1111");
    BotHandler h = Handler(bale, central);
    await h.HandleAsync(Contact(15, 105, "09351111111"));
    await h.HandleAsync(Contact(15, 105, "09351111111"));
    Check("ارسال دوبارهٔ سریع، ثبت‌نام تکراری نمی‌سازد", central.RegisterCalls == 1,
        central.RegisterCalls.ToString());
    Check("به کاربر پیام «قبلاً ثبت شده» داده می‌شود",
        bale.Sent.Count == 2 && bale.Sent[1].Text.Contains("قبلاً ثبت شده"));
}

// ۷) بررسی وضعیت بدون ثبت‌نام
{
    FakeBale bale = new();
    FakeCentral central = new();
    central.LookupMobile = null;
    await Handler(bale, central).HandleAsync(Text(16, 106, BotHandler.BtnStatus));
    Check("بدون ثبت‌نام، وضعیتی از سرویس خوانده نمی‌شود", central.StatusCalls == 0);
    Check("کاربر به ثبت شماره راهنمایی می‌شود",
        bale.Sent.Count == 1 && bale.Sent[0].Text.Contains("ثبت شماره"));
}

// ۸) وضعیت پس از ثبت‌نام
{
    FakeBale bale = new();
    FakeCentral central = new();
    central.LookupMobile = "09121234567";
    central.StatusOutcome = new StatusOutcome(true, "not_member", "عضو کانال نیست",
        "ابتدا در کانال عضو شوید.", "0912***4567");
    await Handler(bale, central).HandleAsync(Text(17, 107, BotHandler.BtnStatus));
    Check("وضعیت غیرعضو از سرویس نمایش داده می‌شود",
        bale.Sent.Count == 1 && bale.Sent[0].Text.Contains("عضو کانال نیست"));
}

// ۹) وضعیت نامعلوم هرگز تأیید عضویت نیست
{
    FakeBale bale = new();
    FakeCentral central = new();
    central.LookupMobile = "09121234567";
    central.StatusOutcome = new StatusOutcome(true, "unknown", "نامعلوم",
        "وضعیت قابل تأیید نبود.", "0912***4567");
    await Handler(bale, central).HandleAsync(Text(18, 108, BotHandler.BtnStatus));
    string text = bale.Sent[0].Text;
    Check("وضعیت نامعلوم به‌عنوان عضویت اعلام نمی‌شود",
        text.Contains("نامعلوم") && !text.Contains("عضویت شما تأیید شد"));
}

// ۱۰) تعارض شماره با حساب دیگر
{
    FakeBale bale = new();
    FakeCentral central = new();
    central.RegisterOutcome = new RegisterOutcome(false, "mobile_registered_to_other_account", null);
    await Handler(bale, central).HandleAsync(Contact(19, 109, "09121234567"));
    Check("تعارض شماره، ثبت‌نام موفق اعلام نمی‌شود",
        bale.Sent.Count == 1 && !bale.Sent[0].Text.Contains("ثبت شد") && bale.Sent[0].Text.Contains("پیش‌تر"));
    Check("تعارض شماره، وضعیت هم نمی‌خواند", central.StatusCalls == 0);
}

// ۱۱) نبود سرویس مرکزی
{
    FakeBale bale = new();
    FakeCentral central = new();
    BotHandler h = new(bale, central, new BotOptions { Token = "test", AgentSecret = "" },
        NullLogger<BotHandler>.Instance);
    await h.HandleAsync(Contact(20, 110, "09121234567"));
    Check("بدون پیکربندی سرویس مرکزی، ثبت‌نام انجام نمی‌شود", central.RegisterCalls == 0);
    Check("کاربر پیام روشن دریافت می‌کند",
        bale.Sent.Count == 1 && bale.Sent[0].Text.Contains("ممکن نیست"));
}

// ۱۲) یکسان‌سازی شماره
Check("شمارهٔ +98 پذیرفته می‌شود", BotHandler.NormalizeMobile("+98 912 123 4567") == "09121234567");
Check("شمارهٔ ۹۹۸۹... پذیرفته می‌شود", BotHandler.NormalizeMobile("00989121234567") == "09121234567");
Check("شمارهٔ ۱۰ رقمی با ۹ اصلاح می‌شود", BotHandler.NormalizeMobile("9121234567") == "09121234567");
Check("شمارهٔ کوتاه رد می‌شود", BotHandler.NormalizeMobile("0912345") == "");
Check("متن غیرشماره رد می‌شود", BotHandler.NormalizeMobile("سلام") == "");

// ۱۳) به‌روزرسانی بدون پیام
{
    FakeBale bale = new();
    FakeCentral central = new();
    await Handler(bale, central).HandleAsync(new BaleUpdate { UpdateId = 9, Message = null });
    Check("به‌روزرسانی بدون پیام نادیده گرفته می‌شود", bale.Sent.Count == 0);
}

Console.WriteLine($"\n=== نتیجهٔ ربات: {ok} OK / {fail} FAIL ===");
return fail == 0 ? 0 : 1;

sealed class FakeBale : IBaleApi
{
    public List<(long ChatId, string Text, bool HasContactButton)> Sent { get; } = new();

    public Task<bool> SendMessageAsync(long chatId, string text, BaleKeyboard? keyboard,
        CancellationToken ct = default)
    {
        Sent.Add((chatId, text, keyboard?.HasContactRequest == true));
        return Task.FromResult(true);
    }

    public Task<IReadOnlyList<BaleUpdate>> GetUpdatesAsync(long offset, int timeoutSeconds,
        CancellationToken ct = default)
        => Task.FromResult((IReadOnlyList<BaleUpdate>)Array.Empty<BaleUpdate>());

    public Task SetWebhookAsync(string url, CancellationToken ct = default) => Task.CompletedTask;
}

sealed class FakeCentral : ICentralApi
{
    public int RegisterCalls { get; private set; }
    public int StatusCalls { get; private set; }
    public string? LastRegisteredMobile { get; private set; }
    public long LastBaleUserId { get; private set; }
    public RegisterOutcome RegisterOutcome { get; set; } = new(true, null, "0912***4567");
    public StatusOutcome StatusOutcome { get; set; } =
        new(true, "member", "عضو کانال", "عضویت تأیید شد.", "0912***4567");
    public string? LookupMobile { get; set; } = "09121234567";

    public Task<RegisterOutcome> RegisterAsync(string mobile, long baleUserId, string firstName,
        CancellationToken ct = default)
    {
        RegisterCalls++;
        LastRegisteredMobile = mobile;
        LastBaleUserId = baleUserId;
        return Task.FromResult(RegisterOutcome);
    }

    public Task<string?> LookupMobileAsync(long baleUserId, CancellationToken ct = default)
        => Task.FromResult(LookupMobile);

    public Task<StatusOutcome> StatusAsync(string mobile, CancellationToken ct = default)
    {
        StatusCalls++;
        return Task.FromResult(StatusOutcome);
    }
}
