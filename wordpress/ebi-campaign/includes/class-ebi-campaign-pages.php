<?php
/**
 * صفحات عمومی کمپین: شورت‌کدهای صفحهٔ کمپین، شمارنده و آرشیو برندگان.
 *
 * داده‌ها همیشه از سرویس مرکزی می‌آید و هیچ عدد ساختگی چاپ نمی‌شود؛ در صورت
 * قطع ارتباط، وضعیت خطا با پیام روشن نمایش داده می‌شود و شمارندهٔ صفر جعل نمی‌شود.
 *
 * @package EbiCampaign
 */

defined('ABSPATH') || exit;

/**
 * خروجی عمومی کمپین.
 */
final class Ebi_Campaign_Pages {

	public const PAGE_QUERY  = 'ebi_page';
	public const ROUND_QUERY = 'ebi_round';
	public const REFRESH_QUERY = 'ebi_refresh';

	/**
	 * ثبت شورت‌کدها و منابع.
	 */
	public static function init(): void {
		add_shortcode('ebi_campaign', array(__CLASS__, 'shortcode_campaign'));
		add_shortcode('ebi_campaign_counter', array(__CLASS__, 'shortcode_counter'));
		add_shortcode('ebi_winners', array(__CLASS__, 'shortcode_winners'));
		add_action('wp_enqueue_scripts', array(__CLASS__, 'register_assets'));
		add_filter('query_vars', array(__CLASS__, 'register_query_vars'));
	}

	/**
	 * پارامترهای پرس‌وجوی قابل استفاده در آدرس صفحه.
	 *
	 * @param array<int,string> $vars پارامترهای موجود.
	 * @return array<int,string>
	 */
	public static function register_query_vars(array $vars): array {
		$vars[] = self::PAGE_QUERY;
		$vars[] = self::ROUND_QUERY;
		return $vars;
	}

	/**
	 * ثبت (نه بارگذاری) منابع؛ بارگذاری فقط وقتی شورت‌کد اجرا شود.
	 */
	public static function register_assets(): void {
		wp_register_style(
			'ebi-campaign',
			EBI_CAMPAIGN_URL . 'assets/css/ebi-campaign.css',
			array(),
			EBI_CAMPAIGN_VERSION
		);
	}

	/**
	 * بارگذاری مشروط استایل.
	 */
	private static function enqueue(): void {
		// شورت‌کد می‌تواند پیش از اجرای wp_enqueue_scripts یا درون ویجت اجرا شود،
		// پس ثبت استایل به‌صورت تنبل انجام می‌شود تا در همهٔ حالت‌ها یک‌بار و درست بارگذاری شود.
		if (!wp_style_is('ebi-campaign', 'registered')) {
			wp_register_style(
				'ebi-campaign',
				EBI_CAMPAIGN_URL . 'assets/css/ebi-campaign.css',
				array(),
				EBI_CAMPAIGN_VERSION
			);
		}
		if (!wp_style_is('ebi-campaign', 'enqueued')) {
			wp_enqueue_style('ebi-campaign');
		}
	}

	/**
	 * آیا کاربر مجاز می‌تواند کش صفحه را تازه کند؟ (جلوگیری از سوءاستفادهٔ ناشناس)
	 *
	 * @return bool
	 */
	private static function may_refresh(): bool {
		return is_user_logged_in() && current_user_can('ebi_manage_campaign')
			&& isset($_GET[ self::REFRESH_QUERY ]); // phpcs:ignore WordPress.Security.NonceVerification.Recommended
	}

	/**
	 * شورت‌کد صفحهٔ کمپین.
	 *
	 * @param array<string,mixed> $atts ویژگی‌ها.
	 * @return string
	 */
	public static function shortcode_campaign($atts = array()): string {
		$atts = shortcode_atts(
			array(
				'show_rules'  => 'yes',
				'show_bale'   => 'yes',
				'show_result' => 'yes',
			),
			$atts,
			'ebi_campaign'
		);

		self::enqueue();
		$status = Ebi_Campaign_Api::campaign_status(self::may_refresh());

		$html  = '<div class="ebi-campaign ebi-campaign--landing" dir="rtl">';
		$html .= self::render_header($status);

		if (is_wp_error($status)) {
			$html .= self::render_error($status);
		} else {
			$html .= self::render_progress($status);
			if ('yes' === $atts['show_result']) {
				$html .= self::render_last_result($status);
			}
		}

		if ('yes' === $atts['show_bale']) {
			$html .= self::render_bale_guide();
		}
		if ('yes' === $atts['show_rules']) {
			$html .= self::render_rules();
		}
		$html .= '</div>';

		return $html;
	}

	/**
	 * شورت‌کد شمارندهٔ فشرده.
	 *
	 * @param array<string,mixed> $atts ویژگی‌ها.
	 * @return string
	 */
	public static function shortcode_counter($atts = array()): string {
		$atts = shortcode_atts(array('label' => 'فاکتور معتبر در این دوره'), $atts, 'ebi_campaign_counter');

		self::enqueue();
		$status = Ebi_Campaign_Api::campaign_status(self::may_refresh());

		if (is_wp_error($status)) {
			return '<div class="ebi-campaign ebi-campaign--counter ebi-campaign--error" dir="rtl">'
				. esc_html__('در این لحظه اطلاعات کمپین در دسترس نیست.', 'ebi-campaign')
				. '</div>';
		}

		$count   = isset($status['count']) ? (int) $status['count'] : 0;
		$target  = isset($status['target']) ? (int) $status['target'] : 0;
		$percent = isset($status['progressPercent']) ? (float) $status['progressPercent'] : 0.0;

		return '<div class="ebi-campaign ebi-campaign--counter" dir="rtl">'
			. '<span class="ebi-counter__value">' . esc_html(number_format_i18n($count)) . '</span>'
			. '<span class="ebi-counter__label">' . esc_html((string) $atts['label']) . '</span>'
			. '<span class="ebi-counter__target">' . esc_html(sprintf(
				/* translators: %s: تعداد هدف دوره. */
				__('از %s فاکتور', 'ebi-campaign'),
				number_format_i18n($target)
			)) . '</span>'
			. self::render_bar($percent)
			. '</div>';
	}

	/**
	 * شورت‌کد آرشیو برندگان.
	 *
	 * @param array<string,mixed> $atts ویژگی‌ها.
	 * @return string
	 */
	public static function shortcode_winners($atts = array()): string {
		$atts = shortcode_atts(
			array(
				'per_page' => 10,
				'round'    => 0,
			),
			$atts,
			'ebi_winners'
		);

		self::enqueue();
		$per_page = max(1, min(50, (int) $atts['per_page']));
		$page     = isset($_GET[ self::PAGE_QUERY ]) ? max(1, (int) $_GET[ self::PAGE_QUERY ]) : 1; // phpcs:ignore WordPress.Security.NonceVerification.Recommended
		$round    = isset($_GET[ self::ROUND_QUERY ]) ? max(0, (int) $_GET[ self::ROUND_QUERY ]) : max(0, (int) $atts['round']); // phpcs:ignore WordPress.Security.NonceVerification.Recommended

		$data = Ebi_Campaign_Api::winners($page, $per_page, $round > 0 ? $round : null);

		$html = '<div class="ebi-campaign ebi-campaign--winners" dir="rtl">';
		$html .= '<h2 class="ebi-section-title">' . esc_html__('آرشیو رسمی برندگان', 'ebi-campaign') . '</h2>';

		if (is_wp_error($data)) {
			$html .= self::render_error($data);
			return $html . '</div>';
		}

		$winners = isset($data['winners']) && is_array($data['winners']) ? $data['winners'] : array();
		if (empty($winners)) {
			$html .= '<p class="ebi-empty">' . esc_html__('هنوز برنده‌ای به‌صورت رسمی منتشر نشده است.', 'ebi-campaign') . '</p>';
		} else {
			$html .= '<div class="ebi-winners-grid">';
			foreach ($winners as $winner) {
				$html .= self::render_winner_card(is_array($winner) ? $winner : array());
			}
			$html .= '</div>';
		}

		$html .= self::render_pagination($data, $page, $per_page, $round);
		$html .= '</div>';

		return $html;
	}

	/**
	 * سرصفحهٔ کمپین همراه وضعیت.
	 *
	 * @param array<string,mixed>|WP_Error $status وضعیت.
	 * @return string
	 */
	private static function render_header($status): string {
		$prize = (string) Ebi_Campaign_Settings::get('prize_label', '');
		$html  = '<header class="ebi-header">';
		$html .= '<h1 class="ebi-title">' . esc_html__('کمپین فروشگاه ابی', 'ebi-campaign') . '</h1>';
		if ($prize !== '') {
			$html .= '<p class="ebi-prize">' . esc_html(sprintf(
				/* translators: %s: مبلغ جایزه. */
				__('جایزهٔ هر دوره: %s', 'ebi-campaign'),
				$prize
			)) . '</p>';
		}

		if (!is_wp_error($status)) {
			$state = isset($status['stateText']) ? (string) $status['stateText'] : '';
			$badge = self::state_label(isset($status['state']) ? (string) $status['state'] : '');
			if ($badge !== '') {
				$html .= '<p class="ebi-state ebi-state--' . esc_attr(sanitize_html_class(strtolower((string) ($status['state'] ?? '')))) . '">'
					. esc_html($badge) . '</p>';
			}
			if ($state !== '') {
				$html .= '<p class="ebi-state-text">' . esc_html($state) . '</p>';
			}
		}
		$html .= '</header>';
		return $html;
	}

	/**
	 * نوار پیشرفت و شمارنده.
	 *
	 * @param array<string,mixed> $status وضعیت.
	 * @return string
	 */
	private static function render_progress(array $status): string {
		$count   = isset($status['count']) ? (int) $status['count'] : 0;
		$target  = isset($status['target']) ? (int) $status['target'] : 0;
		$remain  = isset($status['remaining']) ? (int) $status['remaining'] : 0;
		$percent = isset($status['progressPercent']) ? (float) $status['progressPercent'] : 0.0;
		$round   = isset($status['roundNo']) ? (int) $status['roundNo'] : 0;

		$html  = '<section class="ebi-progress-block">';
		if ($round > 0) {
			$html .= '<p class="ebi-round">' . esc_html(sprintf(
				/* translators: %s: شمارهٔ دوره. */
				__('دورهٔ %s', 'ebi-campaign'),
				number_format_i18n($round)
			)) . '</p>';
		}
		$html .= '<p class="ebi-count"><strong>' . esc_html(number_format_i18n($count)) . '</strong> '
			. esc_html__('فاکتور معتبر ثبت شده است', 'ebi-campaign') . '</p>';
		$html .= self::render_bar($percent, $count, $target);
		$html .= '<p class="ebi-remaining">';
		$html .= $remain > 0
			? esc_html(sprintf(
				/* translators: %s: تعداد فاکتور باقی‌مانده. */
				__('%s فاکتور تا تکمیل این دوره باقی مانده است.', 'ebi-campaign'),
				number_format_i18n($remain)
			))
			: esc_html__('این دوره تکمیل شده است و نتیجهٔ آن پس از قرعه‌کشی اعلام می‌شود.', 'ebi-campaign');
		$html .= '</p>';

		if (!empty($status['awaitingManagerStart'])) {
			$html .= '<p class="ebi-note">' . esc_html__('شمارندهٔ دورهٔ تکمیل‌شده حفظ می‌شود و دورهٔ بعدی با شروع رسمی مدیریت آغاز خواهد شد.', 'ebi-campaign') . '</p>';
		}
		$html .= '</section>';
		return $html;
	}

	/**
	 * نمایش آخرین نتیجهٔ رسمی.
	 *
	 * @param array<string,mixed> $status وضعیت.
	 * @return string
	 */
	private static function render_last_result(array $status): string {
		if (empty($status['lastResult']) || !is_array($status['lastResult'])) {
			return '';
		}
		$result = $status['lastResult'];
		$html   = '<section class="ebi-last-result">';
		$html .= '<h2 class="ebi-section-title">' . esc_html__('آخرین نتیجهٔ رسمی', 'ebi-campaign') . '</h2>';
		$html .= self::render_winner_card($result, false);
		$html .= '</section>';
		return $html;
	}

	/**
	 * کارت برنده.
	 *
	 * @param array<string,mixed> $winner      دادهٔ برنده از سرویس مرکزی.
	 * @param bool                $with_round  نمایش شمارهٔ دوره.
	 * @return string
	 */
	private static function render_winner_card(array $winner, bool $with_round = true): string {
		$round   = isset($winner['roundNo']) ? (int) $winner['roundNo'] : 0;
		$mobile  = isset($winner['mobileMasked']) ? (string) $winner['mobileMasked'] : '';
		$name    = isset($winner['displayName']) ? (string) $winner['displayName'] : '';
		$image   = isset($winner['imageUrl']) ? (string) $winner['imageUrl'] : '';
		$drawn   = isset($winner['drawnAt']) ? (string) $winner['drawnAt'] : '';
		$prize   = isset($winner['prizeToman']) ? (int) $winner['prizeToman'] : 0;
		$verified = isset($winner['verificationStatus']) ? (string) $winner['verificationStatus'] : '';
		$eligible = isset($winner['eligibleCount']) ? (int) $winner['eligibleCount'] : 0;
		$entries  = isset($winner['entryCount']) ? (int) $winner['entryCount'] : 0;

		$html  = '<article class="ebi-winner-card">';
		if ($image !== '') {
			$html .= '<figure class="ebi-winner-card__figure"><img src="' . esc_url($image)
				. '" alt="' . esc_attr__('تصویر برنده', 'ebi-campaign') . '" loading="lazy" decoding="async" /></figure>';
		}
		$html .= '<div class="ebi-winner-card__body">';
		if ($with_round && $round > 0) {
			$html .= '<h3 class="ebi-winner-card__round">' . esc_html(sprintf(
				/* translators: %s: شمارهٔ دوره. */
				__('برندهٔ دورهٔ %s', 'ebi-campaign'),
				number_format_i18n($round)
			)) . '</h3>';
		}
		if ($name !== '') {
			$html .= '<p class="ebi-winner-card__name">' . esc_html($name) . '</p>';
		}
		if ($mobile !== '') {
			$html .= '<p class="ebi-winner-card__mobile">' . esc_html(sprintf(
				/* translators: %s: شمارهٔ سانسورشدهٔ برنده. */
				__('شمارهٔ سانسورشده: %s', 'ebi-campaign'),
				$mobile
			)) . '</p>';
		}
		if ($prize > 0) {
			$html .= '<p class="ebi-winner-card__prize">' . esc_html(sprintf(
				/* translators: %s: مبلغ جایزه. */
				__('جایزه: %s تومان', 'ebi-campaign'),
				number_format_i18n($prize)
			)) . '</p>';
		}
		if ($drawn !== '') {
			$html .= '<p class="ebi-winner-card__date">' . esc_html(sprintf(
				/* translators: %s: تاریخ قرعه‌کشی. */
				__('تاریخ قرعه‌کشی: %s', 'ebi-campaign'),
				$drawn
			)) . '</p>';
		}
		if ($entries > 0) {
			$html .= '<p class="ebi-winner-card__stats">' . esc_html(sprintf(
				/* translators: ۱: تعداد ورودی‌ها، ۲: تعداد ورودی واجد شرایط. */
				__('%1$s ورودی قرعه‌کشی، %2$s ورودی واجد شرایط', 'ebi-campaign'),
				number_format_i18n($entries),
				number_format_i18n($eligible)
			)) . '</p>';
		}
		if ('recomputable' === $verified) {
			$html .= '<p class="ebi-verified">' . esc_html__('این نتیجه با تعهد رمزنگاری‌شده و Seed افشاشده قابل بازتولید است.', 'ebi-campaign') . '</p>';
		}
		$html .= '</div></article>';
		return $html;
	}

	/**
	 * راهنمای عضویت در کانال بله.
	 *
	 * @return string
	 */
	private static function render_bale_guide(): string {
		$channel = (string) Ebi_Campaign_Settings::get('bale_channel', '');
		$html    = '<section class="ebi-bale">';
		$html .= '<h2 class="ebi-section-title">' . esc_html__('راهنمای شرکت در کمپین', 'ebi-campaign') . '</h2>';
		$html .= '<ol class="ebi-bale__steps">';
		$html .= '<li>' . esc_html__('با شمارهٔ موبایل خود در فروشگاه خرید کنید تا فاکتور شما در شمارندهٔ کمپین ثبت شود.', 'ebi-campaign') . '</li>';
		$html .= '<li>' . esc_html__('برای شرکت در قرعه‌کشی، عضویت شمارهٔ خود در کانال فروشگاه در پیام‌رسان بله الزامی است.', 'ebi-campaign') . '</li>';
		$html .= '<li>' . esc_html__('عضویت شما در زمان قرعه‌کشی از سوی سرور بررسی می‌شود؛ شماره‌ای که عضویتش تأیید نشود در قرعه‌کشی شرکت نمی‌کند.', 'ebi-campaign') . '</li>';
		$html .= '</ol>';
		if ($channel !== '') {
			$html .= '<p><a class="ebi-button" href="' . esc_url($channel) . '" rel="noopener noreferrer" target="_blank">'
				. esc_html__('عضویت در کانال بله', 'ebi-campaign') . '</a></p>';
		}
		$html .= '</section>';
		return $html;
	}

	/**
	 * قوانین عمومی کمپین.
	 *
	 * @return string
	 */
	private static function render_rules(): string {
		$support = (string) Ebi_Campaign_Settings::get('support_text', '');
		$html    = '<section class="ebi-rules">';
		$html .= '<h2 class="ebi-section-title">' . esc_html__('قوانین و شرایط کمپین', 'ebi-campaign') . '</h2>';
		$html .= '<ul>';
		$html .= '<li>' . esc_html__('هر فاکتور معتبر فروشگاه، یک ورودی مستقل قرعه‌کشی است.', 'ebi-campaign') . '</li>';
		$html .= '<li>' . esc_html__('شانس هر ورودی بر اساس امتیاز همان فاکتور تعیین می‌شود.', 'ebi-campaign') . '</li>';
		$html .= '<li>' . esc_html__('هر ۱۰۰۰ فاکتور معتبر یک دوره را تکمیل می‌کند و هر دوره یک برنده دارد.', 'ebi-campaign') . '</li>';
		$html .= '<li>' . esc_html__('فاکتورهای تکراری و ابطال‌شده در شمارنده لحاظ نمی‌شوند.', 'ebi-campaign') . '</li>';
		$html .= '<li>' . esc_html__('نتیجهٔ قرعه‌کشی با تعهد رمزنگاری‌شده ثبت و پس از اعلام، قابل بازتولید است.', 'ebi-campaign') . '</li>';
		$html .= '</ul>';
		if ($support !== '') {
			$html .= '<p class="ebi-support">' . esc_html(sprintf(
				/* translators: %s: نام یا راه ارتباطی پشتیبانی. */
				__('پرسش‌ها و پیگیری جایزه: %s', 'ebi-campaign'),
				$support
			)) . '</p>';
		}
		$html .= '</section>';
		return $html;
	}

	/**
	 * نوار پیشرفت با نشانه‌گذاری دسترس‌پذیر.
	 *
	 * @param float $percent درصد پیشرفت.
	 * @param int   $count   مقدار فعلی.
	 * @param int   $target  مقدار هدف.
	 * @return string
	 */
	private static function render_bar(float $percent, int $count = 0, int $target = 0): string {
		$percent = max(0.0, min(100.0, $percent));
		$label   = $target > 0
			? sprintf(
				/* translators: ۱: تعداد فعلی، ۲: تعداد هدف. */
				__('%1$s از %2$s فاکتور', 'ebi-campaign'),
				number_format_i18n($count),
				number_format_i18n($target)
			)
			: __('درصد تکمیل دوره', 'ebi-campaign');

		$html  = '<div class="ebi-bar" role="progressbar" aria-valuemin="0" aria-valuemax="100" aria-valuenow="'
			. esc_attr((string) round($percent, 1)) . '" aria-label="' . esc_attr($label) . '">';
		$html .= '<span class="ebi-bar__fill" style="width:' . esc_attr((string) round($percent, 2)) . '%"></span>';
		$html .= '</div>';
		$html .= '<p class="ebi-bar__caption">' . esc_html(number_format_i18n(round($percent, 1)) . '٪ ' . __('تکمیل دوره', 'ebi-campaign')) . '</p>';
		return $html;
	}

	/**
	 * صفحه‌بندی فهرست برندگان.
	 *
	 * @param array<string,mixed> $data     پاسخ سرویس.
	 * @param int                 $page     صفحهٔ فعلی.
	 * @param int                 $per_page تعداد در صفحه.
	 * @param int                 $round    فیلتر دوره.
	 * @return string
	 */
	private static function render_pagination(array $data, int $page, int $per_page, int $round): string {
		$total   = isset($data['total']) ? (int) $data['total'] : 0;
		$hasmore = !empty($data['hasMore']);
		if ($total <= $per_page && $page === 1 && !$hasmore) {
			return '';
		}

		$base = remove_query_arg(array(self::PAGE_QUERY), self::current_url());
		$args = array(self::PAGE_QUERY => '%#%');
		if ($round > 0) {
			$args[ self::ROUND_QUERY ] = $round;
		}

		$html = '<nav class="ebi-pagination" aria-label="' . esc_attr__('صفحه‌بندی برندگان', 'ebi-campaign') . '">';
		if ($page > 1) {
			$html .= '<a class="ebi-pagination__link" href="' . esc_url(str_replace('%#%', (string) ($page - 1), add_query_arg($args, $base))) . '">'
				. esc_html__('صفحهٔ قبلی', 'ebi-campaign') . '</a>';
		}
		$html .= '<span class="ebi-pagination__info">' . esc_html(sprintf(
			/* translators: %s: شمارهٔ صفحه. */
			__('صفحهٔ %s', 'ebi-campaign'),
			number_format_i18n($page)
		)) . '</span>';
		if ($hasmore) {
			$html .= '<a class="ebi-pagination__link" href="' . esc_url(str_replace('%#%', (string) ($page + 1), add_query_arg($args, $base))) . '">'
				. esc_html__('صفحهٔ بعدی', 'ebi-campaign') . '</a>';
		}
		$html .= '</nav>';
		return $html;
	}

	/**
	 * کارت خطای ارتباط: هیچ عدد جعلی نمایش داده نمی‌شود.
	 *
	 * @param WP_Error $error خطا.
	 * @return string
	 */
	private static function render_error(WP_Error $error): string {
		$html  = '<div class="ebi-error" role="status">';
		$html .= '<p class="ebi-error__title">' . esc_html__('اطلاعات کمپین در این لحظه در دسترس نیست.', 'ebi-campaign') . '</p>';
		$html .= '<p class="ebi-error__detail">' . esc_html($error->get_error_message()) . '</p>';
		$html .= '<p class="ebi-error__hint">' . esc_html__('این پیام به‌معنای صفر بودن شمارنده نیست؛ لطفاً کمی بعد صفحه را دوباره ببینید.', 'ebi-campaign') . '</p>';
		$html .= '</div>';
		return $html;
	}

	/**
	 * برچسب فارسی وضعیت.
	 *
	 * @param string $state وضعیت سرور.
	 * @return string
	 */
	private static function state_label(string $state): string {
		$labels = array(
			'Collecting'      => __('در حال جمع‌آوری فاکتورها', 'ebi-campaign'),
			'DrawPending'     => __('دوره تکمیل شده؛ در انتظار قرعه‌کشی', 'ebi-campaign'),
			'RequiresReview'  => __('نتیجهٔ دوره نیازمند بررسی مدیریتی است', 'ebi-campaign'),
			'ResultPublished' => __('نتیجهٔ رسمی اعلام شده است', 'ebi-campaign'),
			'NoActiveRound'   => __('کمپین هنوز آغاز نشده است', 'ebi-campaign'),
		);
		return isset($labels[ $state ]) ? $labels[ $state ] : '';
	}

	/**
	 * آدرس صفحهٔ جاری بدون پارامترهای تکراری.
	 *
	 * @return string
	 */
	private static function current_url(): string {
		$path = isset($_SERVER['REQUEST_URI']) ? (string) $_SERVER['REQUEST_URI'] : '/'; // phpcs:ignore WordPress.Security.ValidatedSanitizedInput.InputNotSanitized
		return esc_url_raw(home_url($path));
	}
}