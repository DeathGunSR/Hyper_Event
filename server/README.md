# EbiCampaign API — هستهٔ کمپین (فاز ۱ و ۲)

سرویس مرکزی ثبت فاکتورهای معتبر فروشگاه، جلوگیری از شمارش تکراری و مدیریت دوره‌های ۱۰۰۰ فاکتوری.
این سرویس **هیچ ارتباطی با دیتابیس هلو ندارد**؛ فقط درخواست‌های امضاشدهٔ عامل‌های صندوق را می‌پذیرد.

مستندهای فاز ۲:
[گزارش فاز ۲](docs/PHASE2-REPORT.fa.md) · [آمادگی زیرساخت](docs/PHASE2-INFRA.fa.md)

## پیش‌نیازها

- .NET SDK 10
- SQL Server (نسخهٔ آزمون: SQL Server Express 17 / LocalDB سازگار)
- یک دیتابیس خالی یا مجوز `CREATE DATABASE` برای حالت توسعه
- دسترسی شبکهٔ صندوق‌ها به پورت سرویس (پیش‌فرض `5080`)

## اجرا

هیچ کلید پیش‌فرضی در `appsettings.json` وجود ندارد. پیش از اجرا، کلید مدیر و کلید هر عامل را تنظیم کنید.
تنظیمات با متغیر محیطی (اولویت بالاتر از فایل) یا فایل محلیِ خارج از مخزن انجام می‌شود.

نمونهٔ PowerShell:

```powershell
$env:ConnectionStrings__CampaignDb = "Server=.\SQLEXPRESS;Database=EbiCampaign;Integrated Security=True;Encrypt=False;TrustServerCertificate=True"
$env:Auth__AdminKey = "<admin-key-تصادفی-بلند>"
$env:Auth__Agents__cashier-pc-1 = "<agent-secret-تصادفی>"
$env:Campaign__StartDate = "2026-02-09"
$env:Campaign__TargetInvoices = "1000"
dotnet run --project EbiCampaign.Api
```

نمونهٔ bash:

```bash
export ConnectionStrings__CampaignDb='Server=.\SQLEXPRESS;Database=EbiCampaign;Integrated Security=True;Encrypt=False;TrustServerCertificate=True'
export Auth__AdminKey='<admin-key>'
export 'Auth__Agents__cashier-pc-1=<agent-secret>'
dotnet run --project EbiCampaign.Api
```

پورت پیش‌فرض در `appsettings.json` روی `http://0.0.0.0:5080` تنظیم شده تا صندوق‌های شبکه به آن دسترسی داشته باشند.
برای تولید، سرویس را پشت reverse proxy با TLS قرار دهید و آدرس `https://...` را در برنامهٔ صندوق وارد کنید.

بررسی سلامت:

```bash
curl http://127.0.0.1:5080/livez    # زیست سرویس، بدون دیتابیس (برای کاوشگر لبه)
curl http://127.0.0.1:5080/readyz   # آمادگی وابستگی (دیتابیس)
curl http://127.0.0.1:5080/health   # سلامت تفصیلی + دورهٔ جاری (ابزار تشخیص انسانی)
```

## پیکربندی

| کلید | پیش‌فرض | توضیح |
|---|---|---|
| `Urls` | `http://0.0.0.0:5080` | آدرس شنود Kestrel |
| `ConnectionStrings:CampaignDb` | `.\SQLEXPRESS / EbiCampaign` | دیتابیس مستقل کمپین |
| `Campaign:SourceId` | `Holoo1` | باید با `Database` در تنظیمات صندوق یکی باشد |
| `Campaign:TargetInvoices` | `1000` | اندازهٔ هر دوره |
| `Campaign:StartDate` | `2026-02-09` | تاریخ میلادی شروع کمپین؛ فاکتورهای قدیمی‌تر رد می‌شوند |
| `Campaign:PointsPerAmountToman` | `500000` | هر چند تومان خرید = ۱ امتیاز |
| `Campaign:PointsBase` | `gross` | `gross` یا `net` |
| `Campaign:AutoCreateDatabase` | `true` | فقط برای توسعه؛ در تولید `false` و دیتابیس از پیش ساخته شود |
| `Campaign:RequireBaleMembership` | `true` | الزام عضویت کانال بله در زمان قرعه‌کشی |
| `Auth:AdminKey` | خالی | خالی = مسیرهای مدیر غیرفعال (503) |
| `Auth:Agents:<agentId>` | — | کلید HMAC هر صندوق |

مهاجرت‌ها هنگام شروع به‌صورت خودکار و نسخه‌دار روی دیتابیس کمپین اجرا می‌شوند (`dbo.SchemaVersions`).
نسخه‌های ۳ و ۴ (`003_decisions.sql`، `004_lottery.sql`) جدول‌های تصمیم مدیر و قرعه‌کشی را می‌سازند و
افزایشی‌اند؛ روی دیتابیس فاز ۱ بدون از دست رفتن داده اجرا می‌شوند.

## مسیرها

| متد | مسیر | احراز هویت | کار |
|---|---|---|---|
| GET | `/livez` | عمومی | زیست سرویس بدون دیتابیس |
| GET | `/readyz` | عمومی | آمادگی وابستگی (دیتابیس) |
| GET | `/health` | عمومی | سلامت + نسخهٔ اسکیما + دورهٔ جاری |
| POST | `/api/v1/ingest/invoices` | HMAC عامل | ثبت دستهٔ فاکتور (۱ تا ۵۰۰) |
| GET | `/api/v1/rounds/current` | HMAC عامل | شمارندهٔ دورهٔ جاری |
| GET | `/api/v1/admin/rounds` | `X-Admin-Key` | فهرست دوره‌ها |
| POST | `/api/v1/admin/rounds/start` | `X-Admin-Key` | باز کردن دورهٔ جدید |
| POST | `/api/v1/admin/rounds/{id}/close` | `X-Admin-Key` | بستن دورهٔ تکمیل‌شده |
| GET | `/api/v1/admin/held` | `X-Admin-Key` | فاکتورهای در انتظار |
| POST | `/api/v1/admin/held/assign` | `X-Admin-Key` | تخصیص صریح Held به دورهٔ باز |
| GET | `/api/v1/admin/invoices` | `X-Admin-Key` | فهرست فاکتورها (فیلتر دوره/وضعیت) |
| POST | `/api/v1/admin/invoices/{id}/void` | `X-Admin-Key` | ابطال با دلیل |
| POST | `/api/v1/admin/invoices/{id}/revalidate` | `X-Admin-Key` | تأیید فاکتور NeedsReview (دورهٔ باز یا Held) |
| GET | `/api/v1/admin/invoices/{id}/detail` | `X-Admin-Key` | فاکتور + تاریخچهٔ تصمیم‌ها + وضعیت نتیجهٔ دوره |
| POST | `/api/v1/admin/invoices/{id}/decision` | `X-Admin-Key` | تصمیم مدیر: `valid` یا `void` با دلیل، `decisionKey` و `force` |
| POST | `/api/v1/admin/rounds/{id}/lottery` | `X-Admin-Key` | تثبیت مجموعهٔ ورودی‌ها و تعهد هش |
| GET | `/api/v1/admin/lottery` | `X-Admin-Key` | فهرست فراگرفت‌های قرعه‌کشی |
| GET | `/api/v1/admin/lottery/{id}` | `X-Admin-Key` | جزئیات فراگرفت و ورودی‌ها (`?entries=false`) |
| POST | `/api/v1/admin/lottery/{id}/select` | `X-Admin-Key` | انتخاب وزنی برنده و افشای Seed |
| POST | `/api/v1/admin/lottery/{id}/finalize` | `X-Admin-Key` | نهایی‌سازی نتیجه |
| POST | `/api/v1/admin/lottery/{id}/abort` | `X-Admin-Key` | لغو فراگرفت پیش از انتخاب (با دلیل) |
| GET | `/api/v1/admin/lottery/{id}/receipt` | `X-Admin-Key` | سند حسابرسی + فرمول‌های بازتولید |
| GET | `/api/v1/admin/lottery/{id}/verify` | `X-Admin-Key` | بازمحاسبهٔ هش مجموعه، تعهد و برنده |
| GET | `/api/v1/admin/lottery/{id}/winner-contact` | `X-Admin-Key` | شمارهٔ کامل برنده (ثبت‌شده در AuditLog) |

### قرارداد ثبت فاکتور

```json
{
  "requestId": "8f0c...",
  "sourceId": "Holoo1",
  "invoices": [
    {
      "fCode": 590670,
      "fCodeC": 21169,
      "fType": 2,
      "fDate": "2026-02-09",
      "jalali": "1404/11/20",
      "fTime": "15:31",
      "userCode": 9,
      "cashier": "خانم ایمانی",
      "gross": 5000000,
      "discount": 0,
      "net": 5000000,
      "customerCode": "00001",
      "mobile": "09123456789",
      "status": "valid",
      "reviewReason": "",
      "reviewEvidence": ""
    }
  ]
}
```

`status` می‌تواند `valid` یا `review` باشد. `review` یعنی عامل همگام‌سازی این فاکتور را در منبع پیدا نکرده یا
نوع/مبلغ آن را نامعتبر دیده است. `reviewReason` یکی از `missing`، `voided`، `invalid` یا `source` است و
`reviewEvidence` شواهد کوتاه تطبیق را نگه می‌دارد. اگر فاکتور در دورهٔ در حال جمع‌آوری شمرده شده باشد، از
شمارنده خارج و به `NeedsReview` منتقل می‌شود؛ اگر دوره تکمیل/بسته شده باشد، فقط علامت بررسی می‌خورد و
شمارش تاریخی تغییر نمی‌کند. تأیید نهایی با مسیر `revalidate` است: فاکتور آزادشده در صورت ظرفیت به دورهٔ باز
برمی‌گردد، در غیر این صورت `Held` می‌شود؛ فاکتور تاریخی فقط `Valid` می‌شود و شمارش را تغییر نمی‌دهد.

### امضای درخواست

```
canonical = agentId + "\n" + timestamp + "\n" + METHOD + "\n" + path + "\n" + sha256_hex(body)
signature = HMAC_SHA256(agentSecret, canonical)  // hex
```

هدرها: `X-Agent-Id`، `X-Timestamp` (ثانیهٔ یونیکس)، `X-Signature`. اختلاف زمان بیش از ۳۰۰ ثانیه رد می‌شود.
`path` فقط مسیر است (بدون query). بدنهٔ خالی برای GET با هش SHA-256 رشتهٔ خالی امضا می‌شود.

`requestId` کلید ایدمپوتنسی است: ارسال دوبارهٔ همان شناسه و همان بدنه، پاسخ ذخیره‌شده را برمی‌گرداند؛
همان شناسه با بدنهٔ متفاوت `409` می‌گیرد.

## گردش‌کار قرعه‌کشی (بند ۵)

۱. دوره با رسیدن به `Target` خودکار `Completed` می‌شود (شمارش فقط فاکتور `Valid`).
۲. `POST /admin/rounds/{id}/lottery` مجموعهٔ ورودی‌ها را تثبیت می‌کند، عضویت بله را بررسی می‌کند و
   `CommitmentHash` را منتشر می‌سازد. تا این مرحله `seedHex` در پاسخ `null` است.
۳. `POST /admin/lottery/{id}/select` برنده را با CSPRNG و وزن امتیازی انتخاب می‌کند و Seed را افشا می‌کند.
۴. `GET /admin/lottery/{id}/verify` باید هر سه بررسی را سبز نشان دهد: `entriesHashMatches`،
   `commitmentMatches`، `winnerReproduces`. هر ممیز بیرونی هم می‌تواند با `receipt` همین را بازتولید کند.
۵. `POST /admin/lottery/{id}/finalize` نتیجه را رسمی می‌کند. پس از آن، هر تغییر وضعیت فاکتور آن دوره
   فقط با مسیر ویژهٔ `decision` و `force=true` + دلیل مکتوب ممکن است و فراگرفت برای بازبینی علامت می‌خورد.

## آزمون

```bash
# از ریشهٔ server
dotnet run --project EbiCampaign.Tests                 # ۱۹۰ بررسی (۷۸ فاز ۱ + ۱۱۲ فاز ۲)
EBI_LOAD=800x32 dotnet run --project EbiCampaign.Tests # آزمون بار: ۸۰۰ ارسال با همزمانی ۳۲
```

آزمون‌ها دیتابیس موقت `EbiCampaign_Test` را می‌سازند، همهٔ بررسی‌ها را اجرا می‌کنند و در پایان آن را حذف
می‌کنند. آزمون قرعه‌کشی با ارائه‌دهندهٔ جعلی عضویت بله تزریق‌شده اجرا می‌شود (`configureServices` در
آزمون) و هیچ ارتباط شبکه‌ای با بله ندارد.
اگر SQL Express در نام نمونهٔ دیگری است، `MasterCs`/`DbName` در `EbiCampaign.Tests/Program.cs` را تنظیم کنید.

## چک‌لیست تولید

- [ ] `Auth:AdminKey` و کلید همهٔ عامل‌ها تصادفی و خارج از مخزن نگهداری شوند.
- [ ] TLS واقعی (reverse proxy) و بستن پورت مستقیم روی اینترنت.
- [ ] `Campaign:AutoCreateDatabase=false` و کاربر دیتابیس با حداقل مجوز.
- [ ] پشتیبان‌گیری دوره‌ای از دیتابیس کمپین + نگهداری/پاک‌سازی `IngestRequests` و `AuditLog`.
- [ ] پایش `/livez` و `/readyz` (و `/health` به‌صورت دوره‌ای)، صف ارسال صندوق‌ها و تعداد `Held`.
- [ ] `Max Pool Size` مخزن اتصال متناسب با ظرفیت SQL و تعداد نمونه‌ها.
- [ ] سیاست ابطال و بازهٔ تطبیق روزانه مکتوب شود (بند ۴.۱ انجام شد؛ تصمیم سیاست انتظار بند ۳.۳ باز است).
- [ ] پشتیبان‌گیری و **آزمون بازیابی** انجام و مستند شود.
- [ ] رمز/توکن پیامک و بله فقط در متغیر محیطی یا مخزن راز، نه در فایل پروژه.

## محدودیت‌های شناخته‌شده

- رابط مدیریتی فقط API است؛ پنل وب/وردپرس در فازهای بعدی است.
- تطبیق ابطال در برنامهٔ صندوق فقط برای «روز کاری جاری» اجرا می‌شود؛ ابطال روزهای گذشته با مسیر `void` مدیر انجام می‌شود.
- بازگشت از `NeedsReview` به `Valid` خودکار نیست و فقط با مسیر `revalidate` و تصمیم مدیر انجام می‌شود.
- محدودیت نرخ درخواست و WAF در این فاز پیاده نشده است.
- آزمون بازیابی پشتیبان روی محیط کنار انجام نشده است.
- امتیازها در سرور با `PointsPerAmountToman` محاسبه می‌شوند؛ این مقدار باید با تنظیمات صندوق هم‌خوان باشد.
