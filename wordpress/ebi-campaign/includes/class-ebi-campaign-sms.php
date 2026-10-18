<?php
/**
 * پیامک ملی‌پیامک — فقط ارسال دستی.
 *
 * هیچ رویداد کمپین (تکمیل دوره، قرعه‌کشی، ثبت برنده) پیامک نمی‌فرستد و هیچ
 * Job یا صف retry‌ای وجود ندارد. تنها مسیر ارسال، فرم پنل با مجوز
 * `ebi_send_sms`، نونس و تأیید صریح متن و تعداد گیرندگان است.
 *
 * پاسخ مبهم یا Timeout به وضعیت «نامشخص» می‌رود و هرگز خودکار تکرار نمی‌شود.
 *
 * @package EbiCampaign
 */

defined('ABSPATH') || exit;

/**
 * قالب‌ها، اعتبارسنجی متغیرها، ارسال دستی و گزارش ارسال پیامک.
 */
final class Ebi_Campaign_Sms {

	public const OPTION_TEMPLATES = 'ebi_campaign_sms_templates';
	public const OPTION_LOG       = 'ebi_campaign_sms_log';
	public const OPTION_SETTINGS  = 'ebi_campaign_sms_settings';
	public const PREVIEW_KEY      = 'ebi_campaign_sms_preview_';
	public const MAX_RECIPIENTS   = 500;
	public const MAX_LOG          = 500;

	/**
	 * تنظیمات اتصال به ملی‌پیامک (بدون رمز؛ رمز در گزینهٔ محرمانه است).
	 *
	 * @return array<string,mixed>
	 */
	public static function settings(): array {
		$stored = get_option(self::OPTION_SETTINGS, array());
		$stored = is_array($stored) ? $stored : array();
		return array_merge(
			array(
				'username' => '',
				'sender'   => '',
				'api_base' => 'https://rest.payamak-panel.com',
			),
			$stored
		);
	}

	/**
	 * پاک‌سازی تنظیمات اتصال.
	 *
	 * @param array<string,mixed> $input ورودی.
	 * @return array<string,mixed>
	 */
	public static function sanitize_settings(array $input): array {
		$base = isset($input['api_base']) ? esc_url_raw(trim((string) $input['api_base'])) : 'https://rest.payamak-panel.com';
		return array(
			'username' => isset($input['username']) ? sanitize_text_field((string) $input['username']) : '',
			'sender'   => isset($input['sender']) ? preg_replace('/[^0-9]/', '', (string) $input['sender']) : '',
			'api_base' => untrailingslashit($base) === '' ? 'https://rest.payamak-panel.com' : untrailingslashit($base),
		);
	}

	/**
	 * ذخیرهٔ تنظیمات اتصال.
	 *
	 * @param array<string,mixed> $input ورودی.
	 * @return array<string,mixed>
	 */
	public static function save_settings(array $input): array {
		$clean = self::sanitize_settings($input);
		update_option(self::OPTION_SETTINGS, $clean, false);
		return $clean;
	}

	/**
	 * متغیرهای مجاز قالب.
	 *
	 * @return array<string,string> کلید متغیر به برچسب فارسی.
	 */
	public static function variables(): array {
		return array(
			'{name}'         => 'نام مشتری',
			'{mobile}'       => 'شمارهٔ موبایل گیرنده',
			'{round}'        => 'شمارهٔ دوره',
			'{points}'       => 'امتیاز فاکتور',
			'{invoice_count}' => 'تعداد فاکتور',
			'{prize}'        => 'مبلغ جایزه',
			'{date}'         => 'تاریخ اعلام نتیجه',
			'{store}'        => 'نام فروشگاه',
			'{support}'      => 'راه ارتباطی پشتیبانی',
		);
	}

	/**
	 * الگوی قالب، با جای‌گذار شناسه و تاریخ.
	 *
	 * @return array<string,mixed>
	 */
	public static function default_template(): array {
		return array(
			'id'      => 0,
			'name'    => '',
			'kind'    => 'general',
			'body'    => '',
			'updated' => '',
		);
	}

	/**
	 * همهٔ قالب‌ها.
	 *
	 * @return array<int,array<string,mixed>>
	 */
	public static function templates(): array {
		$stored = get_option(self::OPTION_TEMPLATES, array());
		$stored = is_array($stored) ? $stored : array();
		$list   = array();
		foreach ($stored as $item) {
			if (!is_array($item) || !isset($item['id'])) {
				continue;
			}
			$list[] = array_merge(self::default_template(), $item);
		}
		usort($list, static fn( $a, $b ) => (int) $a['id'] <=> (int) $b['id']);
		return $list;
	}

	/**
	 * یک قالب.
	 *
	 * @param int $id شناسه.
	 * @return array<string,mixed>|null
	 */
	public static function template(int $id): ?array {
		foreach (self::templates() as $template) {
			if ((int) $template['id'] === $id) {
				return $template;
			}
		}
		return null;
	}

	/**
	 * ذخیرهٔ قالب (ایجاد یا ویرایش).
	 *
	 * @param array<string,mixed> $input ورودی فرم.
	 * @return int|WP_Error شناسهٔ قالب.
	 */
	public static function save_template(array $input) {
		$id   = isset($input['id']) ? (int) $input['id'] : 0;
		$name = isset($input['name']) ? sanitize_text_field((string) $input['name']) : '';
		$kind = isset($input['kind']) && 'winner' === $input['kind'] ? 'winner' : 'general';
		$body = isset($input['body']) ? sanitize_textarea_field((string) $input['body']) : '';

		if ($name === '') {
			return new WP_Error('ebi_sms_name', __('نام قالب الزامی است.', 'ebi-campaign'));
		}
		if (mb_strlen($body) < 5) {
			return new WP_Error('ebi_sms_body', __('متن قالب باید حداقل ۵ نویسه باشد.', 'ebi-campaign'));
		}
		if (mb_strlen($body) > 500) {
			return new WP_Error('ebi_sms_body_long', __('متن قالب نباید بیش از ۵۰۰ نویسه باشد.', 'ebi-campaign'));
		}
		$unknown = self::unknown_placeholders($body);
		if (!empty($unknown)) {
			return new WP_Error('ebi_sms_placeholder', sprintf(
				/* translators: %s: فهرست متغیرهای ناشناخته. */
				__('متغیر ناشناخته در قالب: %s', 'ebi-campaign'),
				implode(', ', $unknown)
			));
		}

		$list = self::templates();
		if ($id > 0) {
			$found = false;
			foreach ($list as $index => $template) {
				if ((int) $template['id'] === $id) {
					$list[ $index ]['name']    = $name;
					$list[ $index ]['kind']    = $kind;
					$list[ $index ]['body']    = $body;
					$list[ $index ]['updated'] = current_time('mysql');
					$found = true;
					break;
				}
			}
			if (!$found) {
				return new WP_Error('ebi_sms_missing', __('قالب موردنظر پیدا نشد.', 'ebi-campaign'));
			}
		} else {
			$ids     = array_map(static fn( $t ) => (int) $t['id'], $list);
			$id      = empty($ids) ? 1 : max($ids) + 1;
			$list[]  = array(
				'id'      => $id,
				'name'    => $name,
				'kind'    => $kind,
				'body'    => $body,
				'updated' => current_time('mysql'),
			);
		}

		update_option(self::OPTION_TEMPLATES, $list, false);
		return $id;
	}

	/**
	 * حذف قالب.
	 *
	 * @param int $id شناسه.
	 * @return bool
	 */
	public static function delete_template(int $id): bool {
		$list = self::templates();
		$kept = array_values(array_filter($list, static fn( $t ) => (int) $t['id'] !== $id));
		if (count($kept) === count($list)) {
			return false;
		}
		update_option(self::OPTION_TEMPLATES, $kept, false);
		return true;
	}

	/**
	 * متغیرهای ناشناخته در متن.
	 *
	 * @param string $body متن.
	 * @return array<int,string>
	 */
	public static function unknown_placeholders(string $body): array {
		if (!preg_match_all('/\{[a-z_]+\}/i', $body, $matches)) {
			return array();
		}
		$allowed = array_keys(self::variables());
		$unknown = array();
		foreach ($matches[0] as $token) {
			if (!in_array($token, $allowed, true)) {
				$unknown[] = $token;
			}
		}
		return array_values(array_unique($unknown));
	}

	/**
	 * جایگزینی متغیرها؛ متغیر نامعتبر یا مقدار ضروری خالی خطا می‌دهد.
	 *
	 * @param string              $body متن قالب.
	 * @param array<string,mixed> $ctx  مقادیر.
	 * @return string|WP_Error
	 */
	public static function render(string $body, array $ctx) {
		$unknown = self::unknown_placeholders($body);
		if (!empty($unknown)) {
			return new WP_Error('ebi_sms_placeholder', sprintf(
				/* translators: %s: متغیرها. */
				__('متغیر ناشناخته در قالب: %s', 'ebi-campaign'),
				implode(', ', $unknown)
			));
		}

		$out    = $body;
		$tokens = array();
		if (preg_match_all('/\{[a-z_]+\}/i', $body, $matches)) {
			$tokens = array_values(array_unique($matches[0]));
		}

		// فقط متغیرهایی که واقعاً در متن هستند باید مقدار داشته باشند.
		foreach ($tokens as $token) {
			$key   = trim($token, '{}');
			$value = isset($ctx[ $key ]) ? $ctx[ $key ] : null;
			$value = is_scalar($value) ? trim((string) $value) : '';
			if ($value === '') {
				return new WP_Error('ebi_sms_empty_value', sprintf(
					/* translators: %s: نام متغیر. */
					__('مقدار متغیر %s برای این گیرنده خالی است.', 'ebi-campaign'),
					$token
				));
			}
			$out = str_replace($token, $value, $out);
		}

		if (preg_match('/\{[a-z_]+\}/i', $out)) {
			return new WP_Error('ebi_sms_placeholder_left', __('پس از جایگزینی، متغیر پرنشده در متن باقی مانده است.', 'ebi-campaign'));
		}
		return $out;
	}

	/**
	 * گیرندگان مجاز: برندگان منتشرشدهٔ یک دوره (از سرویس مرکزی).
	 *
	 * @param int $round_no شمارهٔ دوره.
	 * @return array<int,array<string,mixed>>|WP_Error
	 */
	public static function winners_of_round(int $round_no) {
		if ($round_no < 1) {
			return new WP_Error('ebi_sms_round', __('شمارهٔ دوره معتبر نیست.', 'ebi-campaign'));
		}

		// نام نمایشی و جایزه از خروجی عمومی می‌آید؛ شمارهٔ کامل فقط از مسیر مدیریتی
		// حریم خصوصی (با ثبت حسابرسی در سرویس مرکزی) خوانده می‌شود.
		$card = Ebi_Campaign_Api::winner($round_no);
		$name = '';
		$prize = (string) Ebi_Campaign_Settings::get('prize_label', '');
		$drawn = '';
		if (!is_wp_error($card)) {
			$name  = isset($card['displayName']) ? (string) $card['displayName'] : '';
			$prize = isset($card['prizeToman']) && (int) $card['prizeToman'] > 0
				? number_format_i18n((int) $card['prizeToman']) : $prize;
			$drawn = isset($card['drawnAt']) ? (string) $card['drawnAt'] : '';
		}

		$runs = Ebi_Campaign_Api::runs();
		if (is_wp_error($runs)) {
			return $runs;
		}

		$run_id = 0;
		foreach ((array) $runs as $run) {
			if (!is_array($run)) {
				continue;
			}
			$status = isset($run['status']) ? (string) $run['status'] : '';
			if ((int) ($run['roundNo'] ?? 0) === $round_no && in_array($status, array('Finalized', 'Selected'), true)) {
				$run_id = (int) ($run['runId'] ?? 0);
				break;
			}
		}
		if ($run_id < 1) {
			return new WP_Error('ebi_sms_no_run', __('برای این دوره قرعه‌کشی نهایی‌شده‌ای پیدا نشد.', 'ebi-campaign'));
		}

		$contact = Ebi_Campaign_Api::winner_contact($run_id);
		if (is_wp_error($contact)) {
			return $contact;
		}
		$mobile = isset($contact['winnerMobile']) ? preg_replace('/[^0-9]/', '', (string) $contact['winnerMobile']) : '';
		if (!preg_match('/^09\d{9}$/', (string) $mobile)) {
			return new WP_Error('ebi_sms_no_mobile', __('شمارهٔ کامل برنده در دسترس نیست.', 'ebi-campaign'));
		}

		return array(
			array(
				'mobile' => $mobile,
				'name'   => $name,
				'round'  => (string) $round_no,
				'prize'  => $prize,
				'date'   => $drawn,
				'masked' => false,
			),
		);
	}

	/**
	 * گیرندهٔ آزمایشی دستی.
	 *
	 * @param string $mobile شماره.
	 * @return array<string,mixed>|WP_Error
	 */
	public static function test_recipient(string $mobile) {
		$digits = preg_replace('/[^0-9]/', '', $mobile);
		if (strlen($digits) === 10 && '9' === $digits[0]) {
			$digits = '0' . $digits;
		}
		if (strlen($digits) !== 11 || 0 !== strpos($digits, '09')) {
			return new WP_Error('ebi_sms_mobile', __('شمارهٔ موبایل آزمایشی معتبر نیست.', 'ebi-campaign'));
		}
		return array(
			'mobile' => $digits,
			'name'   => 'آزمون',
			'round'  => (string) Ebi_Campaign_Settings::get('campaign_slug', ''),
			'prize'  => (string) Ebi_Campaign_Settings::get('prize_label', ''),
			'date'   => '',
			'masked' => false,
		);
	}

	/**
	 * پیش‌نمایش متن نهایی برای یک گیرنده.
	 *
	 * @param string              $body متن.
	 * @param array<string,mixed> $ctx  مقادیر.
	 * @return string|WP_Error
	 */
	public static function preview(string $body, array $ctx) {
		return self::render($body, self::fill_defaults($ctx));
	}

	/**
	 * تکمیل مقادیر ثابت کمپین.
	 *
	 * @param array<string,mixed> $ctx مقادیر.
	 * @return array<string,mixed>
	 */
	public static function fill_defaults(array $ctx): array {
		$ctx += array(
			'store'   => get_bloginfo('name'),
			'support' => (string) Ebi_Campaign_Settings::get('support_text', ''),
			'round'   => (string) Ebi_Campaign_Settings::get('campaign_slug', ''),
			'prize'   => (string) Ebi_Campaign_Settings::get('prize_label', ''),
			'date'    => current_time('mysql'),
			'name'    => '',
			'points'  => '0',
			'invoice_count' => '1',
		);
		return $ctx;
	}

	/**
	 * ارسال دستی یک دسته؛ هیچ فراخوانی از رویدادها به این تابع وجود ندارد.
	 *
	 * @param int                 $template_id شناسهٔ قالب.
	 * @param array<int,array<string,mixed>> $recipients گیرندگان.
	 * @param string              $actor       کاربر اقدام‌کننده.
	 * @param bool                $confirmed   تأیید صریح.
	 * @return array<string,mixed>|WP_Error
	 */
	public static function send_manual(int $template_id, array $recipients, string $actor, bool $confirmed) {
		if (!current_user_can('ebi_send_sms')) {
			return new WP_Error('ebi_sms_forbidden', __('برای ارسال پیامک دسترسی ندارید.', 'ebi-campaign'));
		}
		if (!$confirmed) {
			return new WP_Error('ebi_sms_confirm', __('ارسال پیامک نیازمند تأیید صریح متن و تعداد گیرندگان است.', 'ebi-campaign'));
		}
		$template = self::template($template_id);
		if ($template === null) {
			return new WP_Error('ebi_sms_template', __('قالب پیامک پیدا نشد.', 'ebi-campaign'));
		}
		if (empty($recipients)) {
			return new WP_Error('ebi_sms_recipients', __('گیرنده‌ای برای ارسال انتخاب نشده است.', 'ebi-campaign'));
		}
		if (count($recipients) > self::MAX_RECIPIENTS) {
			return new WP_Error('ebi_sms_many', sprintf(
				/* translators: %s: سقف تعداد گیرندگان. */
				__('تعداد گیرندگان بیش از سقف مجاز (%s) است.', 'ebi-campaign'),
				number_format_i18n(self::MAX_RECIPIENTS)
			));
		}

		$settings = self::settings();
		if ($settings['username'] === '' || $settings['sender'] === '' || Ebi_Campaign_Settings::secret('sms.send') === '') {
			return new WP_Error('ebi_sms_config', __('تنظیمات ملی‌پیامک (نام کاربری، شمارهٔ فرستنده یا رمز) کامل نیست.', 'ebi-campaign'));
		}

		// همهٔ متن‌ها پیش از اولین ارسال ساخته و اعتبارسنجی می‌شوند.
		$prepared = array();
		foreach ($recipients as $recipient) {
			$mobile = isset($recipient['mobile']) ? (string) $recipient['mobile'] : '';
			if (!empty($recipient['masked']) || !preg_match('/^09\d{9}$/', $mobile)) {
				return new WP_Error('ebi_sms_mobile', __('شمارهٔ کامل گیرنده در دسترس نیست؛ ارسال به این فهرست مجاز نیست.', 'ebi-campaign'));
			}
			$text = self::render($template['body'], self::fill_defaults($recipient));
			if (is_wp_error($text)) {
				return $text;
			}
			$prepared[] = array(
				'mobile' => $mobile,
				'text'   => $text,
			);
		}

		$summary = array('sent' => 0, 'failed' => 0, 'unknown' => 0);
		foreach ($prepared as $item) {
			$result = self::send_one($item['mobile'], $item['text'], $actor, (string) $template['name']);
			if (isset($summary[ $result['status'] ])) {
				$summary[ $result['status'] ]++;
			}
		}

		return $summary + array('total' => count($prepared), 'template' => $template['name']);
	}

	/**
	 * ارسال یک پیام با ثبت پیش از فراخوانی (تا Timeout گم نشود).
	 *
	 * @param string $mobile شماره.
	 * @param string $text   متن نهایی.
	 * @param string $actor  اقدام‌کننده.
	 * @param string $template نام قالب.
	 * @return array<string,mixed>
	 */
	public static function send_one(string $mobile, string $text, string $actor, string $template = ''): array {
		$request_id = wp_generate_uuid4();
		$entry_id   = self::add_log(array(
			'at'        => current_time('mysql'),
			'actor'     => $actor,
			'template'  => $template,
			'to'        => self::mask($mobile),
			'status'    => 'pending',
			'provider'  => 'melipayamak',
			'rec_id'    => '',
			'code'      => '',
			'message'   => __('در حال ارسال', 'ebi-campaign'),
			'requestId' => $request_id,
		));

		$transport = self::transport();
		$response  = call_user_func($transport, array(
			'to'         => $mobile,
			'text'       => $text,
			'requestId'  => $request_id,
			'timeout'    => 15,
		));

		$status  = isset($response['status']) ? (string) $response['status'] : 'unknown';
		$allowed = array('sent', 'failed', 'unknown');
		if (!in_array($status, $allowed, true)) {
			$status = 'unknown';
		}

		self::update_log($entry_id, array(
			'status'  => $status,
			'rec_id'  => isset($response['recId']) ? sanitize_text_field((string) $response['recId']) : '',
			'code'    => isset($response['code']) ? sanitize_text_field((string) $response['code']) : '',
			'message' => isset($response['message']) ? sanitize_text_field((string) $response['message']) : '',
		));

		return array('status' => $status, 'requestId' => $request_id);
	}

	/**
	 * انتقال‌دهندهٔ پیامک؛ در آزمون با فیلتر جایگزین می‌شود.
	 *
	 * @return callable
	 */
	public static function transport(): callable {
		$custom = apply_filters('ebi_campaign_sms_transport', null);
		if (is_callable($custom)) {
			return $custom;
		}
		return array(__CLASS__, 'http_transport');
	}

	/**
	 * فراخوانی واقعی REST ملی‌پیامک.
	 *
	 * @param array<string,mixed> $args آرگومان‌ها.
	 * @return array<string,mixed> وضعیت یکسان‌شده.
	 */
	public static function http_transport(array $args): array {
		$settings = self::settings();
		$endpoint = $settings['api_base'] . '/api/SendSMS/SendSMS';

		$response = wp_remote_post(
			$endpoint,
			array(
				'timeout' => isset($args['timeout']) ? (int) $args['timeout'] : 15,
				'headers' => array('Content-Type' => 'application/x-www-form-urlencoded'),
				'body'    => array(
					'username' => $settings['username'],
					'password' => Ebi_Campaign_Settings::secret('sms.send'),
					'to'       => (string) $args['to'],
					'from'     => $settings['sender'],
					'text'     => (string) $args['text'],
					'isFlash'  => 'false',
				),
			)
		);

		if (is_wp_error($response)) {
			// Timeout یا قطع ارتباط یعنی ممکن است سرویس درخواست را پذیرفته باشد.
			return array(
				'status'  => 'unknown',
				'code'    => $response->get_error_code(),
				'message' => __('پاسخ سرویس پیامک دریافت نشد؛ وضعیت نیازمند بررسی است.', 'ebi-campaign'),
			);
		}

		$code = (int) wp_remote_retrieve_response_code($response);
		$body = json_decode((string) wp_remote_retrieve_body($response), true);
		if (!is_array($body)) {
			return array('status' => 'unknown', 'code' => 'http_' . $code, 'message' => __('پاسخ سرویس پیامک قابل خواندن نیست.', 'ebi-campaign'));
		}

		$ret   = isset($body['RetStatus']) ? (int) $body['RetStatus'] : null;
		$value = isset($body['Value']) ? (string) $body['Value'] : '';
		$text  = isset($body['StrRetStatus']) ? (string) $body['StrRetStatus'] : '';

		if ($code >= 200 && $code < 300 && 1 === $ret && $value !== '') {
			return array('status' => 'sent', 'recId' => $value, 'code' => (string) $ret, 'message' => $text);
		}
		if (null === $ret) {
			return array('status' => 'unknown', 'code' => 'http_' . $code, 'message' => __('پاسخ سرویس پیامک مبهم است.', 'ebi-campaign'));
		}
		return array('status' => 'failed', 'code' => (string) $ret, 'message' => $text);
	}

	/**
	 * ماسک شماره برای گزارش.
	 *
	 * @param string $mobile شماره.
	 * @return string
	 */
	public static function mask(string $mobile): string {
		$mobile = preg_replace('/[^0-9]/', '', $mobile);
		if (strlen($mobile) < 7) {
			return str_repeat('*', strlen($mobile));
		}
		return substr($mobile, 0, 4) . '***' . substr($mobile, -4);
	}

	/**
	 * افزودن رکورد گزارش.
	 *
	 * @param array<string,mixed> $entry رکورد.
	 * @return string شناسهٔ رکورد.
	 */
	private static function add_log(array $entry): string {
		$entry['id'] = wp_generate_uuid4();
		$log         = get_option(self::OPTION_LOG, array());
		$log         = is_array($log) ? $log : array();
		array_unshift($log, $entry);
		$log = array_slice($log, 0, self::MAX_LOG);
		update_option(self::OPTION_LOG, $log, false);
		return (string) $entry['id'];
	}

	/**
	 * به‌روزرسانی رکورد گزارش.
	 *
	 * @param string              $id    شناسهٔ رکورد.
	 * @param array<string,mixed> $patch تغییرات.
	 */
	private static function update_log(string $id, array $patch): void {
		$log = get_option(self::OPTION_LOG, array());
		if (!is_array($log)) {
			return;
		}
		foreach ($log as $index => $entry) {
			if (isset($entry['id']) && $entry['id'] === $id) {
				$log[ $index ] = array_merge($entry, $patch);
				break;
			}
		}
		update_option(self::OPTION_LOG, $log, false);
	}

	/**
	 * گزارش ارسال‌ها با فیلتر.
	 *
	 * @param array<string,mixed> $filter فیلتر status/search/from/to.
	 * @param int                 $limit  تعداد.
	 * @return array<int,array<string,mixed>>
	 */
	public static function log_entries(array $filter = array(), int $limit = 100): array {
		$log = get_option(self::OPTION_LOG, array());
		$log = is_array($log) ? $log : array();

		$status = isset($filter['status']) ? (string) $filter['status'] : '';
		$search = isset($filter['search']) ? trim((string) $filter['search']) : '';
		$from   = isset($filter['from']) ? (string) $filter['from'] : '';
		$to     = isset($filter['to']) ? (string) $filter['to'] : '';

		$out = array();
		foreach ($log as $entry) {
			if (!is_array($entry)) {
				continue;
			}
			if ($status !== '' && ($entry['status'] ?? '') !== $status) {
				continue;
			}
			if ($from !== '' && strcmp((string) ($entry['at'] ?? ''), $from) < 0) {
				continue;
			}
			if ($to !== '' && strcmp((string) ($entry['at'] ?? ''), $to . ' 23:59:59') > 0) {
				continue;
			}
			if ($search !== '') {
				$hay = ($entry['to'] ?? '') . ' ' . ($entry['template'] ?? '') . ' ' . ($entry['rec_id'] ?? '') . ' ' . ($entry['actor'] ?? '');
				if (false === stripos($hay, $search)) {
					continue;
				}
			}
			$out[] = $entry;
			if (count($out) >= $limit) {
				break;
			}
		}
		return $out;
	}

	/**
	 * شمارش گزارش‌ها بر اساس وضعیت.
	 *
	 * @return array<string,int>
	 */
	public static function log_summary(): array {
		$summary = array('sent' => 0, 'failed' => 0, 'unknown' => 0, 'pending' => 0);
		$log     = get_option(self::OPTION_LOG, array());
		if (!is_array($log)) {
			return $summary;
		}
		foreach ($log as $entry) {
			$status = is_array($entry) ? (string) ($entry['status'] ?? '') : '';
			if (isset($summary[ $status ])) {
				$summary[ $status ]++;
			}
		}
		return $summary;
	}
}
