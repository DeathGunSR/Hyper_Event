# EbiCampaign API — هستهٔ کمپین (فاز ۱)

سرویس مرکزی ثبت فاکتورهای معتبر فروشگاه، جلوگیری از شمارش تکراری و مدیریت دوره‌های ۱۰۰۰ فاکتوری.
این سرویس **هیچ ارتباطی با دیتابیس هلو ندارد**؛ فقط درخواست‌های امضاشدهٔ عامل‌های صندوق را می‌پذیرد.

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
curl http://127.0.0.1:5080/health
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
| `Auth:AdminKey` | خالی | خالی = مسیرهای مدیر غیرفعال (503) |
| `Auth:Agents:<agentId>` | — | کلید HMAC هر صندوق |

مهاجرت‌ها هنگام شروع به‌صورت خودکار و نسخه‌دار روی دیتابیس کمپین اجرا می‌شوند (`dbo.SchemaVersions`).

## مسیرها

| متد | مسیر | احراز هویت | کار |
|---|---|---|---|
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
      "status": "valid"
    }
  ]
}
```

`status` می‌تواند `valid` یا `review` باشد. `review` یعنی عامل همگام‌سازی این فاکتور را در منبع پیدا نکرده و آن را
مشکوک به ابطال می‌داند. اگر فاکتور در دورهٔ در حال جمع‌آوری شمرده شده باشد، از شمارنده خارج و به `NeedsReview`
منتقل می‌شود؛ اگر دوره تکمیل/بسته شده باشد، فقط علامت بررسی می‌خورد و شمارش تاریخی تغییر نمی‌کند.

### امضای درخواست

```
canonical = agentId + "\n" + timestamp + "\n" + METHOD + "\n" + path + "\n" + sha256_hex(body)
signature = HMAC_SHA256(agentSecret, canonical)  // hex
```

هدرها: `X-Agent-Id`، `X-Timestamp` (ثانیهٔ یونیکس)، `X-Signature`. اختلاف زمان بیش از ۳۰۰ ثانیه رد می‌شود.
`path` فقط مسیر است (بدون query). بدنهٔ خالی برای GET با هش SHA-256 رشتهٔ خالی امضا می‌شود.

`requestId` کلید ایدمپوتنسی است: ارسال دوبارهٔ همان شناسه و همان بدنه، پاسخ ذخیره‌شده را برمی‌گرداند؛
همان شناسه با بدنهٔ متفاوت `409` می‌گیرد.

## آزمون

```bash
# از ریشهٔ server
dotnet run --project EbiCampaign.Tests
```

آزمون‌ها دیتابیس موقت `EbiCampaign_Test` را می‌سازند، ۶۷ بررسی را اجرا می‌کنند و در پایان آن را حذف می‌کنند.
اگر SQL Express در نام نمونهٔ دیگری است، `MasterCs`/`DbName` در `EbiCampaign.Tests/Program.cs` را تنظیم کنید.

## چک‌لیست تولید

- [ ] `Auth:AdminKey` و کلید همهٔ عامل‌ها تصادفی و خارج از مخزن نگهداری شوند.
- [ ] TLS واقعی (reverse proxy) و بستن پورت مستقیم روی اینترنت.
- [ ] `Campaign:AutoCreateDatabase=false` و کاربر دیتابیس با حداقل مجوز.
- [ ] پشتیبان‌گیری دوره‌ای از دیتابیس کمپین + نگهداری/پاک‌سازی `IngestRequests` و `AuditLog`.
- [ ] پایش `/health`، صف ارسال صندوق‌ها و تعداد `Held`.
- [ ] سیاست ابطال و بازهٔ تطبیق روزانه مکتوب شود (فاز ۲).
- [ ] رمز/توکن پیامک و بله فقط در متغیر محیطی یا مخزن راز، نه در فایل پروژه.

## محدودیت‌های شناخته‌شده

- رابط مدیریتی فقط API است؛ پنل وب/وردپرس در فازهای بعدی است.
- تطبیق ابطال در برنامهٔ صندوق فقط برای «روز کاری جاری» اجرا می‌شود؛ ابطال روزهای گذشته با مسیر `void` مدیر انجام می‌شود.
- بازگشت از `NeedsReview` به `Valid` مسیر خودکار ندارد و نیازمند تصمیم مدیریتی است.
- محدودیت نرخ درخواست و WAF در این فاز پیاده نشده است.
- امتیازها در سرور با `PointsPerAmountToman` محاسبه می‌شوند؛ این مقدار باید با تنظیمات صندوق هم‌خوان باشد.
