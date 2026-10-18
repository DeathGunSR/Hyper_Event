using System.Collections.Concurrent;
using System.Text;

namespace EbiCampaign.BaleBot;

/// <summary>
/// منطق گفت‌وگوی ربات، مستقل از حمل‌ونقل (webhook یا polling) تا با Mock قابل آزمون باشد.
///
/// قواعد ثابت:
///  • شماره فقط از «اشتراک‌گذاری شماره» خود کاربر در بله پذیرفته می‌شود، نه از متن پیام.
///  • وضعیت عضویت فقط از پاسخ سرویس مرکزی می‌آید؛ خطا هیچ‌وقت «عضو» تفسیر نمی‌شود.
///  • ربات هیچ پیامکی نمی‌فرستد و هیچ نتیجه‌ای را قطعی اعلام نمی‌کند.
/// </summary>
public sealed class BotHandler
{
    private readonly IBaleApi _bale;
    private readonly ICentralApi _central;
    private readonly BotOptions _opt;
    private readonly ILogger<BotHandler> _log;
    private readonly ConcurrentDictionary<long, DateTime> _lastContactAt = new();

    public BotHandler(IBaleApi bale, ICentralApi central, BotOptions opt, ILogger<BotHandler> log)
    {
        _bale = bale;
        _central = central;
        _opt = opt;
        _log = log;
    }

    public const string TextStart = "/start";
    public const string BtnRules = "قوانین کمپین";
    public const string BtnPhone = "ثبت شماره من";
    public const string BtnStatus = "بررسی وضعیت";

    public async Task HandleAsync(BaleUpdate update, CancellationToken ct = default)
    {
        BaleMessage? msg = update.Message;
        if (msg?.Chat == null) return;

        long chatId = msg.Chat.Id;
        long baleUserId = msg.From?.Id ?? chatId;
        string firstName = msg.From?.FirstName ?? "";

        if (msg.Contact != null && !string.IsNullOrWhiteSpace(msg.Contact.PhoneNumber))
        {
            await HandleContactAsync(chatId, baleUserId, firstName, msg.Contact.PhoneNumber!, ct);
            return;
        }

        string text = (msg.Text ?? "").Trim();
        if (text.Length == 0)
        {
            await _bale.SendMessageAsync(chatId, IntroText(), BaleKeyboard.Main(), ct);
            return;
        }

        if (text.Equals(TextStart, StringComparison.OrdinalIgnoreCase) || text == "شروع")
        {
            await _bale.SendMessageAsync(chatId, IntroText(), BaleKeyboard.Main(), ct);
            return;
        }

        if (text.Contains(BtnRules, StringComparison.Ordinal))
        {
            await _bale.SendMessageAsync(chatId, RulesText(), BaleKeyboard.Main(), ct);
            return;
        }

        if (text.Contains("قوانین", StringComparison.Ordinal))
        {
            await _bale.SendMessageAsync(chatId, RulesText(), BaleKeyboard.Main(), ct);
            return;
        }

        if (text.Contains(BtnPhone, StringComparison.Ordinal) || text.Contains("شماره", StringComparison.Ordinal))
        {
            await _bale.SendMessageAsync(chatId,
                "برای اتصال شماره، دکمهٔ «ثبت شماره من» را بزنید و شمارهٔ خودتان را در بله به اشتراک بگذارید.\n"
                + "شماره‌ای که خودتان در چت تایپ کنید پذیرفته نمی‌شود؛ فقط اشتراک‌گذاری رسمی شماره معتبر است.",
                BaleKeyboard.Contact(), ct);
            return;
        }

        if (text.Contains(BtnStatus, StringComparison.Ordinal) || text.Contains("وضعیت", StringComparison.Ordinal))
        {
            await HandleStatusAsync(chatId, baleUserId, ct);
            return;
        }

        // شمارهٔ تایپ‌شده منبع معتبر مالکیت نیست؛ فقط اشتراک‌گذاری رسمی contact پذیرفته می‌شود.
        if (NormalizeMobile(text).Length > 0)
        {
            await _bale.SendMessageAsync(chatId,
                "شماره‌ای که تایپ می‌کنید ثبت نمی‌شود. لطفاً دکمهٔ «ثبت شماره من» را بزنید تا شمارهٔ خودتان با اشتراک‌گذاری رسمی بله فرستاده شود.",
                BaleKeyboard.Contact(), ct);
            return;
        }

        await _bale.SendMessageAsync(chatId,
            "برای ادامه یکی از گزینه‌های زیر را انتخاب کنید.", BaleKeyboard.Main(), ct);
    }

    private async Task HandleContactAsync(long chatId, long baleUserId, string firstName, string rawPhone,
        CancellationToken ct)
    {
        // جلوگیری از ثبت تکراری ناشی از فشار دوبارهٔ دکمه.
        DateTime now = DateTime.UtcNow;
        if (_lastContactAt.TryGetValue(baleUserId, out DateTime last)
            && (now - last) < TimeSpan.FromSeconds(60))
        {
            await _bale.SendMessageAsync(chatId,
                "شمارهٔ شما قبلاً ثبت شده است؛ برای دیدن وضعیت، «بررسی وضعیت» را بزنید.",
                BaleKeyboard.Main(), ct);
            return;
        }

        string mobile = NormalizeMobile(rawPhone);
        if (mobile.Length == 0)
        {
            await _bale.SendMessageAsync(chatId,
                "شمارهٔ اشتراک‌گذاشته‌شده معتبر نیست. لطفاً شمارهٔ موبایل ایرانی خود را از طریق دکمهٔ «ثبت شماره من» بفرستید.",
                BaleKeyboard.Contact(), ct);
            return;
        }
        _lastContactAt[baleUserId] = now;

        if (!_opt.HasCentral)
        {
            _log.LogWarning("central API is not configured; registration skipped");
            await _bale.SendMessageAsync(chatId,
                "ثبت‌نام در این لحظه ممکن نیست؛ تیم پشتیبانی از این موضوع اطلاع دارد. لطفاً بعداً تلاش کنید.",
                BaleKeyboard.Main(), ct);
            return;
        }

        RegisterOutcome outcome = await _central.RegisterAsync(mobile, baleUserId, firstName, ct);
        if (!outcome.Ok)
        {
            string message = outcome.Error switch
            {
                "mobile_registered_to_other_account" =>
                    "این شماره پیش‌تر به حساب بلهٔ دیگری متصل شده است. برای بررسی، با پشتیبانی فروشگاه تماس بگیرید.",
                "contact_not_shared" =>
                    "شماره فقط زمانی ثبت می‌شود که خودتان آن را با دکمهٔ اشتراک‌گذاری در بله بفرستید.",
                "invalid_mobile" =>
                    "شمارهٔ ارسال‌شده معتبر نیست. لطفاً شمارهٔ موبایل ایرانی خود را به اشتراک بگذارید.",
                _ => "ثبت‌نام در این لحظه انجام نشد؛ لطفاً کمی بعد دوباره تلاش کنید."
            };
            await _bale.SendMessageAsync(chatId, message, BaleKeyboard.Main(), ct);
            return;
        }

        StatusOutcome status = await _central.StatusAsync(mobile, ct);
        await _bale.SendMessageAsync(chatId, RegisteredText(outcome.MobileMasked ?? "", status),
            BaleKeyboard.Main(), ct);
    }

    private async Task HandleStatusAsync(long chatId, long baleUserId, CancellationToken ct)
    {
        if (!_opt.HasCentral)
        {
            await _bale.SendMessageAsync(chatId,
                "بررسی وضعیت در این لحظه ممکن نیست.", BaleKeyboard.Main(), ct);
            return;
        }

        string? mobile = await _central.LookupMobileAsync(baleUserId, ct);
        if (mobile == null)
        {
            await _bale.SendMessageAsync(chatId,
                "شماره‌ای برای این حساب بله ثبت نشده است. ابتدا دکمهٔ «ثبت شماره من» را بزنید.",
                BaleKeyboard.Contact(), ct);
            return;
        }

        StatusOutcome status = await _central.StatusAsync(mobile, ct);
        await _bale.SendMessageAsync(chatId,
            $"وضعیت عضویت شما: {status.StatusText}\n{status.Guidance}"
            + (string.IsNullOrWhiteSpace(_opt.ChannelUrl) ? "" : "\nکانال فروشگاه: " + _opt.ChannelUrl),
            BaleKeyboard.Main(), ct);
    }

    /// <summary>یکسان‌سازی موبایل ایران؛ همان قاعدهٔ سرویس مرکزی.</summary>
    public static string NormalizeMobile(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        StringBuilder digits = new();
        foreach (char c in raw)
            if (c >= '0' && c <= '9') digits.Append(c);

        string n = digits.ToString();
        if (n.StartsWith("0098", StringComparison.Ordinal)) n = n[4..];
        else if (n.StartsWith("98", StringComparison.Ordinal) && n.Length == 12) n = n[2..];
        if (n.Length == 10 && n[0] == '9') n = "0" + n;
        return n.Length == 11 && n.StartsWith("09", StringComparison.Ordinal) ? n : "";
    }

    private string IntroText() =>
        "به ربات کمپین فروشگاه ابی خوش آمدید.\n\n"
        + "هر ۱۰۰۰ فاکتور معتبر فروشگاه، یک دوره را تکمیل می‌کند و هر دوره یک برنده با جایزهٔ ۱۰ میلیون تومانی دارد.\n"
        + "برای شرکت در قرعه‌کشی باید شمارهٔ خود را در همین ربات ثبت کنید و در کانال فروشگاه در بله عضو باشید.\n\n"
        + (string.IsNullOrWhiteSpace(_opt.ChannelUrl) ? "" : "کانال فروشگاه: " + _opt.ChannelUrl + "\n\n")
        + "شمارهٔ شما فقط برای همین کمپین و با اجازهٔ خودتان ثبت می‌شود.";

    private static string RulesText() =>
        "قوانین و شرایط کمپین:\n"
        + "• هر فاکتور معتبر فروشگاه یک ورودی مستقل قرعه‌کشی است.\n"
        + "• شانس هر ورودی بر اساس امتیاز همان فاکتور است.\n"
        + "• هر ۱۰۰۰ فاکتور معتبر یک دوره را تکمیل می‌کند و هر دوره یک برنده دارد.\n"
        + "• عضویت در کانال فروشگاه در بله در زمان قرعه‌کشی بررسی می‌شود.\n"
        + "• شمارهٔ بدون عضویت تأییدشده در قرعه‌کشی شرکت داده نمی‌شود؛ وضعیت «نامعلوم» به‌معنای عضویت نیست.\n"
        + "• فاکتورهای تکراری و ابطال‌شده در شمارنده لحاظ نمی‌شوند.\n"
        + "• نتیجهٔ رسمی پس از قرعه‌کشی با تعهد رمزنگاری‌شده و سند قابل بازتولید اعلام می‌شود.";

    private static string RegisteredText(string mobileMasked, StatusOutcome status) =>
        "شمارهٔ شما ثبت شد: " + mobileMasked + "\n\n"
        + "وضعیت عضویت: " + status.StatusText + "\n"
        + status.Guidance;
}
