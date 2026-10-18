<?php
/**
 * کلاینت سرویس مرکزی کمپین.
 *
 * • مسیرهای عمومی با کش کوتاه‌مدت خوانده می‌شوند تا سایت زیر فشار بازدید،
 *   سرویس مرکزی را غرق نکند.
 * • مسیرهای مدیریتی با کلید نقش همان توانایی فرستاده می‌شوند و کلید هرگز در
 *   پاسخ یا صفحه چاپ نمی‌شود.
 * • خطاهای سرویس به پیام قابل فهم ترجمه می‌شوند و جزئیات داخلی منتشر نمی‌شود.
 *
 * @package EbiCampaign
 */

defined('ABSPATH') || exit;

/**
 * ارتباط با API مرکزی.
 */
final class Ebi_Campaign_Api {

	public const CACHE_VERSION_OPTION = 'ebi_campaign_cache_version';
	private const CACHE_PREFIX       = 'ebi_c_';

	/**
	 * مصداق‌سازی یک URL عمومی.
	 *
	 * @param string               $path  مسیر نسبی.
	 * @param array<string,scalar> $query پارامترهای پرس‌وجو.
	 * @return string|WP_Error
	 */
	private static function public_url(string $path, array $query = array()) {
		$base = Ebi_Campaign_Settings::api_base_url();
		if (is_wp_error($base)) {
			return $base;
		}
		$url = $base . '/api/v1/' . ltrim($path, '/');
		if (!empty($query)) {
			$url = add_query_arg(array_map('strval', $query), $url);
		}
		return $url;
	}

	/**
	 * ساخت URL مسیر مدیریتی.
	 *
	 * @param string $path مسیر نسبی.
	 * @return string|WP_Error
	 */
	private static function admin_url(string $path) {
		$base = Ebi_Campaign_Settings::api_base_url();
		if (is_wp_error($base)) {
			return $base;
		}
		return $base . '/api/v1/admin/' . ltrim($path, '/');
	}

	/**
	 * ترجمهٔ پاسخ خطای سرویس به WP_Error بی‌خطر.
	 *
	 * @param mixed  $response پاسخ wp_remote_*.
	 * @param string $context  زمینه برای لاگ (بدون کلید).
	 * @return WP_Error
	 */
	private static function to_error($response, string $context): WP_Error {
		if (is_wp_error($response)) {
			self::log('transport', $context, 0);
			return new WP_Error(
				'ebi_unreachable',
				__('ارتباط با سرویس کمپین برقرار نشد. لطفاً بعداً دوباره تلاش کنید.', 'ebi-campaign')
			);
		}

		$status = (int) wp_remote_retrieve_response_code($response);
		$body   = (string) wp_remote_retrieve_body($response);
		$code   = '';
		$message = '';

		$decoded = json_decode($body, true);
		if (is_array($decoded)) {
			$code    = isset($decoded['code']) ? (string) $decoded['code'] : '';
			$message = isset($decoded['message']) ? (string) $decoded['message'] : '';
		}

		self::log($code !== '' ? $code : 'http_' . $status, $context, $status);

		// پیام ۴xx از سرویس مرکزی می‌آید و جزئیات داخلی ندارد، پس قابل نمایش است.
		if ($status >= 400 && $status < 500 && $message !== '') {
			return new WP_Error($code !== '' ? $code : 'ebi_client_error', $message, array('status' => $status));
		}

		return new WP_Error(
			'ebi_server_error',
			__('سرویس کمپین در این لحظه پاسخ درست نداد. لطفاً بعداً دوباره تلاش کنید.', 'ebi-campaign'),
			array('status' => $status)
		);
	}

	/**
	 * ثبت خطای بدون دادهٔ حساس.
	 *
	 * @param string $code    کد خطا.
	 * @param string $context مسیر یا عملیات.
	 * @param int    $status  کد وضعیت HTTP.
	 */
	private static function log(string $code, string $context, int $status): void {
		if (!defined('WP_DEBUG') || !WP_DEBUG) {
			return;
		}
		error_log(sprintf('[ebi-campaign] %s %s status=%d', $code, $context, $status)); // phpcs:ignore WordPress.PHP.DevelopmentFunctions.error_log_error_log
	}

	/**
	 * درخواست GET عمومی با کش.
	 *
	 * @param string               $path    مسیر نسبی.
	 * @param array<string,scalar> $query   پارامترها.
	 * @param bool                 $refresh نادیده‌گرفتن کش.
	 * @return array<string,mixed>|WP_Error
	 */
	public static function public_get(string $path, array $query = array(), bool $refresh = false) {
		$url = self::public_url($path, $query);
		if (is_wp_error($url)) {
			return $url;
		}

		$key = self::CACHE_PREFIX . self::cache_version() . '_' . md5($url);
		if (!$refresh) {
			$cached = get_transient($key);
			if (is_array($cached) && isset($cached['__ebi'])) {
				return $cached['__ebi'];
			}
		}

		$response = wp_remote_get(
			$url,
			array(
				'timeout'     => (int) Ebi_Campaign_Settings::get('api_timeout', Ebi_Campaign_Settings::DEFAULT_TIMEOUT),
				'redirection' => 0,
				'headers'     => array('Accept' => 'application/json'),
			)
		);

		if (is_wp_error($response) || (int) wp_remote_retrieve_response_code($response) !== 200) {
			return self::to_error($response, '/api/v1/' . ltrim($path, '/'));
		}

		$data = json_decode((string) wp_remote_retrieve_body($response), true);
		if (!is_array($data)) {
			self::log('bad_json', '/api/v1/' . ltrim($path, '/'), 200);
			return new WP_Error('ebi_bad_response', __('پاسخ سرویس کمپین قابل خواندن نبود.', 'ebi-campaign'));
		}

		$ttl = (int) Ebi_Campaign_Settings::get('cache_ttl', 15);
		$ttl = max(Ebi_Campaign_Settings::MIN_CACHE_TTL, min(Ebi_Campaign_Settings::MAX_CACHE_TTL, $ttl));
		set_transient($key, array('__ebi' => $data), $ttl);

		return $data;
	}

	/**
	 * درخواست مدیریتی با کلید نقش مشخص.
	 *
	 * @param string               $role    کلید نقش سرویس مرکزی.
	 * @param string               $method  GET یا POST.
	 * @param string               $path    مسیر نسبی بعد از /admin/.
	 * @param array<string,mixed>  $payload بدنهٔ JSON برای POST.
	 * @param array<string,scalar> $query   پارامترهای GET.
	 * @return array<string,mixed>|WP_Error
	 */
	public static function admin_request(string $role, string $method, string $path, array $payload = array(), array $query = array()) {
		$url = self::admin_url($path);
		if (is_wp_error($url)) {
			return $url;
		}

		$key = Ebi_Campaign_Settings::secret($role);
		if ($key === '' && $role !== 'admin') {
			$key = Ebi_Campaign_Settings::secret('admin');
		}
		if ($key === '') {
			return new WP_Error(
				ebi_campaign_missing_key_code($role),
				sprintf(
					/* translators: %s: نام توانایی. */
					__('کلید سرویس کمپین برای «%s» تنظیم نشده است.', 'ebi-campaign'),
					$role
				)
			);
		}

		if (!empty($query)) {
			$url = add_query_arg(array_map('strval', $query), $url);
		}

		$args = array(
			'method'      => strtoupper($method) === 'POST' ? 'POST' : 'GET',
			'timeout'     => (int) Ebi_Campaign_Settings::get('api_timeout', Ebi_Campaign_Settings::DEFAULT_TIMEOUT),
			'redirection' => 0,
			'headers'     => array(
				'Accept'       => 'application/json',
				'X-Admin-Key'  => $key,
				'Content-Type' => 'application/json; charset=utf-8',
			),
		);
		if ($args['method'] === 'POST') {
			$args['body'] = wp_json_encode($payload);
		}

		$response = wp_remote_request($url, $args);
		if (is_wp_error($response) || (int) wp_remote_retrieve_response_code($response) < 200
			|| (int) wp_remote_retrieve_response_code($response) >= 300) {
			return self::to_error($response, '/api/v1/admin/' . ltrim($path, '/'));
		}

		$data = json_decode((string) wp_remote_retrieve_body($response), true);
		if (!is_array($data)) {
			return new WP_Error('ebi_bad_response', __('پاسخ سرویس کمپین قابل خواندن نبود.', 'ebi-campaign'));
		}

		// هر تغییر مدیریتی، کش عمومی همان سایت را بی‌اعتبار می‌کند؛ خواندن‌ها کش را دور نمی‌ریزند.
		if ('POST' === $args['method']) {
			self::flush_cache();
		}
		return $data;
	}

	/**
	 * وضعیت کمپین.
	 *
	 * @param bool $refresh نادیده‌گرفتن کش.
	 * @return array<string,mixed>|WP_Error
	 */
	public static function campaign_status(bool $refresh = false) {
		return self::public_get('public/campaign/current', array(), $refresh);
	}

	/**
	 * فهرست برندگان منتشرشده.
	 *
	 * @param int      $page     شمارهٔ صفحه.
	 * @param int      $per_page تعداد در صفحه (۱ تا ۵۰).
	 * @param int|null $round_no فیلتر دوره.
	 * @return array<string,mixed>|WP_Error
	 */
	public static function winners(int $page = 1, int $per_page = 10, ?int $round_no = null) {
		$page     = max(1, min(1000, $page));
		$per_page = max(1, min(50, $per_page));
		$query    = array('page' => $page, 'perPage' => $per_page);
		if ($round_no !== null && $round_no > 0) {
			$query['roundNo'] = $round_no;
		}
		return self::public_get('public/winners', $query);
	}

	/**
	 * برندهٔ یک دوره.
	 *
	 * @param int $round_no شمارهٔ دوره.
	 * @return array<string,mixed>|WP_Error
	 */
	public static function winner(int $round_no) {
		return self::public_get('public/winners/' . max(1, $round_no));
	}

	/**
	 * فهرست فراگرفت‌های قرعه‌کشی (مدیریتی).
	 *
	 * @return array<int,array<string,mixed>>|WP_Error
	 */
	public static function runs() {
		return self::admin_request('lottery.run', 'GET', 'lottery');
	}

	/**
	 * شمارهٔ کامل برندهٔ نهایی‌شده؛ فقط با کلید حریم خصوصی و با ثبت حسابرسی در سرویس مرکزی.
	 *
	 * @param int $run_id شناسهٔ فراگرفت.
	 * @return array<string,mixed>|WP_Error
	 */
	public static function winner_contact(int $run_id) {
		return self::admin_request('privacy.contact', 'GET', 'lottery/' . max(1, $run_id) . '/winner-contact');
	}

	/**
	 * سند عمومی قرعه‌کشی.
	 *
	 * @param int $run_id شناسهٔ فراگرفت.
	 * @return array<string,mixed>|WP_Error
	 */
	public static function receipt(int $run_id) {
		return self::public_get('public/lottery/' . max(1, $run_id) . '/receipt');
	}

	/**
	 * نتیجهٔ راستی‌آزمایی عمومی.
	 *
	 * @param int $run_id شناسهٔ فراگرفت.
	 * @return array<string,mixed>|WP_Error
	 */
	public static function verify(int $run_id) {
		return self::public_get('public/lottery/' . max(1, $run_id) . '/verify');
	}

	/**
	 * فهرست عمومی ورودی‌ها (شناسهٔ مستعار، صلاحیت، وزن).
	 *
	 * @param int $run_id   شناسهٔ فراگرفت.
	 * @param int $page     صفحه.
	 * @param int $per_page تعداد در صفحه (۱ تا ۵۰۰).
	 * @return array<string,mixed>|WP_Error
	 */
	public static function entries(int $run_id, int $page = 1, int $per_page = 200) {
		return self::public_get(
			'public/lottery/' . max(1, $run_id) . '/entries',
			array(
				'page'    => max(1, $page),
				'perPage' => max(1, min(500, $per_page)),
			)
		);
	}

	/**
	 * نسخهٔ کش؛ با هر تغییر عوض می‌شود و کلیدهای قدیمی بی‌اثر می‌شوند.
	 *
	 * @return int
	 */
	public static function cache_version(): int {
		return (int) get_option(self::CACHE_VERSION_OPTION, 1);
	}

	/**
	 * بی‌اعتبار کردن کش عمومی سایت.
	 */
	public static function flush_cache(): void {
		update_option(self::CACHE_VERSION_OPTION, self::cache_version() + 1, false);
	}
}

/**
 * کد خطای کلید ناموجود برای یک نقش.
 *
 * @param string $role کلید نقش.
 * @return string
 */
function ebi_campaign_missing_key_code(string $role): string {
	return 'ebi_missing_key_' . preg_replace('/[^a-z0-9_.]/', '_', strtolower($role));
}