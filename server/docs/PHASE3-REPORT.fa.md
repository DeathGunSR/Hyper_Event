# گزارش فاز ۳ — API عمومی، وردپرس، ربات بله و پیامک دستی

شاخه: `phase3/campaign-web-integrations`

این گزارش با کد واقعی مخزن تطبیق داده شده است. هر قابلیتی که فقط با Mock آزموده شده و
اتصال واقعی آن اجرا نشده، صریحاً «آزمون واقعی نشده» علامت خورده است.

## ۱) آنچه واقعاً اضافه شد

### مرحلهٔ ۱ — API عمومی و ابزار حسابرسی (کامیت `9385da5`)

| فایل | قابلیت |
| --- | --- |
| `EbiCampaign.Api/PublicApi.cs` | DTOهای عمومی، کش کوتاه، محدودکنندهٔ نرخ، مسیرهای عمومی |
| `EbiCampaign.Api/migrations/005_public.sql` | ستون‌های انتشار/تصویر/نام نمایشی نتیجه |
| `EbiCampaign.Api/LotteryService.cs` | سند عمومی بدون دادهٔ شخصی، راستی‌آزمایی بازتولیدپذیر، انتشار با تأیید |
| `EbiCampaign.Tests/Program.cs` | ۷۷ بررسی فاز ۳ برای عمومی/خصوصی، نرخ و سانسور |

### مرحلهٔ ۲ — افزونهٔ وردپرس (کامیت `9dde1b2` و به‌روزرسانی‌های پیامک)

| فایل | قابلیت |
| --- | --- |
| `wordpress/ebi-campaign/ebi-campaign.php` | بارگذاری، توانایی‌ها، فعال‌سازی/غیرفعال‌سازی، نسخه‌بندی |
| `includes/class-ebi-campaign-settings.php` | تنظیمات، پاک‌سازی، کلیدهای محرمانه، HTTP محلی فقط با مجوز |
| `includes/class-ebi-campaign-api.php` | کلاینت کش‌دار عمومی + مسیرهای مدیریتی با کلید نقش |
| `includes/class-ebi-campaign-pages.php` | صفحهٔ کمپین، شمارنده، آرشیو برندگان، خطای قطع ارتباط |
| `includes/class-ebi-campaign-admin.php` | پنل دوره/بررسی/انتظار/قرعه‌کشی/برنده/پیامک/تنظیمات |
| `includes/class-ebi-campaign-images.php` | بارگذاری تصویر با کنترل نوع، حجم و میزبانی داخلی |
| `includes/class-ebi-campaign-sms.php` | قالب، متغیر، پیش‌نمایش، ارسال دستی، گزارش و وضعیت نامشخص |
| `assets/css/ebi-campaign.css` | استایل RTL و واکنش‌گرا |
| `tests/` | سرویس مرکزی جعلی، آزمون وردپرس واقعی و کاوشگر عملیات |

### مرحلهٔ ۳ — ربات بله

| فایل | قابلیت |
| --- | --- |
| `EbiCampaign.Api/BaleService.cs` | کلاینت Bot API، ثبت‌نام، جست‌وجو، وضعیت و ارائه‌دهندهٔ عضویت |
| `EbiCampaign.Api/migrations/006_bale.sql` | جدول ثبت‌نام و شاهد بررسی عضویت |
| `EbiCampaign.BaleBot/` | ربات واقعی: webhook و polling، پیام‌ها، دریافت شماره |
| `EbiCampaign.BaleBot.Tests/` | ۳۰ بررسی رفتار ربات با Bot API و سرویس مرکزی جعلی |

### مرحلهٔ ۴ — پیامک ملی‌پیامک

داخل افزونه (`class-ebi-campaign-sms.php`) با مسیر `rest.payamak-panel.com/api/SendSMS/SendSMS`
و `Content-Type: application/x-www-form-urlencoded`، مطابق SDK رسمی. ارسال فقط دستی است.

## ۲) Endpointهای واقعی و مجوز

### عمومی (بدون احراز هویت، با محدودیت نرخ و کش)

| متد | مسیر | مجوز | کش |
| --- | --- | --- | --- |
| GET | `/livez`، `/readyz`، `/health` | عمومی | ندارد |
| GET | `/api/v1/public/campaign/current` | عمومی | ۵ ثانیه |
| GET | `/api/v1/public/winners` | عمومی | ۳۰ ثانیه |
| GET | `/api/v1/public/winners/{roundNo}` | عمومی | ۳۰ ثانیه |
| GET | `/api/v1/public/lottery/{id}/receipt` | عمومی | ۶۰ ثانیه |
| GET | `/api/v1/public/lottery/{id}/entries` | عمومی | ۳۰۰ ثانیه |
| GET | `/api/v1/public/lottery/{id}/verify` | عمومی | ۶۰ ثانیه |

### عامل امضاشده (HMAC: `X-Agent-Id`، `X-Timestamp`، `X-Signature`)

| متد | مسیر | کاربرد |
| --- | --- | --- |
| POST | `/api/v1/ingest/invoices` | ثبت فاکتور از نرم‌افزار صندوق |
| GET | `/api/v1/rounds/current` | دورهٔ جاری برای صندوق |
| POST | `/api/v1/agent/bale/register` | ثبت شمارهٔ اشتراک‌گذاری‌شده توسط ربات |
| POST | `/api/v1/agent/bale/status` | وضعیت عضویت برای ربات |
| POST | `/api/v1/agent/bale/lookup` | شمارهٔ متصل به حساب بله برای ربات |

### مدیریتی (کلید `X-Admin-Key`؛ کلید کل یا کلید همان توانایی)

| متد | مسیر | توانایی |
| --- | --- | --- |
| GET | `/api/v1/admin/rounds` | هر کلید مدیر |
| POST | `/api/v1/admin/rounds/start` | `campaign.manage` |
| POST | `/api/v1/admin/rounds/{id}/close` | `campaign.manage` |
| GET | `/api/v1/admin/held` | هر کلید مدیر |
| POST | `/api/v1/admin/held/assign` | `campaign.manage` |
| GET | `/api/v1/admin/invoices` | هر کلید مدیر |
| GET | `/api/v1/admin/invoices/{id}/detail` | هر کلید مدیر |
| POST | `/api/v1/admin/invoices/{id}/decision` | `review.decide` |
| POST | `/api/v1/admin/invoices/{id}/void` | `review.decide` |
| POST | `/api/v1/admin/invoices/{id}/revalidate` | `review.decide` |
| POST | `/api/v1/admin/rounds/{id}/lottery` | `lottery.run` |
| GET | `/api/v1/admin/lottery` و `/lottery/{id}` | هر کلید مدیر |
| POST | `/api/v1/admin/lottery/{id}/select` | `lottery.run` |
| POST | `/api/v1/admin/lottery/{id}/finalize` | `lottery.run` |
| POST | `/api/v1/admin/lottery/{id}/abort` | `lottery.run` |
| GET | `/api/v1/admin/lottery/{id}/receipt` و `/verify` | هر کلید مدیر |
| GET | `/api/v1/admin/lottery/{id}/winner-contact` | `privacy.contact` |
| POST | `/api/v1/admin/lottery/{id}/publish` | `lottery.publish` |
| POST | `/api/v1/admin/lottery/{id}/unpublish` | `lottery.publish` |
| POST | `/api/v1/admin/lottery/{id}/image` | `lottery.publish` |
| GET | `/api/v1/admin/bale/registrations` | `privacy.contact` |
| POST | `/api/v1/admin/bale/check` | `privacy.contact` |

## ۳) آزمون‌هایی که اجرا شدند

| مجموعه | فرمان | نتیجه |
| --- | --- | --- |
| سرور (فاز ۱، ۲، ۳ و انتهابه‌انتها) | `dotnet run --project EbiCampaign.Tests` | **۳۱۱ OK / ۰ FAIL** |
| ربات بله (Mock) | `dotnet run --project EbiCampaign.BaleBot.Tests` | **۳۰ OK / ۰ FAIL** |
| افزونه در وردپرس واقعی | `plugin-test.php` با سرویس جعلی | **۹۹ OK / ۰ FAIL** |
| محیط وردپرس | `smoke.php` | WordPress 7.1.3، PHP 8.2، Woodmart Child، نصب زیرپوشه |

پوشش مهم سرور: شمارش و تکرار فاکتور، `Held`، تصمیم مدیریتی و شواهد، قرعه‌کشی وزن‌دار،
عدم افشای داده در مسیر عمومی، سند بازتولیدپذیر، محدودیت نرخ، ثبت‌نام بله، تعارض شماره،
تفکیک عضو/غیرعضو/نامعلوم، تفسیر `getChatMember` و مدیریت خطای شبکه.

سناریوی انتهابه‌انتها (بررسی ۵۹) واقعاً اجرا می‌شود: شروع دوره → ثبت ۱۰۰۰ فاکتور از مسیر
امضاشدهٔ عامل → تکمیل خودکار دوره → تثبیت مجموعه → انتخاب → نهایی‌سازی → انتشار →
خواندن وضعیت/برندگان/سند از مسیرهای عمومی و سبز بودن بازتولید برنده و تعهد.

پوشش مهم افزونه: نمایش همهٔ حالت‌های کمپین، کش، قطع API بدون صفر جعلی، XSS، سانسور موبایل،
تصویر نامعتبر، مجوز/نونس/تأیید، قالب و متغیر پیامک، پیش‌نمایش برابر متن ارسالی،
ثبت گزارش، وضعیت نامشخص بدون retry و اثبات نبود ارسال خودکار.

## ۴) آزمون‌هایی که اجرا نشدند (نیازمند دسترسی واقعی)

| مورد | وضعیت |
| --- | --- |
| اتصال واقعی ربات به `tapi.bale.ai` | **آزمون واقعی نشده** — توکن ربات و ادمین‌بودن در کانال لازم است |
| `getChatMember` روی کانال واقعی | **آزمون واقعی نشده** — فقط پاسخ Mock و تفسیر آن آزموده شد |
| اشتراک‌گذاری شماره در کلاینت بله | **آزمون واقعی نشده** — ساختار `message.contact` مطابق مستندات پیاده شد |
| ارسال واقعی پیامک ملی‌پیامک | **آزمون واقعی نشده** — فقط انتقال‌دهندهٔ جعلی؛ نیازی به کلید حساب و شمارهٔ آزمایشی مجاز |
| استعلام تحویل پیامک (`GetDeliveries2`) | پیاده نشده؛ وضعیت نامشخص فعلاً با بررسی دستی و شناسهٔ سرویس پیگیری می‌شود |
| قرعه‌کشی عملیاتی روی داده‌های واقعی | اجرا نشده (نیازمند تأیید صریح مالک) |
| HTTPS عملیاتی و تست مرورگر روی سایت واقعی | اجرا نشده — محیط محلی HTTP |
| آزمون بار سرور | فرمان `EBI_LOAD` موجود است ولی در این فاز اجرا نشد |

## ۵) ریسک‌ها و محدودیت‌های باقی‌مانده

1. **ملی‌پیامک:** معنای `RetStatus` در REST فقط بر پایهٔ مستندات و SDK رسمی پیاده شد؛
   موفقیت فقط با `RetStatus=1` و شناسهٔ غیرخالی پذیرفته می‌شود و بقیه «ناموفق» است.
   پیش از استفادهٔ عملی، یک ارسال آزمایشی با شمارهٔ مجاز لازم است.
2. **بله:** اگر ربات در کانال ادمین نباشد، `getChatMember` خطا می‌دهد و نتیجه «نامعلوم»
   می‌ماند (هرگز عضو تلقی نمی‌شود)؛ قرعه‌کشی با ورودی نامعلوم متوقف می‌شود.
3. **مالکیت شماره:** فقط اشتراک‌گذاری رسمی `contact` پذیرفته می‌شود. اگر بله در آینده
   `user_id` داخل contact را حذف کند، ثبت‌نام با شناسهٔ فرستنده انجام می‌شود؛ این فرض
   باید در آزمون واقعی تأیید شود.
4. **پیامک گروهی:** سقف هر دسته ۵۰۰ گیرنده و بدون صف است؛ ارسال بزرگ‌تر باید دستی و
   مرحله‌ای انجام شود. خطای میانی، ارسال‌های بعدی را متوقف نمی‌کند و همه در گزارش می‌آیند.
5. **تصویر برنده:** فقط از میزبان همان سایت؛ برای CDN جداگانه باید allowlist اضافه شود.
6. **دادهٔ محلی وردپرس:** گزارش پیامک و تنظیمات در `wp_options` است؛ برای حجم بالاتر
   باید به جدول اختصاصی منتقل شود.
7. **کلید مدیر در سایت:** اگر `admin` تنظیم شود، بقیهٔ نقش‌ها به آن fallback می‌کنند؛
   برای تفکیک کامل باید همهٔ کلیدهای نقش تنظیم شوند.

## ۶) اجرای محیط آزمایش

```bash
# سرویس مرکزی
cd server
dotnet run --project EbiCampaign.Api            # http://localhost:5080

# ربات بله (بدون توکن: هیچ پیامی ارسال نمی‌شود)
dotnet run --project EbiCampaign.BaleBot -- --poll

# آزمون‌ها
dotnet run --project EbiCampaign.Tests
dotnet run --project EbiCampaign.BaleBot.Tests

# وردپرس
php -S 127.0.0.1:8099 wordpress/tests/mock-api/router.php
WP_ROOT=/path/to/wordpress EBI_MOCK=http://127.0.0.1:8099 php wordpress/tests/plugin-test.php
```

پیکربندی بله در `EbiCampaign.Api/appsettings.json` (یا متغیر محیطی):

```json
"Bale": { "Token": "", "Channel": "@yourchannel", "ApiBase": "https://tapi.bale.ai" }
```

پیکربندی ربات (`EbiCampaign.BaleBot/appsettings.json`):

```json
"Bale": { "Token": "", "ChannelUrl": "https://ble.ir/...", "WebhookPath": "", "PublicBaseUrl": "" },
"Central": { "BaseUrl": "https://...", "AgentId": "bale-bot", "AgentSecret": "" }
```

کلید عامل ربات باید در `Auth:Agents:bale-bot` سرویس مرکزی با همان مقدار تنظیم شود.

## ۷) نصب و بازگشت

- **افزونه:** نصب زیپ از پیشخوان یا کپی پوشه؛ غیرفعال‌سازی فقط کش را پاک می‌کند و
  تنظیمات/گزارش‌ها می‌مانند. حذف افزونه داده‌های کمپین مرکزی را تغییر نمی‌دهد.
- **دیتابیس:** مهاجرت‌ها با `Db.MigrateAsync` و جدول `SchemaVersions` نسخه‌بندی شده‌اند؛
  مهاجرت ۰۰۵ و ۰۰۶ فقط اضافه می‌کنند و دادهٔ موجود را تغییر نمی‌دهند.
- **بازگشت کد:** `git checkout phase2/campaign-features` (یا کامیت `d7983e7`) بدون
  نیاز به بازگرداندن دیتابیس؛ جدول‌های تازه بی‌استفاده می‌مانند.
- **بازگشت داده:** از `EbiCampaign` بکاپ کامل بگیرید؛ هیچ تغییری در `Holoo1` انجام نشده است.

## ۸) نتیجه

API عمومی امن، افزونهٔ وردپرس با پنل کامل، صفحات کمپین/برندگان، مدیریت تصویر، ربات بله
(کد واقعی + آزمون Mock) و پیامک دستی ملی‌پیامک پیاده و آزموده شدند.
اتصال واقعی بله و ارسال واقعی پیامک، مطابق قاعدهٔ مأموریت، **تکمیل‌شده اعلام نمی‌شوند** و
پس از تأمین توکن/کلید و شمارهٔ آزمایشی باید آزمون واقعی جداگانه اجرا شود.
