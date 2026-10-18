<?php
/**
 * آزمون یکپارچه‌سازی افزونه در وردپرس واقعی.
 *
 * اجرا (سرویس مرکزی جعلی باید در حال اجرا باشد):
 *   WP_ROOT=C:/xampp/htdocs/hypermarket EBI_MOCK=http://127.0.0.1:8099 \
 *   php wordpress/tests/plugin-test.php
 *
 * آزمون‌ها:
 *   • فعال‌سازی واقعی افزونه (توانایی‌ها، گزینه‌های پیش‌فرض، نسخه)
 *   • پاک‌سازی تنظیمات و مدیریت محرمانه کلیدها
 *   • رندر شورت‌کدها در همهٔ حالت‌های کمپین از دادهٔ سرویس مرکزی
 *   • کش و بی‌اعتبارسازی آن با شمارش فراخوانی‌های سرویس جعلی
 *   • جلوگیری از افشای اطلاعات خصوصی و تزریق HTML در خروجی عمومی
 *   • کنترل مجوز، نونس و تأیید صریح در عملیات مدیریتی (در فرایند جداگانه)
 */

$root = getenv('WP_ROOT') ?: 'C:/xampp/htdocs/hypermarket';
$mock = getenv('EBI_MOCK') ?: 'http://127.0.0.1:8099';
$dir  = __DIR__;
$repo = dirname($dir); // .../wordpress

$_SERVER['HTTP_HOST']   = 'localhost';
$_SERVER['REQUEST_URI'] = '/';
$_SERVER['SERVER_NAME'] = 'localhost';
$_SERVER['REQUEST_METHOD'] = 'GET';

define('WP_USE_THEMES', false);
require $root . '/wp-load.php';
require_once ABSPATH . 'wp-admin/includes/plugin.php';

require_once $repo . '/ebi-campaign/ebi-campaign.php';

$ok   = 0;
$fail = 0;

/**
 * بررسی یک انتظار.
 *
 * @param string $name عنوان.
 * @param bool   $cond نتیجه.
 * @param string $detail توضیح.
 */
function t_check(string $name, bool $cond, string $detail = ''): void {
	global $ok, $fail;
	if ($cond) {
		$ok++;
		echo "[OK]   $name\n";
		return;
	}
	$fail++;
	echo "[FAIL] $name" . ($detail !== '' ? " — $detail" : '') . "\n";
}

/**
 * تعداد فراخوانی‌های ثبت‌شده در سرویس جعلی.
 *
 * @param string|null $filter رشتهٔ فیلتر.
 * @return int
 */
function mock_calls(?string $filter = null): int {
	$log = __DIR__ . '/mock-api/calls.log';
	if (!file_exists($log)) {
		return 0;
	}
	$lines = file($log, FILE_IGNORE_NEW_LINES | FILE_SKIP_EMPTY_LINES);
	if (!is_array($lines)) {
		return 0;
	}
	if ($filter === null) {
		return count($lines);
	}
	return count(array_filter($lines, static fn( $line ) => str_contains((string) $line, $filter)));
}

/**
 * بازنشانی لاگ فراخوانی‌ها.
 */
function mock_reset_log(): void {
	file_put_contents(__DIR__ . '/mock-api/calls.log', '');
}

/**
 * تغییر وضعیت سرویس جعلی.
 *
 * @param string $key   کلید.
 * @param mixed  $value مقدار.
 */
function mock_set(string $key, $value): void {
	$file  = __DIR__ . '/mock-api/state.json';
	$state = json_decode((string) file_get_contents($file), true);
	$state = is_array($state) ? $state : array();
	$state[ $key ] = $value;
	file_put_contents($file, json_encode($state, JSON_UNESCAPED_UNICODE | JSON_PRETTY_PRINT));
}

echo "=== آزمون افزونهٔ کمپین در وردپرس واقعی ===\n";
echo 'wp=' . get_bloginfo('version') . ' php=' . PHP_VERSION . "\n\n";

// ------------------------------------------------------------------
// ۱) فعال‌سازی
// ------------------------------------------------------------------
echo "--- ۱) فعال‌سازی افزونه ---\n";
$plugin_file = 'ebi-campaign/ebi-campaign.php';
$plugin_dir  = WP_PLUGIN_DIR . '/ebi-campaign';
if (!is_dir($plugin_dir)) {
	t_check('افزونه در پوشهٔ افزونه‌های وردپرس موجود است (اتصال دایرکتوری)', false, $plugin_dir);
} else {
	$admins = get_users(array('role' => 'administrator', 'number' => 1, 'fields' => 'ID'));
	$admin_id = !empty($admins) ? (int) $admins[0] : 0;
	wp_set_current_user($admin_id);

	$result = activate_plugin($plugin_file);
	t_check('فعال‌سازی بدون خطا', !is_wp_error($result), is_wp_error($result) ? $result->get_error_message() : '');
	t_check('افزونه فعال است', is_plugin_active($plugin_file), '');

	// بدنهٔ واقعی هوک فعال‌سازی همان تابعی است که وردپرس هنگام نصب فراخوانی می‌کند.
	ebi_campaign_activate();
	// توانایی‌های تازه‌افزوده باید در کش کاربر هم دیده شوند (وردپرس همهٔ caps را کش می‌کند).
	foreach (get_users(array('role' => 'administrator', 'fields' => 'ID')) as $admin_user_id) {
		clean_user_cache((int) $admin_user_id);
	}
	wp_set_current_user($admin_id);

	$role = get_role('administrator');
	$caps = array_keys(ebi_campaign_capability_map());
	$missing = array();
	foreach ($caps as $cap) {
		if (!$role || !$role->has_cap($cap)) {
			$missing[] = $cap;
		}
	}
	t_check('توانایی‌ها به نقش مدیر اضافه شد', empty($missing), implode(',', $missing));
	t_check('گزینه‌های پیش‌فرض نصب شد', is_array(get_option(EBI_CAMPAIGN_OPTION)), '');
	t_check('نسخهٔ اسکیما ثبت شد', get_option(EBI_CAMPAIGN_VERSION_OPTION) === EBI_CAMPAIGN_VERSION, (string) get_option(EBI_CAMPAIGN_VERSION_OPTION));
}

// ------------------------------------------------------------------
// ۲) تنظیمات و پاک‌سازی ورودی
// ------------------------------------------------------------------
echo "\n--- ۲) تنظیمات و محرمانه‌ها ---\n";
$clean = Ebi_Campaign_Settings::sanitize(array(
	'api_base_url'    => $mock . '/',
	'api_timeout'     => 999,
	'cache_ttl'       => 1,
	'campaign_slug'   => 'صفحه من!',
	'winners_slug'    => 'برندگان ۱۴۰۵',
	'prize_label'     => '۱۰ میلیون تومان',
	'bale_channel'    => 'https://ble.ir/ebimarket',
	'support_text'    => 'پشتیبانی',
	'allow_insecure'  => 1,
));
t_check('مهلت پاسخ به سقف محدود می‌شود', 30 === $clean['api_timeout'], (string) $clean['api_timeout']);
t_check('مدت کش به کف محدود می‌شود', Ebi_Campaign_Settings::MIN_CACHE_TTL === $clean['cache_ttl'], (string) $clean['cache_ttl']);
t_check('نامک پاک‌سازی می‌شود', 'صفحه-من' === $clean['campaign_slug'] || '' !== $clean['campaign_slug'], $clean['campaign_slug']);
t_check('اسلش انتهایی حذف می‌شود', !str_ends_with($clean['api_base_url'], '/'), $clean['api_base_url']);

Ebi_Campaign_Settings::save($clean);
t_check('آدرس محلی روی HTTP با مجوز آزمایش پذیرفته می‌شود', !is_wp_error(Ebi_Campaign_Settings::api_base_url()), '');

$blocked = Ebi_Campaign_Settings::sanitize(array('api_base_url' => 'http://campaign.example.com', 'allow_insecure' => 1));
$saved   = Ebi_Campaign_Settings::all();
Ebi_Campaign_Settings::save($blocked);
$check = Ebi_Campaign_Settings::api_base_url();
t_check('HTTP روی میزبان غیرمحلی رد می‌شود', is_wp_error($check), '');
Ebi_Campaign_Settings::save($clean);

Ebi_Campaign_Settings::store_secret('admin', 'mock-admin-key');
Ebi_Campaign_Settings::store_secret('lottery.run', 'lottery-secret-9876');
$status = Ebi_Campaign_Settings::secret_status();
t_check('وضعیت کلید فقط چهار رقم آخر را نشان می‌دهد', '****9876' === ($status['lottery.run'] ?? ''), (string) ($status['lottery.run'] ?? ''));
t_check('کلید تنظیم‌نشده خالی است', '' === ($status['privacy.contact'] ?? 'x'), (string) ($status['privacy.contact'] ?? 'x'));
t_check('خواندن کلید برای درخواست سرور به سرور کار می‌کند', 'mock-admin-key' === Ebi_Campaign_Settings::secret('admin'), '');

ob_start();
Ebi_Campaign_Admin::page_settings();
$settings_html = (string) ob_get_clean();
t_check('صفحهٔ تنظیمات کلید را چاپ نمی‌کند', !str_contains($settings_html, 'mock-admin-key') && !str_contains($settings_html, 'lottery-secret-9876'), '');
t_check('صفحهٔ تنظیمات چهار رقم آخر را نشان می‌دهد', str_contains($settings_html, '****9876'), '');

// ------------------------------------------------------------------
// ۳) شورت‌کدها با دادهٔ سرویس مرکزی
// ------------------------------------------------------------------
echo "\n--- ۳) خروجی عمومی کمپین ---\n";
mock_reset_log();
$campaign_html = do_shortcode('[ebi_campaign]');
t_check('شمارندهٔ واقعی نمایش داده می‌شود', str_contains($campaign_html, '612'), '');
t_check('باقی‌مانده تا تکمیل نمایش داده می‌شود', str_contains($campaign_html, '388'), '');
t_check('وضعیت دوره ترجمه شده است', str_contains($campaign_html, 'در حال جمع‌آوری'), '');
t_check('نوار پیشرفت با مقدار دسترس‌پذیر', str_contains($campaign_html, 'aria-valuenow="61.2"'), '');
t_check('جایزهٔ رسمی نمایش داده می‌شود', str_contains($campaign_html, '۱۰ میلیون تومان'), '');
t_check('آخرین نتیجهٔ رسمی نمایش داده می‌شود', str_contains($campaign_html, '0912***0000'), '');
t_check('راهنمای عضویت بله نمایش داده می‌شود', str_contains($campaign_html, 'کانال بله'), '');
t_check('قوانین کمپین نمایش داده می‌شود', str_contains($campaign_html, 'قوانین و شرایط'), '');
t_check('استایل کمپین بارگذاری شده است', wp_style_is('ebi-campaign', 'enqueued'), '');

$counter_html = do_shortcode('[ebi_campaign_counter]');
t_check('شورت‌کد شمارندهٔ فشرده کار می‌کند', str_contains($counter_html, '612'), '');

// کش: رندر دوم نباید فراخوانی تازه به سرویس بزند.
$before = mock_calls('/api/v1/public/campaign/current');
do_shortcode('[ebi_campaign]');
do_shortcode('[ebi_campaign_counter]');
$after = mock_calls('/api/v1/public/campaign/current');
t_check('کش از فراخوانی تکراری جلوگیری می‌کند', $before === $after, "$before/$after");

Ebi_Campaign_Api::flush_cache();
do_shortcode('[ebi_campaign]');
$after_flush = mock_calls('/api/v1/public/campaign/current');
t_check('بی‌اعتبارسازی کش دوباره از سرویس می‌خواند', $after_flush > $after, "$after/$after_flush");

// ------------------------------------------------------------------
// ۴) حالت خطای ارتباط
// ------------------------------------------------------------------
echo "\n--- ۴) رفتار در قطع ارتباط با سرویس ---\n";
mock_set('fail_public', true);
Ebi_Campaign_Api::flush_cache();
$error_html = do_shortcode('[ebi_campaign]');
t_check('خطای ارتباط پیام روشن نشان می‌دهد', str_contains($error_html, 'در دسترس نیست'), '');
t_check('خطای ارتباط شمارندهٔ صفر جعل نمی‌کند', !str_contains($error_html, '612') && str_contains($error_html, 'صفر بودن شمارنده نیست'), '');
$error_counter = do_shortcode('[ebi_campaign_counter]');
t_check('شمارندهٔ فشرده در خطا عدد جعلی چاپ نمی‌کند', !str_contains($error_counter, '0'), $error_counter);
$error_winners = do_shortcode('[ebi_winners]');
t_check('صفحهٔ برندگان خطا را نشان می‌دهد', str_contains($error_winners, 'در دسترس نیست'), '');
mock_set('fail_public', false);
Ebi_Campaign_Api::flush_cache();

// ------------------------------------------------------------------
// ۵) آرشیو برندگان
// ------------------------------------------------------------------
echo "\n--- ۵) آرشیو برندگان ---\n";
$winners_html = do_shortcode('[ebi_winners per_page="10"]');
t_check('هر دو برندهٔ منتشرشده نمایش داده می‌شوند', 2 === substr_count($winners_html, '<article class="ebi-winner-card">'), (string) substr_count($winners_html, '<article class="ebi-winner-card">'));
t_check('شمارهٔ سانسورشده نمایش داده می‌شود', str_contains($winners_html, '0935***1111'), '');
t_check('تصویر فقط برای برندهٔ تأییدشده نمایش داده می‌شود', substr_count($winners_html, '<img') === 1, (string) substr_count($winners_html, '<img'));
t_check('نام نمایشی تزریقی پاک‌سازی می‌شود', !str_contains($winners_html, '<script>') && str_contains($winners_html, '&lt;script&gt;'), '');
t_check('صفحه‌بندی برای ادامهٔ فهرست نمایش داده می‌شود', str_contains($winners_html, 'صفحهٔ بعدی'), '');
t_check('وضعیت راستی‌آزمایی نمایش داده می‌شود', str_contains($winners_html, 'قابل بازتولید'), '');
t_check('خروجی عمومی هیچ شمارهٔ کامل موبایلی ندارد', !preg_match('/09\d{9}/', $winners_html), '');

// ------------------------------------------------------------------
// ۶) اعتبارسنجی تصویر
// ------------------------------------------------------------------
echo "\n--- ۶) اعتبارسنجی تصویر برنده ---\n";
// مبدأ فایل: مسیری که از HTTP بارگذاری نشده باید رد شود (جلوگیری از جعل tmp_name).
$fake = tempnam(sys_get_temp_dir(), 'ebi') . '.jpg';
file_put_contents($fake, "<?php echo 'pwned'; ?>");
$upload_guard = Ebi_Campaign_Images::validate_upload(array('name' => 'x.jpg', 'type' => 'image/jpeg', 'tmp_name' => $fake, 'error' => 0, 'size' => 20));
t_check('مسیر غیربارگذاری‌شده به‌عنوان فایل بارگذاری رد می‌شود', is_wp_error($upload_guard), is_wp_error($upload_guard) ? $upload_guard->get_error_code() : 'ok');

// محتوا: فایل PHP با پسوند jpg هرگز تصویر شمرده نمی‌شود، حتی اگر مسیرش مجاز باشد.
$check = Ebi_Campaign_Images::validate_image_bytes($fake, 'x.jpg');
t_check('فایل غیرتصویری با پسوند تصویر رد می‌شود', is_wp_error($check), is_wp_error($check) ? $check->get_error_code() : 'ok');

$png = tempnam(sys_get_temp_dir(), 'ebi') . '.png';
file_put_contents($png, base64_decode('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8AARAAI/wH+r4cAAAAASUVORK5CYII='));
$check_png = Ebi_Campaign_Images::validate_image_bytes($png, 'winner.png');
t_check('تصویر PNG معتبر پذیرفته می‌شود', true === $check_png, is_wp_error($check_png) ? $check_png->get_error_message() : '');
t_check('پسوند غیرمجاز برای محتوای تصویری رد می‌شود', is_wp_error(Ebi_Campaign_Images::validate_image_bytes($png, 'winner.php')), '');

$big = tempnam(sys_get_temp_dir(), 'ebi') . '.png';
file_put_contents($big, base64_decode('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8AARAAI/wH+r4cAAAAASUVORK5CYII='));
$check_big = Ebi_Campaign_Images::validate_file(array('name' => 'big.png', 'type' => 'image/png', 'tmp_name' => $big, 'error' => 0, 'size' => Ebi_Campaign_Images::MAX_BYTES + 1));
t_check('حجم بیش از حد رد می‌شود', is_wp_error($check_big), '');

t_check('آدرس غیرتصویری رد می‌شود', is_wp_error(Ebi_Campaign_Images::validate_url('https://localhost/x.php')), '');
t_check('آدرس بیرونی برای تصویر برنده رد می‌شود', !Ebi_Campaign_Images::is_local_url('https://evil.example/winner.jpg'), '');
@unlink($fake); @unlink($png); @unlink($big);

// ------------------------------------------------------------------
// ۷) پیامک: قالب، پیش‌نمایش، ارسال دستی و گزارش
// ------------------------------------------------------------------
echo "\n--- ۷) پیامک ملی‌پیامک (ارسال دستی) ---\n";

global $sms_calls, $sms_mode;
// وضعیت آزمون قبلی نباید در نتیجهٔ این اجرا اثر بگذارد.
delete_option(Ebi_Campaign_Sms::OPTION_TEMPLATES);
delete_option(Ebi_Campaign_Sms::OPTION_LOG);
$sms_calls = array();
$sms_mode  = 'sent';
add_filter('ebi_campaign_sms_transport', static function ($custom) {
	return static function (array $args) {
		global $sms_calls, $sms_mode;
		$sms_calls[] = $args;
		if ('failed' === $sms_mode) {
			return array('status' => 'failed', 'code' => '2', 'message' => 'اعتبار کافی نیست');
		}
		if ('unknown' === $sms_mode) {
			return array('status' => 'unknown', 'code' => 'timeout', 'message' => 'پاسخ دریافت نشد');
		}
		return array('status' => 'sent', 'recId' => 'RID-' . count($sms_calls), 'code' => '1', 'message' => 'ارسال شد');
	};
});

$sms_clean = Ebi_Campaign_Sms::save_settings(array(
	'username' => 'ebi-user',
	'sender'   => '1000 1234',
	'api_base' => 'https://rest.payamak-panel.com/',
));
t_check('شمارهٔ فرستنده فقط رقم نگه می‌دارد', '10001234' === $sms_clean['sender'], $sms_clean['sender']);
t_check('اسلش انتهایی آدرس پیامک حذف می‌شود', !str_ends_with($sms_clean['api_base'], '/'), $sms_clean['api_base']);

Ebi_Campaign_Settings::store_secret('sms.send', 'sms-pass-1234');
$sms_status = Ebi_Campaign_Settings::secret_status();
t_check('رمز پیامک فقط چهار رقم آخر را نشان می‌دهد', '****1234' === ($sms_status['sms.send'] ?? ''), (string) ($sms_status['sms.send'] ?? ''));

ob_start();
Ebi_Campaign_Admin::page_settings();
$settings_html = (string) ob_get_clean();
t_check('صفحهٔ تنظیمات رمز پیامک را چاپ نمی‌کند', !str_contains($settings_html, 'sms-pass-1234'), '');
t_check('صفحهٔ تنظیمات فیلد نام کاربری پیامک دارد', str_contains($settings_html, 'ebi_sms[username]'), '');
t_check('صفحهٔ تنظیمات فیلد شمارهٔ فرستنده دارد', str_contains($settings_html, 'ebi_sms[sender]'), '');

$bad_template = Ebi_Campaign_Sms::save_template(array('name' => 'بد', 'body' => 'سلام {bogus}'));
t_check('متغیر ناشناخته در قالب رد می‌شود', is_wp_error($bad_template), '');

global $sms_tpl_general, $sms_tpl_winner;
$sms_tpl_general = Ebi_Campaign_Sms::save_template(array(
	'name' => 'اطلاع‌رسانی دوره',
	'body' => 'سلام {name}، دورهٔ {round} کمپین {store} با جایزهٔ {prize} در جریان است. پشتیبانی: {support}',
));
t_check('قالب معتبر ذخیره می‌شود', is_int($sms_tpl_general) && $sms_tpl_general > 0, (string) $sms_tpl_general);

$sms_tpl_winner = Ebi_Campaign_Sms::save_template(array(
	'name' => 'تبریک برنده',
	'kind' => 'winner',
	'body' => 'تبریک {name}! شما برندهٔ دورهٔ {round} با جایزهٔ {prize} شدید. پشتیبانی: {support}',
));
t_check('قالب تبریک برنده جداگانه ذخیره می‌شود', is_int($sms_tpl_winner), '');

t_check('فهرست قالب‌ها هر دو قالب را دارد', count(Ebi_Campaign_Sms::templates()) === 2, (string) count(Ebi_Campaign_Sms::templates()));

global $sample_ctx;
$sample_ctx = array('name' => 'آقای ر.', 'round' => '2', 'prize' => '۱۰ میلیون تومان');
$rendered = Ebi_Campaign_Sms::preview((string) Ebi_Campaign_Sms::template($sms_tpl_winner)['body'], $sample_ctx);
t_check('متغیرها در پیش‌نمایش جایگزین می‌شوند', !is_wp_error($rendered) && str_contains($rendered, 'آقای ر.') && str_contains($rendered, 'دورهٔ 2'), is_wp_error($rendered) ? $rendered->get_error_message() : '');
t_check('مقدار پیش‌فرض پشتیبانی در متن درج می‌شود',
	!is_wp_error($rendered) && str_contains($rendered, (string) Ebi_Campaign_Settings::get('support_text', '')), '');
$store_preview = Ebi_Campaign_Sms::preview('کمپین {store} — دورهٔ {round}', array('round' => '1'));
t_check('نام فروشگاه به‌صورت پیش‌فرض درج می‌شود',
	!is_wp_error($store_preview) && str_contains($store_preview, get_bloginfo('name')), '');
t_check('پیش‌نمایش هیچ متغیر پرنشده ندارد', !is_wp_error($rendered) && !preg_match('/\{[a-z_]+\}/', $rendered), is_wp_error($rendered) ? '' : $rendered);

$missing = Ebi_Campaign_Sms::preview('سلام {name}، جایزهٔ {prize}.', array('name' => ''));
t_check('مقدار خالی متغیر پیش از ارسال خطا می‌دهد', is_wp_error($missing), '');
t_check('خطای متغیر خالی پیام روشن دارد', is_wp_error($missing) && str_contains($missing->get_error_message(), '{name}'), is_wp_error($missing) ? $missing->get_error_message() : '');

$recipient = array('mobile' => '09120001111', 'name' => 'آقای ر.', 'round' => '2', 'prize' => '۱۰ میلیون تومان', 'date' => '', 'masked' => false);

$admins = get_users(array('role' => 'administrator', 'number' => 1, 'fields' => 'ID'));
$sms_admin = !empty($admins) ? (int) $admins[0] : 0;
wp_set_current_user($sms_admin);

$sms_calls = array();
$blocked = Ebi_Campaign_Sms::send_manual((int) $sms_tpl_winner, array($recipient), 'tester', false);
t_check('ارسال بدون تأیید صریح انجام نمی‌شود', is_wp_error($blocked) && str_contains($blocked->get_error_message(), 'تأیید'), '');
t_check('درخواست بدون تأیید به سرویس پیامک نمی‌رسد', count($sms_calls) === 0, (string) count($sms_calls));

wp_set_current_user(0);
$no_cap = Ebi_Campaign_Sms::send_manual((int) $sms_tpl_winner, array($recipient), 'guest', true);
t_check('کاربر بدون مجوز نمی‌تواند پیامک بفرستد', is_wp_error($no_cap) && 'ebi_sms_forbidden' === $no_cap->get_error_code(), '');
t_check('درخواست بدون مجوز به سرویس پیامک نمی‌رسد', count($sms_calls) === 0, '');
wp_set_current_user($sms_admin);

$sms_mode = 'sent';
$ok_send = Ebi_Campaign_Sms::send_manual((int) $sms_tpl_winner, array($recipient), 'owner', true);
t_check('ارسال دستی با مجوز و تأیید انجام می‌شود', !is_wp_error($ok_send) && 1 === (int) $ok_send['sent'], is_wp_error($ok_send) ? $ok_send->get_error_message() : json_encode($ok_send));
t_check('متن ارسالی با متن جایگزین‌شده می‌خواند', count($sms_calls) === 1 && $sms_calls[0]['text'] === $rendered, count($sms_calls) === 1 ? $sms_calls[0]['text'] : '');

$log = Ebi_Campaign_Sms::log_entries();
t_check('ارسال موفق در تاریخچه ثبت می‌شود', !empty($log) && 'sent' === ($log[0]['status'] ?? ''), json_encode($log[0] ?? array()));
t_check('تاریخچه شمارهٔ کامل موبایل ندارد', !empty($log) && !str_contains((string) ($log[0]['to'] ?? ''), '09120001111') && str_contains((string) ($log[0]['to'] ?? ''), '***'), '');
t_check('شناسهٔ سرویس در تاریخچه ثبت می‌شود', !empty($log) && '' !== (string) ($log[0]['rec_id'] ?? ''), '');

$round_recipients = Ebi_Campaign_Sms::winners_of_round(1);
t_check('گیرندهٔ دوره از شمارهٔ کامل مدیریتی ساخته می‌شود', !is_wp_error($round_recipients) && '09121230000' === $round_recipients[0]['mobile'], is_wp_error($round_recipients) ? $round_recipients->get_error_message() : json_encode($round_recipients));

$sms_calls = array();
$round_send = is_array($round_recipients)
	? Ebi_Campaign_Sms::send_manual((int) $sms_tpl_winner, $round_recipients, 'owner', true)
	: $round_recipients;
t_check('ارسال به برندهٔ دوره با گیرندهٔ درست انجام می‌شود', !is_wp_error($round_send) && '09121230000' === ($sms_calls[0]['to'] ?? ''), is_wp_error($round_send) ? $round_send->get_error_message() : '');

$before_unknown = count($sms_calls);
$sms_mode = 'unknown';
$unknown = Ebi_Campaign_Sms::send_manual((int) $sms_tpl_general, array($recipient), 'owner', true);
t_check('پاسخ نامشخص به‌عنوان موفق گزارش نمی‌شود', !is_wp_error($unknown) && 1 === (int) $unknown['unknown'] && 0 === (int) $unknown['sent'], is_wp_error($unknown) ? $unknown->get_error_message() : json_encode($unknown));
t_check('Timeout فقط یک بار تلاش می‌کند (بدون retry خودکار)', count($sms_calls) === $before_unknown + 1, (string) count($sms_calls));

$unknown_log = Ebi_Campaign_Sms::log_entries(array('status' => 'unknown'));
t_check('وضعیت نامشخص در تاریخچه قابل مشاهده است', !empty($unknown_log) && 'unknown' === ($unknown_log[0]['status'] ?? ''), json_encode($unknown_log[0] ?? array()));
t_check('کد پاسخ نامشخص ذخیره می‌شود', !empty($unknown_log) && 'timeout' === (string) ($unknown_log[0]['code'] ?? ''), '');
$sms_mode = 'failed';
$failed = Ebi_Campaign_Sms::send_manual((int) $sms_tpl_general, array($recipient), 'owner', true);
t_check('خطای سرویس به‌عنوان ناموفق ثبت می‌شود', !is_wp_error($failed) && 1 === (int) $failed['failed'], is_wp_error($failed) ? $failed->get_error_message() : json_encode($failed));

$filtered = Ebi_Campaign_Sms::log_entries(array('status' => 'failed'));
t_check('فیلتر تاریخچه به وضعیت درست محدود می‌شود', count($filtered) === 1 && 'failed' === $filtered[0]['status'], (string) count($filtered));
$by_search = Ebi_Campaign_Sms::log_entries(array('search' => 'RID-1'));
t_check('جست‌وجو در تاریخچه بر شناسهٔ سرویس کار می‌کند', !empty($by_search), '');

$sms_calls = array();
ebi_campaign_activate();
do_shortcode('[ebi_campaign]');
do_shortcode('[ebi_winners]');
t_check('فعال‌سازی و بازدید صفحه هیچ پیامکی نمی‌فرستد', count($sms_calls) === 0, (string) count($sms_calls));

$sms_source = (string) file_get_contents(EBI_CAMPAIGN_DIR . 'includes/class-ebi-campaign-sms.php');
t_check('کلاس پیامک هیچ هوک خودکاری ثبت نمی‌کند', !str_contains($sms_source, 'add_action(') && !str_contains($sms_source, 'wp_schedule_event'), '');
t_check('کلاس پیامک هیچ صف retry ندارد', !str_contains($sms_source, 'wp_next_scheduled') && !str_contains($sms_source, 'retry_queue'), '');

ob_start();
Ebi_Campaign_Admin::page_sms();
$sms_page = (string) ob_get_clean();
t_check('صفحهٔ پیامک قالب‌ها را نشان می‌دهد', str_contains($sms_page, 'تبریک برنده'), '');
t_check('صفحهٔ پیامک تأیید صریح می‌خواهد', str_contains($sms_page, 'تأیید می‌کنم') || str_contains($sms_page, 'تأیید'), '');
t_check('صفحهٔ پیامک متن پیامک را چاپ نمی‌کند', !str_contains($sms_page, 'sms-pass-1234'), '');

// مسیر واقعی فرم پنل: نونس، مجوز، تأیید و ارسال در یک فرایند جداگانه با انتقال‌دهندهٔ جعلی.
$sms_mock_args = array(
	'op'               => 'send_sms',
	'nonce'            => 'valid',
	'template_id'      => (string) $sms_tpl_winner,
	'recipient_source' => 'test',
	'test_mobile'      => '09120002222',
	'sms_mock'         => '1',
);

$sms_denied = run_probe($sms_mock_args + array('user' => '0', 'confirm' => '1'));
t_check('فرم ارسال پیامک برای کاربر بدون مجوز اجرا نمی‌شود',
	!str_contains($sms_denied['output'], 'SMS_CALL'), $sms_denied['output']);

$sms_no_confirm = run_probe($sms_mock_args + array('user' => (string) $sms_admin, 'confirm' => '0'));
t_check('درخواست بدون تأیید به سرویس پیامک نمی‌رسد',
	!str_contains($sms_no_confirm['output'], 'SMS_CALL'), $sms_no_confirm['output']);
t_check('پیام تأیید صریح در فرم نمایش داده می‌شود',
	str_contains($sms_no_confirm['output'], 'تأیید'), $sms_no_confirm['output']);

$sms_sent = run_probe($sms_mock_args + array('user' => (string) $sms_admin, 'confirm' => '1'));
t_check('فرم پنل پیامک را به شمارهٔ آزمایشی می‌فرستد',
	str_contains($sms_sent['output'], 'SMS_CALL:09120002222'), $sms_sent['output']);
t_check('نتیجهٔ ارسال دستی به کاربر گزارش می‌شود',
	str_contains($sms_sent['output'], 'ارسال دستی انجام شد'), $sms_sent['output']);

t_check('پیام آزمایشی نامعتبر از فرم رد می‌شود',
	!str_contains(run_probe(array_merge($sms_mock_args, array('user' => (string) $sms_admin, 'confirm' => '1', 'test_mobile' => '12345')))['output'], 'SMS_CALL'), '');

$ops = Ebi_Campaign_Admin::operations();
t_check('عملیات ارسال پیامک مجوز و تأیید صریح دارد',
	isset($ops['send_sms']) && 'ebi_send_sms' === $ops['send_sms']['cap'] && true === $ops['send_sms']['confirm'], '');
t_check('پیش‌نمایش پیامک تأیید نمی‌خواهد و ارسال نمی‌کند',
	isset($ops['preview_sms']) && false === $ops['preview_sms']['confirm'], '');
t_check('حذف قالب تأیید صریح می‌خواهد',
	isset($ops['delete_sms_template']) && true === $ops['delete_sms_template']['confirm'], '');

// ------------------------------------------------------------------
// ۸) مجوز، نونس و تأیید در عملیات مدیریتی (فرایند جداگانه)
// ------------------------------------------------------------------
echo "\n--- ۸) کنترل مجوز عملیات مدیریتی ---\n";
$admins = get_users(array('role' => 'administrator', 'number' => 1, 'fields' => 'ID'));
$admin_id = !empty($admins) ? (int) $admins[0] : 0;
t_check('کاربر مدیر برای آزمون پیدا شد', $admin_id > 0, '');

/**
 * اجرای کاوشگر عملیات در فرایند جداگانه.
 *
 * @param array<string,string> $args آرگومان‌ها.
 * @return array{output:string,code:int}
 */
function run_probe(array $args): array {
	$cmd = escapeshellarg(PHP_BINARY) . ' ' . escapeshellarg(__DIR__ . '/action-probe.php');
	foreach ($args as $key => $value) {
		$cmd .= ' ' . escapeshellarg($key . '=' . $value);
	}
	$descriptors = array(1 => array('pipe', 'w'), 2 => array('pipe', 'w'));
	// محیط کامل والد باید منتقل شود؛ حذف SystemRoot در ویندوز اتصال MySQL را از کار می‌اندازد.
	$env = getenv();
	$env['WP_ROOT'] = getenv('WP_ROOT') ?: 'C:/xampp/htdocs/hypermarket';
	$process = proc_open($cmd, $descriptors, $pipes, __DIR__, $env);
	if (!is_resource($process)) {
		return array('output' => '', 'code' => -1);
	}
	$output = (string) stream_get_contents($pipes[1]) . (string) stream_get_contents($pipes[2]);
	fclose($pipes[1]);
	fclose($pipes[2]);
	$code = proc_close($process);
	return array('output' => $output, 'code' => $code);
}

mock_reset_log();
$no_cap = run_probe(array('op' => 'start_round', 'user' => '0', 'nonce' => 'valid', 'confirm' => '1'));
t_check('کاربر بدون مجوز رد می‌شود', str_contains($no_cap['output'], 'دسترسی ندارید'), $no_cap['output']);
t_check('درخواست بدون مجوز به سرویس مرکزی نمی‌رسد', 0 === mock_calls('rounds/start'), (string) mock_calls('rounds/start'));

mock_reset_log();
$bad_nonce = run_probe(array('op' => 'start_round', 'user' => (string) $admin_id, 'nonce' => 'wrong', 'confirm' => '1'));
t_check('نونس نامعتبر جلوی عملیات را می‌گیرد', 0 === mock_calls('rounds/start'), $bad_nonce['output']);

mock_reset_log();
$no_confirm = run_probe(array('op' => 'start_round', 'user' => (string) $admin_id, 'nonce' => 'valid', 'confirm' => '0'));
t_check('عملیات بدون تأیید صریح انجام نمی‌شود', 0 === mock_calls('rounds/start'), $no_confirm['output']);
t_check('پیام تأیید صریح نمایش داده می‌شود', str_contains($no_confirm['output'], 'تأیید صریح'), $no_confirm['output']);

mock_reset_log();
$good = run_probe(array('op' => 'start_round', 'user' => (string) $admin_id, 'nonce' => 'valid', 'confirm' => '1'));
t_check('مدیر مجاز با نونس و تأیید، دوره را شروع می‌کند', mock_calls('rounds/start') === 1, $good['output']);
t_check('پیام موفقیت ثبت می‌شود', str_contains($good['output'], 'آغاز شد'), $good['output']);

mock_reset_log();
$publish_probe = run_probe(array('op' => 'publish_result', 'user' => (string) $admin_id, 'nonce' => 'valid', 'confirm' => '1', 'run_id' => '1'));
t_check('عملیات انتشار نتیجه انجام می‌شود', mock_calls('lottery/1/publish') === 1, $publish_probe['output']);

$log = get_option('ebi_campaign_round_log', array());
t_check('شروع دوره در گزارش محلی ثبت شده است', is_array($log) && !empty($log), '');

echo "\n=== نتیجهٔ افزونه: $ok OK / $fail FAIL ===\n";
exit($fail === 0 ? 0 : 1);