<?php
/**
 * پنل مدیریت کمپین در وردپرس.
 *
 * هر عملیات با سه کنترل انجام می‌شود: مجوز سرور‌محور (current_user_can)،
 * نونس CSRF و کلید نقش متناظر در سرویس مرکزی. پنهان‌بودن دکمه هیچ‌گاه
 * جایگزین کنترل سرور نیست.
 *
 * @package EbiCampaign
 */

defined('ABSPATH') || exit;

/**
 * پنل و عملیات مدیریتی.
 */
final class Ebi_Campaign_Admin {

	public const MENU_SLUG   = 'ebi-campaign';
	public const ACTION      = 'ebi_campaign_action';
	public const NOTICE_KEY  = 'ebi_campaign_notice_';

	/**
	 * نگاشت عملیات به صفحهٔ بازگشت و مجوز لازم.
	 *
	 * @return array<string,array{page:string,cap:string,confirm:bool}>
	 */
	public static function operations(): array {
		return array(
			'start_round'    => array('page' => 'ebi-campaign-rounds', 'cap' => 'ebi_manage_campaign', 'confirm' => true),
			'close_round'    => array('page' => 'ebi-campaign-rounds', 'cap' => 'ebi_manage_campaign', 'confirm' => true),
			'assign_held'    => array('page' => 'ebi-campaign-held', 'cap' => 'ebi_manage_campaign', 'confirm' => true),
			'decision'       => array('page' => 'ebi-campaign-reviews', 'cap' => 'ebi_review_invoice', 'confirm' => true),
			'prepare_lottery' => array('page' => 'ebi-campaign-lottery', 'cap' => 'ebi_run_lottery', 'confirm' => true),
			'select_winner'  => array('page' => 'ebi-campaign-lottery', 'cap' => 'ebi_run_lottery', 'confirm' => true),
			'finalize_run'   => array('page' => 'ebi-campaign-lottery', 'cap' => 'ebi_run_lottery', 'confirm' => true),
			'abort_run'      => array('page' => 'ebi-campaign-lottery', 'cap' => 'ebi_run_lottery', 'confirm' => true),
			'publish_result' => array('page' => 'ebi-campaign-winners', 'cap' => 'ebi_publish_result', 'confirm' => true),
			'unpublish_result' => array('page' => 'ebi-campaign-winners', 'cap' => 'ebi_publish_result', 'confirm' => false),
			'upload_image'   => array('page' => 'ebi-campaign-winners', 'cap' => 'ebi_publish_result', 'confirm' => true),
			'withdraw_image' => array('page' => 'ebi-campaign-winners', 'cap' => 'ebi_publish_result', 'confirm' => false),
			'refresh_cache'  => array('page' => 'ebi-campaign', 'cap' => 'ebi_view_campaign', 'confirm' => false),
			'save_settings'  => array('page' => 'ebi-campaign-settings', 'cap' => 'manage_options', 'confirm' => false),
		);
	}

	/**
	 * راه‌اندازی پنل.
	 */
	public static function init(): void {
		add_action('admin_menu', array(__CLASS__, 'menu'));
		add_action('admin_post_' . self::ACTION, array(__CLASS__, 'handle_action'));
	}

	/**
	 * ساخت منو و زیرمنوها.
	 */
	public static function menu(): void {
		add_menu_page(
			__('کمپین ابی', 'ebi-campaign'),
			__('کمپین ابی', 'ebi-campaign'),
			'ebi_view_campaign',
			self::MENU_SLUG,
			array(__CLASS__, 'page_status'),
			'dashicons-awards',
			56
		);

		$pages = array(
			'ebi-campaign-rounds'   => array(__('دوره‌ها', 'ebi-campaign'), 'page_rounds'),
			'ebi-campaign-reviews'  => array(__('نیازمند بررسی', 'ebi-campaign'), 'page_reviews'),
			'ebi-campaign-held'     => array(__('فاکتورهای انتظار', 'ebi-campaign'), 'page_held'),
			'ebi-campaign-lottery'  => array(__('قرعه‌کشی', 'ebi-campaign'), 'page_lottery'),
			'ebi-campaign-winners'  => array(__('برندگان و تصاویر', 'ebi-campaign'), 'page_winners'),
			'ebi-campaign-settings' => array(__('تنظیمات', 'ebi-campaign'), 'page_settings'),
		);
		foreach ($pages as $slug => $config) {
			add_submenu_page(self::MENU_SLUG, $config[0], $config[0], 'ebi_view_campaign', $slug, array(__CLASS__, $config[1]));
		}
	}

	// ------------------------------------------------------------------
	//  عملیات
	// ------------------------------------------------------------------

	/**
	 * اجرای عملیات فرم‌ها.
	 */
	public static function handle_action(): void {
		$ops = self::operations();
		$op  = isset($_POST['ebi_op']) ? sanitize_key(wp_unslash((string) $_POST['ebi_op'])) : '';

		if (!isset($ops[ $op ])) {
			self::notice('error', __('درخواست ناشناخته است.', 'ebi-campaign'));
			self::redirect(self::MENU_SLUG);
		}

		check_admin_referer(self::ACTION);

		$rule = $ops[ $op ];
		if (!current_user_can($rule['cap'])) {
			self::notice('error', __('برای این عملیات دسترسی ندارید. کنترل مجوز در سرویس مرکزی هم اعمال می‌شود.', 'ebi-campaign'));
			self::redirect($rule['page']);
		}
		if ($rule['confirm'] && empty($_POST['confirm'])) {
			self::notice('error', __('این عملیات نیازمند تأیید صریح است. کادر تأیید را علامت بزنید.', 'ebi-campaign'));
			self::redirect($rule['page']);
		}

		$result = self::run_operation($op);
		if (is_wp_error($result)) {
			self::notice('error', $result->get_error_message());
		} else {
			self::notice('success', (string) $result);
		}
		self::redirect($rule['page']);
	}

	/**
	 * اجرای واقعی هر عملیات روی سرویس مرکزی.
	 *
	 * @param string $op نام عملیات.
	 * @return string|WP_Error پیام موفقیت یا خطا.
	 */
	private static function run_operation(string $op) {
		$actor = wp_get_current_user()->user_login;

		switch ($op) {
			case 'start_round':
				$data = Ebi_Campaign_Api::admin_request('campaign.manage', 'POST', 'rounds/start', array('actor' => $actor));
				if (is_wp_error($data)) {
					return $data;
				}
				self::remember_round_start((array) $data, $actor);
				return sprintf(
					/* translators: %s: شمارهٔ دوره. */
					__('دورهٔ %s آغاز شد.', 'ebi-campaign'),
					number_format_i18n((int) ($data['roundNo'] ?? 0))
				);

			case 'close_round':
				$round_id = isset($_POST['round_id']) ? (int) $_POST['round_id'] : 0;
				$data     = Ebi_Campaign_Api::admin_request('campaign.manage', 'POST', 'rounds/' . $round_id . '/close', array('actor' => $actor));
				if (is_wp_error($data)) {
					return $data;
				}
				return __('دوره بسته شد.', 'ebi-campaign');

			case 'assign_held':
				$round_id = isset($_POST['round_id']) ? (int) $_POST['round_id'] : 0;
				$limit    = isset($_POST['limit']) ? max(1, min(1000, (int) $_POST['limit'])) : 1000;
				$data     = Ebi_Campaign_Api::admin_request(
					'campaign.manage',
					'POST',
					'held/assign',
					array('roundId' => $round_id, 'limit' => $limit, 'actor' => $actor)
				);
				if (is_wp_error($data)) {
					return $data;
				}
				return sprintf(
					/* translators: %s: تعداد فاکتور تخصیص‌داده‌شده. */
					__('%s فاکتور انتظار به دورهٔ انتخابی منتقل شد.', 'ebi-campaign'),
					number_format_i18n((int) ($data['assigned'] ?? 0))
				);

			case 'decision':
				$invoice_id = isset($_POST['invoice_id']) ? (int) $_POST['invoice_id'] : 0;
				$verdict    = isset($_POST['verdict']) ? sanitize_key(wp_unslash((string) $_POST['verdict'])) : '';
				$reason     = isset($_POST['reason']) ? sanitize_text_field(wp_unslash((string) $_POST['reason'])) : '';
				$force      = !empty($_POST['force']);
				$data       = Ebi_Campaign_Api::admin_request(
					'review.decide',
					'POST',
					'invoices/' . $invoice_id . '/decision',
					array(
						'verdict'  => $verdict,
						'reason'   => $reason,
						'actor'    => $actor,
						'force'    => $force,
					)
				);
				if (is_wp_error($data)) {
					return $data;
				}
				return isset($data['message']) && $data['message'] !== ''
					? (string) $data['message']
					: __('تصمیم ثبت شد.', 'ebi-campaign');

			case 'prepare_lottery':
				$round_id = isset($_POST['round_id']) ? (int) $_POST['round_id'] : 0;
				$ack      = !empty($_POST['acknowledge_reviews']);
				$data     = Ebi_Campaign_Api::admin_request(
					'lottery.run',
					'POST',
					'rounds/' . $round_id . '/lottery',
					array('actor' => $actor, 'acknowledgeReviews' => $ack)
				);
				if (is_wp_error($data)) {
					return $data;
				}
				return sprintf(
					/* translators: ۱: شناسهٔ فراگرفت، ۲: وضعیت. */
					__('مجموعهٔ ورودی‌ها تثبیت شد (فراگرفت %1$s، وضعیت %2$s).', 'ebi-campaign'),
					number_format_i18n((int) ($data['runId'] ?? 0)),
					(string) ($data['status'] ?? '-')
				);

			case 'select_winner':
				$run_id = isset($_POST['run_id']) ? (int) $_POST['run_id'] : 0;
				$data   = Ebi_Campaign_Api::admin_request('lottery.run', 'POST', 'lottery/' . $run_id . '/select', array('actor' => $actor));
				if (is_wp_error($data)) {
					return $data;
				}
				return __('برنده انتخاب شد. اکنون نتیجه را نهایی و سپس منتشر کنید.', 'ebi-campaign');

			case 'finalize_run':
				$run_id = isset($_POST['run_id']) ? (int) $_POST['run_id'] : 0;
				$data   = Ebi_Campaign_Api::admin_request('lottery.run', 'POST', 'lottery/' . $run_id . '/finalize', array('actor' => $actor));
				if (is_wp_error($data)) {
					return $data;
				}
				return __('نتیجهٔ دوره نهایی شد.', 'ebi-campaign');

			case 'abort_run':
				$run_id = isset($_POST['run_id']) ? (int) $_POST['run_id'] : 0;
				$reason = isset($_POST['reason']) ? sanitize_text_field(wp_unslash((string) $_POST['reason'])) : '';
				$data   = Ebi_Campaign_Api::admin_request(
					'lottery.run',
					'POST',
					'lottery/' . $run_id . '/abort',
					array('actor' => $actor, 'reason' => $reason)
				);
				if (is_wp_error($data)) {
					return $data;
				}
				return __('فراگرفت قرعه‌کشی لغو شد؛ تلاش جدید پس از رفع علت مجاز است.', 'ebi-campaign');

			case 'publish_result':
				$run_id   = isset($_POST['run_id']) ? (int) $_POST['run_id'] : 0;
				$name     = isset($_POST['display_name']) ? sanitize_text_field(wp_unslash((string) $_POST['display_name'])) : '';
				$data     = Ebi_Campaign_Api::admin_request(
					'lottery.publish',
					'POST',
					'lottery/' . $run_id . '/publish',
					array(
						'actor'       => $actor,
						'confirm'     => true,
						'displayName' => $name,
					)
				);
				if (is_wp_error($data)) {
					return $data;
				}
				return __('نتیجه برای نمایش عمومی منتشر شد.', 'ebi-campaign');

			case 'unpublish_result':
				$run_id = isset($_POST['run_id']) ? (int) $_POST['run_id'] : 0;
				$reason = isset($_POST['reason']) ? sanitize_text_field(wp_unslash((string) $_POST['reason'])) : '';
				$data   = Ebi_Campaign_Api::admin_request(
					'lottery.publish',
					'POST',
					'lottery/' . $run_id . '/unpublish',
					array('actor' => $actor, 'reason' => $reason)
				);
				if (is_wp_error($data)) {
					return $data;
				}
				return __('انتشار عمومی نتیجه لغو شد.', 'ebi-campaign');

			case 'upload_image':
				$run_id = isset($_POST['run_id']) ? (int) $_POST['run_id'] : 0;
				$file   = isset($_FILES['winner_image']) && is_array($_FILES['winner_image'])
					? wp_unslash($_FILES['winner_image']) // phpcs:ignore WordPress.Security.ValidatedSanitizedInput.InputNotSanitized
					: array();
				$result = Ebi_Campaign_Images::upload_and_publish($file, $run_id, $actor);
				if (is_wp_error($result)) {
					return $result;
				}
				return __('تصویر برنده بارگذاری و با تأیید رضایت منتشر شد.', 'ebi-campaign');

			case 'withdraw_image':
				$run_id = isset($_POST['run_id']) ? (int) $_POST['run_id'] : 0;
				$result = Ebi_Campaign_Images::withdraw($run_id, $actor);
				if (is_wp_error($result)) {
					return $result;
				}
				return __('تصویر از خروجی عمومی برداشته شد.', 'ebi-campaign');

			case 'refresh_cache':
				Ebi_Campaign_Api::flush_cache();
				return __('کش اطلاعات کمپین تازه شد.', 'ebi-campaign');

			case 'save_settings':
				$input = isset($_POST['ebi_settings']) && is_array($_POST['ebi_settings'])
					? wp_unslash($_POST['ebi_settings']) // phpcs:ignore WordPress.Security.ValidatedSanitizedInput.InputNotSanitized
					: array();
				Ebi_Campaign_Settings::save(is_array($input) ? $input : array());

				$keys = isset($_POST['ebi_keys']) && is_array($_POST['ebi_keys']) ? wp_unslash($_POST['ebi_keys']) : array();
				if (is_array($keys)) {
					foreach ($keys as $role => $value) {
						$role  = sanitize_text_field((string) $role);
						$value = is_string($value) ? trim($value) : '';
						if ($value !== '') {
							Ebi_Campaign_Settings::store_secret($role, $value);
						}
					}
				}
				return __('تنظیمات ذخیره شد.', 'ebi-campaign');
		}

		return new WP_Error('ebi_unknown_op', __('عملیات پشتیبانی نمی‌شود.', 'ebi-campaign'));
	}

	/**
	 * ثبت شروع دوره برای پیگیری سیاست فاکتورهای انتظار (بند ۳.۳).
	 *
	 * @param array<string,mixed> $round پاسخ سرور.
	 * @param string              $actor کاربر اقدام‌کننده.
	 */
	private static function remember_round_start(array $round, string $actor): void {
		$log   = get_option('ebi_campaign_round_log', array());
		$log   = is_array($log) ? $log : array();
		$log[] = array(
			'round_no' => (int) ($round['roundNo'] ?? 0),
			'actor'    => sanitize_text_field($actor),
			'at'       => current_time('mysql'),
			'held'     => (int) ($round['held'] ?? 0),
		);
		$log = array_slice($log, -50);
		update_option('ebi_campaign_round_log', $log, false);
	}

	/**
	 * ثبت پیام موقت برای نمایش پس از بازگشت.
	 *
	 * @param string $type success|error.
	 * @param string $text متن.
	 */
	private static function notice(string $type, string $text): void {
		set_transient(self::NOTICE_KEY . get_current_user_id(), array('type' => $type, 'text' => $text), 60);
	}

	/**
	 * چاپ و پاک‌کردن پیام موقت.
	 */
	private static function print_notice(): void {
		$key    = self::NOTICE_KEY . get_current_user_id();
		$notice = get_transient($key);
		if (!is_array($notice)) {
			return;
		}
		delete_transient($key);
		$class = 'success' === ($notice['type'] ?? '') ? 'notice-success' : 'notice-error';
		printf(
			'<div class="notice %s is-dismissible"><p>%s</p></div>',
			esc_attr($class),
			esc_html((string) ($notice['text'] ?? ''))
		);
	}

	/**
	 * بازگشت امن به صفحهٔ پنل.
	 *
	 * @param string $page نامک صفحه.
	 */
	private static function redirect(string $page): void {
		wp_safe_redirect(admin_url('admin.php?page=' . $page));
		exit;
	}

	// ------------------------------------------------------------------
	//  صفحه‌ها
	// ------------------------------------------------------------------

	/**
	 * صفحهٔ وضعیت.
	 */
	public static function page_status(): void {
		$status = Ebi_Campaign_Api::campaign_status(true);
		$rounds = Ebi_Campaign_Api::admin_request('admin', 'GET', 'rounds');
		echo '<div class="wrap ebi-admin" dir="rtl">';
		echo '<h1>' . esc_html__('وضعیت کمپین', 'ebi-campaign') . '</h1>';
		self::print_notice();

		if (is_wp_error($status)) {
			self::error_box($status);
		} else {
			$rows = array(
				__('وضعیت', 'ebi-campaign')            => (string) ($status['stateText'] ?? '-'),
				__('شمارهٔ دوره', 'ebi-campaign')       => number_format_i18n((int) ($status['roundNo'] ?? 0)),
				__('فاکتورهای معتبر', 'ebi-campaign')   => number_format_i18n((int) ($status['count'] ?? 0)) . ' / ' . number_format_i18n((int) ($status['target'] ?? 0)),
				__('باقی‌مانده', 'ebi-campaign')        => number_format_i18n((int) ($status['remaining'] ?? 0)),
				__('درصد تکمیل', 'ebi-campaign')        => number_format_i18n((float) ($status['progressPercent'] ?? 0), 1) . '٪',
				__('جایزهٔ دوره', 'ebi-campaign')       => number_format_i18n((int) ($status['prizeToman'] ?? 0)) . ' ' . __('تومان', 'ebi-campaign'),
				__('آخرین به‌روزرسانی', 'ebi-campaign') => (string) ($status['updatedAt'] ?? '-'),
			);
			echo '<table class="widefat striped ebi-table"><tbody>';
			foreach ($rows as $label => $value) {
				echo '<tr><th scope="row">' . esc_html((string) $label) . '</th><td>' . esc_html($value) . '</td></tr>';
			}
			echo '</tbody></table>';

			if (!empty($status['lastResult']) && is_array($status['lastResult'])) {
				$last = $status['lastResult'];
				echo '<h2>' . esc_html__('آخرین نتیجهٔ منتشرشده', 'ebi-campaign') . '</h2>';
				echo '<p>' . esc_html(sprintf(
					/* translators: ۱: شمارهٔ دوره، ۲: شمارهٔ سانسورشده. */
					__('دورهٔ %1$s — شمارهٔ سانسورشدهٔ برنده: %2$s', 'ebi-campaign'),
					number_format_i18n((int) ($last['roundNo'] ?? 0)),
					(string) ($last['mobileMasked'] ?? '-')
				)) . '</p>';
			}
		}

		echo '<h2>' . esc_html__('دوره‌ها', 'ebi-campaign') . '</h2>';
		if (is_wp_error($rounds)) {
			self::error_box($rounds);
		} else {
			echo '<table class="widefat striped ebi-table"><thead><tr>';
			foreach (array(__('دوره', 'ebi-campaign'), __('وضعیت', 'ebi-campaign'), __('شمارنده', 'ebi-campaign'), __('شروع', 'ebi-campaign'), __('پایان', 'ebi-campaign')) as $head) {
				echo '<th>' . esc_html($head) . '</th>';
			}
			echo '</tr></thead><tbody>';
			foreach ((array) $rounds as $round) {
				if (!is_array($round)) {
					continue;
				}
				echo '<tr><td>' . esc_html(number_format_i18n((int) ($round['roundNo'] ?? 0))) . '</td>'
					. '<td>' . esc_html((string) ($round['status'] ?? '-')) . '</td>'
					. '<td>' . esc_html(number_format_i18n((int) ($round['count'] ?? 0)) . ' / ' . number_format_i18n((int) ($round['target'] ?? 0))) . '</td>'
					. '<td>' . esc_html((string) ($round['startedAt'] ?? '-')) . '</td>'
					. '<td>' . esc_html((string) ($round['completedAt'] ?? '-')) . '</td></tr>';
			}
			echo '</tbody></table>';
		}

		self::action_form('refresh_cache', array(), __('تازه‌سازی کش اطلاعات', 'ebi-campaign'), false);
		echo '</div>';
	}

	/**
	 * صفحهٔ دوره‌ها و شروع/bستن دوره.
	 */
	public static function page_rounds(): void {
		$rounds = Ebi_Campaign_Api::admin_request('admin', 'GET', 'rounds');
		$held   = Ebi_Campaign_Api::admin_request('admin', 'GET', 'held', array(), array('limit' => 200));
		echo '<div class="wrap ebi-admin" dir="rtl">';
		echo '<h1>' . esc_html__('مدیریت دوره‌ها', 'ebi-campaign') . '</h1>';
		self::print_notice();

		if (is_wp_error($rounds)) {
			self::error_box($rounds);
		} else {
			$open = null;
			foreach ((array) $rounds as $round) {
				if (is_array($round) && 'Collecting' === ($round['status'] ?? '')) {
					$open = $round;
				}
			}
			echo '<h2>' . esc_html__('شروع دورهٔ جدید', 'ebi-campaign') . '</h2>';
			if ($open) {
				echo '<p>' . esc_html(sprintf(
					/* translators: %s: شمارهٔ دورهٔ باز. */
					__('دورهٔ %s هم‌اکنون باز است؛ تا بسته‌شدن و اعلام نتیجه، دورهٔ جدیدی باز نمی‌شود.', 'ebi-campaign'),
					number_format_i18n((int) ($open['roundNo'] ?? 0))
				)) . '</p>';
			} else {
				self::action_form('start_round', array(), __('باز کردن دورهٔ جدید', 'ebi-campaign'), true);
			}

			echo '<h2>' . esc_html__('فاکتورهای انتظار و سیاست انتقال', 'ebi-campaign') . '</h2>';
			if (is_wp_error($held)) {
				self::error_box($held);
			} else {
				$held_list = is_array($held) ? $held : array();
				echo '<p>' . esc_html(sprintf(
					/* translators: %s: تعداد فاکتور انتظار. */
					__('در این لحظه %s فاکتور در وضعیت انتظار است. این فاکتورها به‌صورت خودکار به هیچ دوره‌ای منتقل نمی‌شوند؛ انتقال فقط با فرمان صریح زیر انجام می‌شود.', 'ebi-campaign'),
					number_format_i18n(count($held_list))
				)) . '</p>';
				if (!empty($held_list) && $open) {
					self::action_form(
						'assign_held',
						array(
							'round_id' => (int) ($open['roundId'] ?? 0),
							'limit'    => 1000,
						),
						sprintf(
							/* translators: %s: شمارهٔ دوره. */
							__('انتقال فاکتورهای انتظار به دورهٔ %s', 'ebi-campaign'),
							number_format_i18n((int) ($open['roundNo'] ?? 0))
						),
						true
					);
				} elseif (!empty($held_list)) {
					echo '<p class="description">' . esc_html__('برای انتقال فاکتورهای انتظار نخست یک دورهٔ باز لازم است.', 'ebi-campaign') . '</p>';
				}
			}
		}

		echo '<h2>' . esc_html__('فهرست دوره‌ها', 'ebi-campaign') . '</h2>';
		echo '<table class="widefat striped ebi-table"><thead><tr>';
		foreach (array(__('دوره', 'ebi-campaign'), __('وضعیت', 'ebi-campaign'), __('شمارنده', 'ebi-campaign'), __('اقدام', 'ebi-campaign')) as $head) {
			echo '<th>' . esc_html($head) . '</th>';
		}
		echo '</tr></thead><tbody>';
		foreach ((array) $rounds as $round) {
			if (!is_array($round)) {
				continue;
			}
			echo '<tr><td>' . esc_html(number_format_i18n((int) ($round['roundNo'] ?? 0))) . '</td>'
				. '<td>' . esc_html((string) ($round['status'] ?? '-')) . '</td>'
				. '<td>' . esc_html(number_format_i18n((int) ($round['count'] ?? 0))) . '</td><td>';
			if (in_array((string) ($round['status'] ?? ''), array('Completed', 'Closed'), true)) {
				self::action_form(
					'close_round',
					array('round_id' => (int) ($round['roundId'] ?? 0)),
					__('بستن دوره', 'ebi-campaign'),
					true,
					true
				);
			}
			echo '</td></tr>';
		}
		echo '</tbody></table></div>';
	}

	/**
	 * صفحهٔ فاکتورهای نیازمند بررسی.
	 */
	public static function page_reviews(): void {
		$list = Ebi_Campaign_Api::admin_request('admin', 'GET', 'invoices', array(), array('status' => 'NeedsReview', 'limit' => 50));
		echo '<div class="wrap ebi-admin" dir="rtl">';
		echo '<h1>' . esc_html__('فاکتورهای نیازمند بررسی', 'ebi-campaign') . '</h1>';
		self::print_notice();

		if (is_wp_error($list)) {
			self::error_box($list);
			echo '</div>';
			return;
		}

		$items = is_array($list) ? $list : array();
		if (empty($items)) {
			echo '<p>' . esc_html__('هیچ فاکتوری در وضعیت بررسی نیست.', 'ebi-campaign') . '</p></div>';
			return;
		}

		echo '<table class="widefat striped ebi-table"><thead><tr>';
		foreach (array(__('فاکتور', 'ebi-campaign'), __('تاریخ', 'ebi-campaign'), __('مبلغ', 'ebi-campaign'), __('دلیل بررسی', 'ebi-campaign'), __('شواهد', 'ebi-campaign'), __('تصمیم', 'ebi-campaign')) as $head) {
			echo '<th>' . esc_html($head) . '</th>';
		}
		echo '</tr></thead><tbody>';
		foreach ($items as $row) {
			if (!is_array($row)) {
				continue;
			}
			echo '<tr><td>' . esc_html((string) ($row['fCode'] ?? '-')) . '</td>'
				. '<td>' . esc_html((string) ($row['jalali'] ?? $row['fDate'] ?? '-')) . '</td>'
				. '<td>' . esc_html(number_format_i18n((float) ($row['gross'] ?? 0))) . '</td>'
				. '<td>' . esc_html((string) ($row['reviewReason'] ?? '-')) . '</td>'
				. '<td>' . esc_html((string) ($row['reviewEvidence'] ?? '-')) . '</td><td>';
			$invoice_id = (int) ($row['invoiceId'] ?? 0);
			self::action_form(
				'decision',
				array('invoice_id' => $invoice_id, 'verdict' => 'valid'),
				__('تأیید اعتبار', 'ebi-campaign'),
				true,
				true,
				array('reason' => __('تأیید از تاریخچهٔ هلو', 'ebi-campaign'))
			);
			self::action_form(
				'decision',
				array('invoice_id' => $invoice_id, 'verdict' => 'void'),
				__('تأیید ابطال', 'ebi-campaign'),
				true,
				true,
				array('reason' => __('ابطال در منبع هلو', 'ebi-campaign'))
			);
			echo '</td></tr>';
		}
		echo '</tbody></table></div>';
	}

	/**
	 * صفحهٔ فاکتورهای انتظار.
	 */
	public static function page_held(): void {
		$list   = Ebi_Campaign_Api::admin_request('admin', 'GET', 'held', array(), array('limit' => 200));
		$rounds = Ebi_Campaign_Api::admin_request('admin', 'GET', 'rounds');
		echo '<div class="wrap ebi-admin" dir="rtl">';
		echo '<h1>' . esc_html__('فاکتورهای انتظار', 'ebi-campaign') . '</h1>';
		self::print_notice();

		if (is_wp_error($list)) {
			self::error_box($list);
			echo '</div>';
			return;
		}

		$items = is_array($list) ? $list : array();
		echo '<p>' . esc_html(sprintf(
			/* translators: %s: تعداد فاکتور انتظار. */
			__('%s فاکتور در انتظار تخصیص است.', 'ebi-campaign'),
			number_format_i18n(count($items))
		)) . '</p>';

		$open_id = 0;
		foreach ((array) $rounds as $round) {
			if (is_array($round) && 'Collecting' === ($round['status'] ?? '')) {
				$open_id = (int) ($round['roundId'] ?? 0);
			}
		}
		if (!empty($items) && $open_id > 0) {
			self::action_form(
				'assign_held',
				array('round_id' => $open_id, 'limit' => 1000),
				__('تخصیص این فاکتورها به دورهٔ باز', 'ebi-campaign'),
				true
			);
		}

		echo '<table class="widefat striped ebi-table"><thead><tr>';
		foreach (array(__('فاکتور', 'ebi-campaign'), __('تاریخ', 'ebi-campaign'), __('صندوق', 'ebi-campaign'), __('مبلغ', 'ebi-campaign'), __('امتیاز', 'ebi-campaign'), __('وضعیت', 'ebi-campaign')) as $head) {
			echo '<th>' . esc_html($head) . '</th>';
		}
		echo '</tr></thead><tbody>';
		foreach ($items as $row) {
			if (!is_array($row)) {
				continue;
			}
			echo '<tr><td>' . esc_html((string) ($row['fCode'] ?? '-')) . '</td>'
				. '<td>' . esc_html((string) ($row['jalali'] ?? $row['fDate'] ?? '-')) . '</td>'
				. '<td>' . esc_html((string) ($row['cashier'] ?? '-')) . '</td>'
				. '<td>' . esc_html(number_format_i18n((float) ($row['gross'] ?? 0))) . '</td>'
				. '<td>' . esc_html(number_format_i18n((int) ($row['points'] ?? 0))) . '</td>'
				. '<td>' . esc_html((string) ($row['status'] ?? '-')) . '</td></tr>';
		}
		echo '</tbody></table></div>';
	}

	/**
	 * صفحهٔ قرعه‌کشی.
	 */
	public static function page_lottery(): void {
		$rounds = Ebi_Campaign_Api::admin_request('admin', 'GET', 'rounds');
		$runs   = Ebi_Campaign_Api::admin_request('admin', 'GET', 'lottery', array(), array('limit' => 20));
		echo '<div class="wrap ebi-admin" dir="rtl">';
		echo '<h1>' . esc_html__('قرعه‌کشی', 'ebi-campaign') . '</h1>';
		self::print_notice();

		if (is_wp_error($rounds) || is_wp_error($runs)) {
			self::error_box(is_wp_error($rounds) ? $rounds : $runs);
			echo '</div>';
			return;
		}

		$completed = array();
		foreach ((array) $rounds as $round) {
			if (is_array($round) && in_array((string) ($round['status'] ?? ''), array('Completed', 'Closed'), true)) {
				$completed[] = $round;
			}
		}

		echo '<h2>' . esc_html__('تثبیت مجموعه و برگزاری', 'ebi-campaign') . '</h2>';
		if (empty($completed)) {
			echo '<p>' . esc_html__('هنوز دوره‌ای به ۱۰۰۰ فاکتور معتبر نرسیده است.', 'ebi-campaign') . '</p>';
		}
		foreach ($completed as $round) {
			self::action_form(
				'prepare_lottery',
				array('round_id' => (int) ($round['roundId'] ?? 0)),
				sprintf(
					/* translators: %s: شمارهٔ دوره. */
					__('تثبیت مجموعهٔ ورودی‌های دورهٔ %s', 'ebi-campaign'),
					number_format_i18n((int) ($round['roundNo'] ?? 0))
				),
				true,
				true,
				array('acknowledge_reviews' => '1')
			);
		}

		echo '<h2>' . esc_html__('فراگرفت‌ها', 'ebi-campaign') . '</h2>';
		$run_list = is_array($runs) ? $runs : array();
		if (empty($run_list)) {
			echo '<p>' . esc_html__('فراگرفتی ثبت نشده است.', 'ebi-campaign') . '</p></div>';
			return;
		}

		echo '<table class="widefat striped ebi-table"><thead><tr>';
		foreach (array(__('فراگرفت', 'ebi-campaign'), __('دوره', 'ebi-campaign'), __('وضعیت', 'ebi-campaign'), __('ورودی/واجد شرایط', 'ebi-campaign'), __('برنده', 'ebi-campaign'), __('اقدام', 'ebi-campaign')) as $head) {
			echo '<th>' . esc_html($head) . '</th>';
		}
		echo '</tr></thead><tbody>';
		foreach ($run_list as $run) {
			if (!is_array($run)) {
				continue;
			}
			$run_id   = (int) ($run['runId'] ?? 0);
			$status   = (string) ($run['status'] ?? '');
			$verified = '' !== (string) ($run['seedHex'] ?? '');
			echo '<tr><td>' . esc_html(number_format_i18n($run_id)) . '</td>'
				. '<td>' . esc_html(number_format_i18n((int) ($run['roundNo'] ?? 0))) . '</td>'
				. '<td>' . esc_html($status) . '</td>'
				. '<td>' . esc_html(number_format_i18n((int) ($run['entryCount'] ?? 0)) . ' / ' . number_format_i18n((int) ($run['eligibleCount'] ?? 0))) . '</td>'
				. '<td>' . esc_html($verified ? (string) ($run['winnerMobile'] ?? '-') : __('انتشارنشده', 'ebi-campaign')) . '</td><td>';

			if ('Committed' === $status) {
				self::action_form('select_winner', array('run_id' => $run_id), __('انتخاب برنده', 'ebi-campaign'), true, true);
			}
			if ('Selected' === $status) {
				self::action_form('finalize_run', array('run_id' => $run_id), __('نهایی‌سازی نتیجه', 'ebi-campaign'), true, true);
			}
			if (in_array($status, array('Committed', 'RequiresReview'), true)) {
				self::action_form('abort_run', array('run_id' => $run_id), __('لغو با دلیل', 'ebi-campaign'), false, true, array('reason' => ''));
			}
			if ('Finalized' === $status && empty($run['publishApproved'])) {
				echo '<p class="description">' . esc_html__('نتیجه نهایی است و از صفحهٔ «برندگان و تصاویر» منتشر می‌شود.', 'ebi-campaign') . '</p>';
			}
			if ($verified) {
				$receipt = Ebi_Campaign_Api::receipt($run_id);
				if (!is_wp_error($receipt)) {
					echo '<p class="description">' . esc_html(sprintf(
						/* translators: %s: وضعیت راستی‌آزمایی. */
						__('راستی‌آزمایی عمومی: %s', 'ebi-campaign'),
						!empty($receipt['verified']) ? __('تأییدشده', 'ebi-campaign') : __('در انتظار', 'ebi-campaign')
					)) . '</p>';
				}
			}
			echo '</td></tr>';
		}
		echo '</tbody></table></div>';
	}

	/**
	 * صفحهٔ برندگان، انتشار و تصاویر.
	 */
	public static function page_winners(): void {
		$runs = Ebi_Campaign_Api::admin_request('admin', 'GET', 'lottery', array(), array('limit' => 20));
		echo '<div class="wrap ebi-admin" dir="rtl">';
		echo '<h1>' . esc_html__('برندگان و تصاویر', 'ebi-campaign') . '</h1>';
		self::print_notice();

		if (is_wp_error($runs)) {
			self::error_box($runs);
			echo '</div>';
			return;
		}

		$list = is_array($runs) ? $runs : array();
		if (empty($list)) {
			echo '<p>' . esc_html__('هنوز فراگرفتی ثبت نشده است.', 'ebi-campaign') . '</p></div>';
			return;
		}

		foreach ($list as $run) {
			if (!is_array($run)) {
				continue;
			}
			$run_id = (int) ($run['runId'] ?? 0);
			$status = (string) ($run['status'] ?? '');
			echo '<div class="ebi-admin__card">';
			echo '<h2>' . esc_html(sprintf(
				/* translators: ۱: شمارهٔ دوره، ۲: شناسهٔ فراگرفت. */
				__('دورهٔ %1$s — فراگرفت %2$s', 'ebi-campaign'),
				number_format_i18n((int) ($run['roundNo'] ?? 0)),
				number_format_i18n($run_id)
			)) . '</h2>';
			echo '<p>' . esc_html(sprintf(
				/* translators: ۱: وضعیت فراگرفت، ۲: وضعیت انتشار. */
				__('وضعیت: %1$s — انتشار عمومی: %2$s', 'ebi-campaign'),
				$status,
				!empty($run['publishApproved']) ? __('منتشرشده', 'ebi-campaign') : __('منتشرنشده', 'ebi-campaign')
			)) . '</p>';

			if ('Finalized' !== $status) {
				echo '<p class="description">' . esc_html__('تنها نتیجهٔ نهایی‌شده قابل انتشار است.', 'ebi-campaign') . '</p></div>';
				continue;
			}

			if (empty($run['publishApproved'])) {
				self::action_form(
					'publish_result',
					array('run_id' => $run_id),
					__('انتشار عمومی نتیجه', 'ebi-campaign'),
					true,
					false,
					array('display_name' => '')
				);
			} else {
				echo '<p>' . esc_html(sprintf(
					/* translators: %s: شمارهٔ سانسورشده. */
					__('شمارهٔ سانسورشده: %s', 'ebi-campaign'),
					(string) ($run['winnerMobile'] ?? '-')
				)) . '</p>';
				if (!empty($run['winnerImageApproved']) && !empty($run['winnerImageUrl'])) {
					echo '<p><img src="' . esc_url((string) $run['winnerImageUrl']) . '" alt="'
						. esc_attr__('پیش‌نمایش تصویر برنده', 'ebi-campaign') . '" style="max-width:160px;height:auto;border-radius:8px" /></p>';
				}
				self::image_form($run_id);
				self::action_form('withdraw_image', array('run_id' => $run_id), __('برداشتن تصویر از خروجی عمومی', 'ebi-campaign'), false);
				self::action_form(
					'unpublish_result',
					array('run_id' => $run_id),
					__('لغو انتشار عمومی', 'ebi-campaign'),
					false,
					false,
					array('reason' => '')
				);
			}

			echo '</div>';
		}
		echo '</div>';
	}

	/**
	 * صفحهٔ تنظیمات.
	 */
	public static function page_settings(): void {
		$settings = Ebi_Campaign_Settings::all();
		$status   = Ebi_Campaign_Settings::secret_status();
		echo '<div class="wrap ebi-admin" dir="rtl">';
		echo '<h1>' . esc_html__('تنظیمات کمپین', 'ebi-campaign') . '</h1>';
		self::print_notice();

		echo '<form method="post" action="' . esc_url(admin_url('admin-post.php')) . '">';
		echo '<input type="hidden" name="action" value="' . esc_attr(self::ACTION) . '" />';
		echo '<input type="hidden" name="ebi_op" value="save_settings" />';
		wp_nonce_field(self::ACTION);

		echo '<table class="form-table" role="presentation"><tbody>';
		self::text_row('api_base_url', __('آدرس سرویس مرکزی کمپین', 'ebi-campaign'), (string) $settings['api_base_url'], __('مثال: https://ebimarket.ir — بدون اسلش انتهایی.', 'ebi-campaign'));
		self::number_row('api_timeout', __('مهلت پاسخ سرویس (ثانیه)', 'ebi-campaign'), (int) $settings['api_timeout'], 2, 30);
		self::number_row('cache_ttl', __('مدت کش اطلاعات در سایت (ثانیه)', 'ebi-campaign'), (int) $settings['cache_ttl'], Ebi_Campaign_Settings::MIN_CACHE_TTL, Ebi_Campaign_Settings::MAX_CACHE_TTL);
		self::text_row('campaign_slug', __('نامک صفحهٔ کمپین', 'ebi-campaign'), (string) $settings['campaign_slug'], '');
		self::text_row('winners_slug', __('نامک صفحهٔ برندگان', 'ebi-campaign'), (string) $settings['winners_slug'], '');
		self::text_row('prize_label', __('متن جایزه', 'ebi-campaign'), (string) $settings['prize_label'], '');
		self::text_row('bale_channel', __('آدرس کانال بله برای عضویت', 'ebi-campaign'), (string) $settings['bale_channel'], '');
		self::text_row('support_text', __('متن پشتیبانی', 'ebi-campaign'), (string) $settings['support_text'], '');

		echo '<tr><th scope="row">' . esc_html__('مجوز ارتباط HTTP محلی', 'ebi-campaign') . '</th><td>';
		echo '<label><input type="checkbox" name="ebi_settings[allow_insecure]" value="1" ' . checked((int) $settings['allow_insecure'], 1, false) . ' /> ';
		echo esc_html__('فقط برای محیط آزمایش محلی (localhost). در سایت عملیاتی خاموش بماند.', 'ebi-campaign') . '</label></td></tr>';
		echo '</tbody></table>';

		echo '<h2>' . esc_html__('کلیدهای سرویس‌به‌سرویس', 'ebi-campaign') . '</h2>';
		echo '<p class="description">' . esc_html__('اگر کلیدها را در wp-config.php تعریف کنید، در پایگاه داده ذخیره نمی‌شوند و این فیلدها نادیده گرفته می‌شوند. مقدار کلید هیچ‌گاه در صفحه یا JavaScript چاپ نمی‌شود.', 'ebi-campaign') . '</p>';
		echo '<table class="form-table" role="presentation"><tbody>';
		$labels = array(
			'admin'           => __('کلید مدیر کل (دسترسی کامل)', 'ebi-campaign'),
			'campaign.manage' => __('کلید مدیریت دوره‌ها', 'ebi-campaign'),
			'review.decide'   => __('کلید تصمیم روی فاکتورها', 'ebi-campaign'),
			'lottery.run'     => __('کلید اجرای قرعه‌کشی', 'ebi-campaign'),
			'lottery.publish' => __('کلید انتشار نتیجه و تصویر', 'ebi-campaign'),
			'privacy.contact' => __('کلید مشاهدهٔ شمارهٔ برنده', 'ebi-campaign'),
		);
		foreach ($labels as $role => $label) {
			$current = isset($status[ $role ]) ? (string) $status[ $role ] : '';
			echo '<tr><th scope="row">' . esc_html($label) . '</th><td>';
			echo '<input type="password" class="regular-text" name="ebi_keys[' . esc_attr($role) . ']" value="" autocomplete="new-password" placeholder="'
				. esc_attr($current === '' ? __('کلید تنظیم نشده است', 'ebi-campaign') : sprintf(
					/* translators: %s: چهار رقم آخر کلید. */
					__('کلید ذخیره‌شده: %s', 'ebi-campaign'),
					$current
				)) . '" />';
			echo '</td></tr>';
		}
		echo '</tbody></table>';

		echo '<p><button type="submit" class="button button-primary">' . esc_html__('ذخیرهٔ تنظیمات', 'ebi-campaign') . '</button></p>';
		echo '</form>';

		echo '<h2>' . esc_html__('نکات راه‌اندازی', 'ebi-campaign') . '</h2><ul class="ebi-help-list">';
		echo '<li>' . esc_html__('برای صفحهٔ کمپین یک صفحه بسازید و شورت‌کد [ebi_campaign] را در آن قرار دهید.', 'ebi-campaign') . '</li>';
		echo '<li>' . esc_html__('برای آرشیو برندگان، شورت‌کد [ebi_winners per_page="10"] را در صفحهٔ برندگان بگذارید.', 'ebi-campaign') . '</li>';
		echo '<li>' . esc_html__('برای نمایش شمارنده در برگهٔ اصلی، [ebi_campaign_counter] را در برگه یا ویجت قرار دهید.', 'ebi-campaign') . '</li>';
		echo '<li>' . esc_html__('اگر شورت‌کد را در المنتور یا وودمارت استفاده می‌کنید، از ویجت «شورت‌کد» همان صفحه‌ساز بهره ببرید.', 'ebi-campaign') . '</li>';
		echo '</ul>';

		echo '<h2>' . esc_html__('ثابت‌های پیشنهادی در wp-config.php', 'ebi-campaign') . '</h2>';
		echo '<pre class="ebi-code">define( \'EBI_CAMPAIGN_ADMIN_KEY\', \'...\' );
define( \'EBI_CAMPAIGN_CAMPAIGN_KEY\', \'...\' );
define( \'EBI_CAMPAIGN_REVIEW_KEY\', \'...\' );
define( \'EBI_CAMPAIGN_LOTTERY_KEY\', \'...\' );
define( \'EBI_CAMPAIGN_PUBLISH_KEY\', \'...\' );
define( \'EBI_CAMPAIGN_PRIVACY_KEY\', \'...\' );</pre>';
		echo '</div>';
	}

	// ------------------------------------------------------------------
	//  کمک‌کننده‌های نمایش
	// ------------------------------------------------------------------

	/**
	 * یک ردیف متنی در تنظیمات.
	 *
	 * @param string $key   کلید تنظیم.
	 * @param string $label برچسب.
	 * @param string $value مقدار.
	 * @param string $hint  توضیح.
	 */
	private static function text_row(string $key, string $label, string $value, string $hint): void {
		echo '<tr><th scope="row"><label for="ebi-' . esc_attr($key) . '">' . esc_html($label) . '</label></th><td>';
		echo '<input type="text" class="regular-text" id="ebi-' . esc_attr($key) . '" name="ebi_settings[' . esc_attr($key) . ']" value="'
			. esc_attr($value) . '" />';
		if ($hint !== '') {
			echo '<p class="description">' . esc_html($hint) . '</p>';
		}
		echo '</td></tr>';
	}

	/**
	 * یک ردیف عددی در تنظیمات.
	 *
	 * @param string $key   کلید.
	 * @param string $label برچسب.
	 * @param int    $value مقدار.
	 * @param int    $min   کمینه.
	 * @param int    $max   بیشینه.
	 */
	private static function number_row(string $key, string $label, int $value, int $min, int $max): void {
		echo '<tr><th scope="row"><label for="ebi-' . esc_attr($key) . '">' . esc_html($label) . '</label></th><td>';
		echo '<input type="number" id="ebi-' . esc_attr($key) . '" name="ebi_settings[' . esc_attr($key) . ']" value="' . esc_attr((string) $value)
			. '" min="' . esc_attr((string) $min) . '" max="' . esc_attr((string) $max) . '" step="1" />';
		echo '</td></tr>';
	}

	/**
	 * فرم عملیات با نونس و تأیید صریح.
	 *
	 * @param string               $op      نام عملیات.
	 * @param array<string,mixed>  $hidden  فیلدهای مخفی.
	 * @param string               $label   برچسب دکمه.
	 * @param bool                 $confirm نیاز به کادر تأیید.
	 * @param bool                 $inline  نمایش درون‌خطی.
	 * @param array<string,string> $fields  فیلدهای متنی.
	 */
	private static function action_form(string $op, array $hidden, string $label, bool $confirm, bool $inline = false, array $fields = array()): void {
		echo '<form method="post" action="' . esc_url(admin_url('admin-post.php')) . '" class="ebi-action-form'
			. ($inline ? ' ebi-action-form--inline' : '') . '">';
		echo '<input type="hidden" name="action" value="' . esc_attr(self::ACTION) . '" />';
		echo '<input type="hidden" name="ebi_op" value="' . esc_attr($op) . '" />';
		foreach ($hidden as $name => $value) {
			echo '<input type="hidden" name="' . esc_attr((string) $name) . '" value="' . esc_attr((string) $value) . '" />';
		}
		foreach ($fields as $name => $value) {
			echo '<input type="text" name="' . esc_attr((string) $name) . '" value="' . esc_attr((string) $value) . '" placeholder="'
				. esc_attr((string) $name) . '" />';
		}
		if ($confirm) {
			echo '<label class="ebi-confirm"><input type="checkbox" name="confirm" value="1" /> '
				. esc_html__('تأیید می‌کنم', 'ebi-campaign') . '</label> ';
		}
		wp_nonce_field(self::ACTION);
		echo '<button type="submit" class="button">' . esc_html($label) . '</button>';
		echo '</form>';
	}

	/**
	 * فرم بارگذاری تصویر برنده.
	 *
	 * @param int $run_id شناسهٔ فراگرفت.
	 */
	private static function image_form(int $run_id): void {
		echo '<form method="post" enctype="multipart/form-data" action="' . esc_url(admin_url('admin-post.php')) . '" class="ebi-action-form">';
		echo '<input type="hidden" name="action" value="' . esc_attr(self::ACTION) . '" />';
		echo '<input type="hidden" name="ebi_op" value="upload_image" />';
		echo '<input type="hidden" name="run_id" value="' . esc_attr((string) $run_id) . '" />';
		echo '<p><input type="file" name="winner_image" accept="image/jpeg,image/png,image/webp" required="required" /></p>';
		echo '<p class="description">' . esc_html__('فقط JPG، PNG یا WebP تا ۲ مگابایت. انتشار تصویر نیازمند تأیید رضایت برنده است.', 'ebi-campaign') . '</p>';
		echo '<label class="ebi-confirm"><input type="checkbox" name="confirm" value="1" /> '
			. esc_html__('رضایت برنده برای انتشار تصویر دریافت شده است', 'ebi-campaign') . '</label> ';
		wp_nonce_field(self::ACTION);
		echo '<button type="submit" class="button button-primary">' . esc_html__('بارگذاری و انتشار تصویر', 'ebi-campaign') . '</button>';
		echo '</form>';
	}

	/**
	 * کادر خطا.
	 *
	 * @param WP_Error $error خطا.
	 */
	private static function error_box(WP_Error $error): void {
		echo '<div class="notice notice-error"><p>' . esc_html($error->get_error_message()) . '</p></div>';
	}
}