<?php
/**
 * تنظیمات افزونه و مدیریت کلیدهای سرویس‌به‌سرویس.
 *
 * کلیدها هرگز در HTML، JavaScript یا پاسخ REST چاپ نمی‌شوند. اگر ثابت‌های
 * wp-config.php تعریف شده باشند بر گزینه‌ها اولویت دارند، چون در پایگاه داده
 * ذخیره نمی‌شوند.
 *
 * @package EbiCampaign
 */

defined('ABSPATH') || exit;

/**
 * مدیریت تنظیمات کمپین.
 */
final class Ebi_Campaign_Settings {

	public const DEFAULT_TIMEOUT = 8;
	public const MIN_CACHE_TTL  = 5;
	public const MAX_CACHE_TTL  = 300;

	/**
	 * مقادیر پیش‌فرض.
	 *
	 * @return array<string,mixed>
	 */
	public static function defaults(): array {
		return array(
			'api_base_url'   => 'https://ebimarket.ir',
			'api_timeout'    => self::DEFAULT_TIMEOUT,
			'cache_ttl'      => 15,
			'allow_insecure' => 0,
			'campaign_slug'  => 'campaign',
			'winners_slug'   => 'winners',
			'prize_label'    => '۱۰ میلیون تومان',
			'bale_channel'   => '',
			'support_text'   => 'پشتیبانی فروشگاه ابی',
		);
	}

	/**
	 * نصب مقادیر اولیهٔ گزینه‌ها در فعال‌سازی.
	 */
	public static function install_defaults(): void {
		$current = get_option(EBI_CAMPAIGN_OPTION, array());
		if (!is_array($current)) {
			$current = array();
		}
		update_option(EBI_CAMPAIGN_OPTION, array_merge(self::defaults(), $current));
	}

	/**
	 * مهاجرت نسخه‌بندی‌شدهٔ تنظیمات.
	 */
	public static function maybe_upgrade(): void {
		$stored = get_option(EBI_CAMPAIGN_VERSION_OPTION);
		if ($stored === EBI_CAMPAIGN_VERSION) {
			return;
		}
		$settings = self::all();
		// نسخهٔ ۰.۳.۰: افزودن آدرس کانال بله و متن پشتیبانی به تنظیمات ذخیره‌شده.
		$merged = array_merge(self::defaults(), $settings);
		update_option(EBI_CAMPAIGN_OPTION, self::sanitize($merged), false);
		update_option(EBI_CAMPAIGN_VERSION_OPTION, EBI_CAMPAIGN_VERSION, false);
	}

	/**
	 * خواندن همهٔ تنظیمات.
	 *
	 * @return array<string,mixed>
	 */
	public static function all(): array {
		$stored = get_option(EBI_CAMPAIGN_OPTION, array());
		if (!is_array($stored)) {
			$stored = array();
		}
		return array_merge(self::defaults(), $stored);
	}

	/**
	 * یک تنظیم.
	 *
	 * @param string $key     کلید.
	 * @param mixed  $default مقدار پیش‌فرض.
	 * @return mixed
	 */
	public static function get(string $key, $default = null) {
		$all = self::all();
		return array_key_exists($key, $all) ? $all[$key] : $default;
	}

	/**
	 * پاک‌سازی ورودی فرم تنظیمات.
	 *
	 * @param array<string,mixed> $input ورودی خام.
	 * @return array<string,mixed>
	 */
	public static function sanitize(array $input): array {
		$defaults = self::defaults();
		$base     = isset($input['api_base_url']) ? esc_url_raw(trim((string) $input['api_base_url'])) : $defaults['api_base_url'];
		$base     = untrailingslashit($base);

		$timeout = isset($input['api_timeout']) ? (int) $input['api_timeout'] : $defaults['api_timeout'];
		$timeout = max(2, min(30, $timeout));

		$ttl = isset($input['cache_ttl']) ? (int) $input['cache_ttl'] : $defaults['cache_ttl'];
		$ttl = max(self::MIN_CACHE_TTL, min(self::MAX_CACHE_TTL, $ttl));

		return array(
			'api_base_url'   => $base,
			'api_timeout'    => $timeout,
			'cache_ttl'      => $ttl,
			'allow_insecure' => empty($input['allow_insecure']) ? 0 : 1,
			'campaign_slug'  => isset($input['campaign_slug']) ? sanitize_title((string) $input['campaign_slug']) : $defaults['campaign_slug'],
			'winners_slug'   => isset($input['winners_slug']) ? sanitize_title((string) $input['winners_slug']) : $defaults['winners_slug'],
			'prize_label'    => isset($input['prize_label']) ? sanitize_text_field((string) $input['prize_label']) : $defaults['prize_label'],
			'bale_channel'   => isset($input['bale_channel']) ? esc_url_raw(trim((string) $input['bale_channel'])) : '',
			'support_text'   => isset($input['support_text']) ? sanitize_text_field((string) $input['support_text']) : $defaults['support_text'],
		);
	}

	/**
	 * ذخیرهٔ تنظیمات.
	 *
	 * @param array<string,mixed> $input ورودی خام فرم.
	 * @return array<string,mixed> تنظیمات ذخیره‌شده.
	 */
	public static function save(array $input): array {
		$clean = self::sanitize($input);
		update_option(EBI_CAMPAIGN_OPTION, $clean);
		Ebi_Campaign_Api::flush_cache();
		return $clean;
	}

	/**
	 * نگاشت کلیدهای سرویس‌به‌سرویس: ثابت wp-config.php بر گزینه اولویت دارد.
	 *
	 * @return array<string,string> کلید نقش به مقدار کلید.
	 */
	public static function secret_map(): array {
		$constants = array(
			'admin'           => 'EBI_CAMPAIGN_ADMIN_KEY',
			'campaign.manage' => 'EBI_CAMPAIGN_CAMPAIGN_KEY',
			'review.decide'   => 'EBI_CAMPAIGN_REVIEW_KEY',
			'lottery.run'     => 'EBI_CAMPAIGN_LOTTERY_KEY',
			'lottery.publish' => 'EBI_CAMPAIGN_PUBLISH_KEY',
			'privacy.contact' => 'EBI_CAMPAIGN_PRIVACY_KEY',
		);

		$keys = array();
		foreach ($constants as $role => $constant) {
			if (defined($constant) && constant($constant)) {
				$keys[$role] = (string) constant($constant);
			}
		}

		$stored = get_option('ebi_campaign_keys', array());
		if (is_array($stored)) {
			foreach ($stored as $role => $value) {
				if (is_string($value) && $value !== '' && !isset($keys[$role])) {
					$keys[$role] = $value;
				}
			}
		}

		return $keys;
	}

	/**
	 * ذخیرهٔ کلید یک نقش در گزینه‌ها (اگر ثابت تعریف نشده باشد).
	 *
	 * @param string      $role  کلید نقش سرویس مرکزی.
	 * @param string|null $value مقدار جدید؛ null یعنی حذف.
	 */
	public static function store_secret(string $role, ?string $value): void {
		$allowed = array('admin', 'campaign.manage', 'review.decide', 'lottery.run', 'lottery.publish', 'privacy.contact');
		if (!in_array($role, $allowed, true)) {
			return;
		}
		$stored = get_option('ebi_campaign_keys', array());
		if (!is_array($stored)) {
			$stored = array();
		}
		if ($value === null || trim($value) === '') {
			unset($stored[$role]);
		} else {
			$stored[$role] = trim($value);
		}
		update_option('ebi_campaign_keys', $stored, false);
	}

	/**
	 * کلید یک نقش.
	 *
	 * @param string $role کلید نقش.
	 * @return string
	 */
	public static function secret(string $role): string {
		$keys = self::secret_map();
		return isset($keys[$role]) ? $keys[$role] : '';
	}

	/**
	 * وضعیت تنظیم کلیدها برای نمایش در پنل — بدون افشای مقدار.
	 *
	 * @return array<string,string> نقش به وضعیت «تعریف‌شده/تعریف‌نشده» و چهار رقم آخر.
	 */
	public static function secret_status(): array {
		$keys     = self::secret_map();
		$status   = array();
		$explicit = array('admin', 'campaign.manage', 'review.decide', 'lottery.run', 'lottery.publish', 'privacy.contact');
		foreach ($explicit as $role) {
			$value = isset($keys[$role]) ? $keys[$role] : '';
			if ($value === '') {
				$status[$role] = '';
				continue;
			}
			$tail          = substr($value, -4);
			$status[$role] = '****' . $tail;
		}
		return $status;
	}

	/**
	 * آدرس پایهٔ سرویس مرکزی یا خطا در صورت ناامن‌بودن.
	 *
	 * @return string|WP_Error
	 */
	public static function api_base_url() {
		$base = untrailingslashit((string) self::get('api_base_url', ''));
		if ($base === '') {
			return new WP_Error('ebi_no_base_url', __('آدرس سرویس مرکزی کمپین تنظیم نشده است.', 'ebi-campaign'));
		}
		$scheme = strtolower((string) wp_parse_url($base, PHP_URL_SCHEME));
		if ($scheme === 'https') {
			return $base;
		}
		if (!self::get('allow_insecure', 0)) {
			return new WP_Error(
				'ebi_insecure_url',
				__('ارتباط با سرویس کمپین باید روی HTTPS باشد. برای محیط آزمایش محلی می‌توانید این محدودیت را از تنظیمات فعال کنید.', 'ebi-campaign')
			);
		}
		if ($scheme === 'http' && !self::is_loopback($base)) {
			return new WP_Error(
				'ebi_insecure_host',
				__('آدرس HTTP فقط برای میزبان محلی (localhost/127.0.0.1) مجاز است.', 'ebi-campaign')
			);
		}
		return $base;
	}

	/**
	 * تشخیص میزبان محلی.
	 *
	 * @param string $url آدرس.
	 * @return bool
	 */
	public static function is_loopback(string $url): bool {
		$host = strtolower((string) wp_parse_url($url, PHP_URL_HOST));
		return in_array($host, array('localhost', '127.0.0.1', '::1'), true);
	}
}