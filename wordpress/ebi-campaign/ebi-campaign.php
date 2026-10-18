<?php
/**
 * Plugin Name:       Ebi Campaign (کمپین فروشگاه ابی)
 * Plugin URI:        https://ebimarket.ir/
 * Description:       اتصال سایت فروشگاه به سرویس مرکزی کمپین: شمارندهٔ واقعی فاکتورها، صفحهٔ کمپین، آرشیو رسمی برندگان، پنل مدیریت دوره‌ها، قرعه‌کشی و انتشار نتیجه.
 * Version:           0.3.0
 * Requires at least: 6.4
 * Requires PHP:      7.4
 * Author:            Ebi Market
 * License:           GPL-2.0-or-later
 * License URI:       https://www.gnu.org/licenses/gpl-2.0.html
 * Text Domain:       ebi-campaign
 * Domain Path:       /languages
 *
 * @package EbiCampaign
 */

defined('ABSPATH') || exit;

define('EBI_CAMPAIGN_VERSION', '0.3.0');
define('EBI_CAMPAIGN_FILE', __FILE__);
define('EBI_CAMPAIGN_DIR', plugin_dir_path(__FILE__));
define('EBI_CAMPAIGN_URL', plugin_dir_url(__FILE__));
define('EBI_CAMPAIGN_OPTION', 'ebi_campaign_settings');
define('EBI_CAMPAIGN_VERSION_OPTION', 'ebi_campaign_schema_version');
define('EBI_CAMPAIGN_REST_NAMESPACE', 'ebi/v1');

require_once EBI_CAMPAIGN_DIR . 'includes/class-ebi-campaign-settings.php';
require_once EBI_CAMPAIGN_DIR . 'includes/class-ebi-campaign-api.php';
require_once EBI_CAMPAIGN_DIR . 'includes/class-ebi-campaign-images.php';
require_once EBI_CAMPAIGN_DIR . 'includes/class-ebi-campaign-pages.php';
require_once EBI_CAMPAIGN_DIR . 'includes/class-ebi-campaign-admin.php';

/**
 * نقش‌ها و توانایی‌های افزونه.
 *
 * هر توانایی مستقل از دیگری به سرویس مرکزی نگاشت می‌شود تا پنهان‌کردن دکمه
 * جایگزین کنترل سرور نشود، بلکه لایهٔ نمایش هم همان تفکیک را نشان دهد.
 *
 * @return array<string,string> نگاشت توانایی وردپرس به کلید نقش سرویس مرکزی.
 */
function ebi_campaign_capability_map(): array {
	return array(
		'ebi_view_campaign'   => '',                 // فقط خواندن وضعیت و فهرست‌ها.
		'ebi_manage_campaign' => 'campaign.manage',  // شروع/بستن دوره، تخصیص فاکتور انتظار.
		'ebi_review_invoice'  => 'review.decide',    // تصمیم روی فاکتورهای نیازمند بررسی.
		'ebi_run_lottery'     => 'lottery.run',      // تثبیت مجموعه، انتخاب و نهایی‌سازی.
		'ebi_publish_result'  => 'lottery.publish',  // انتشار عمومی نتیجه و تصویر برنده.
		'ebi_view_private'    => 'privacy.contact',  // شمارهٔ کامل برنده برای تماس.
	);
}

/**
 * فعال‌سازی: گزینه‌های پیش‌فرض، توانایی‌ها و نسخه‌بندی اسکیما.
 */
function ebi_campaign_activate(): void {
	Ebi_Campaign_Settings::install_defaults();

	$role = get_role('administrator');
	if ($role) {
		foreach (array_keys(ebi_campaign_capability_map()) as $capability) {
			$role->add_cap($capability);
		}
	}

	update_option(EBI_CAMPAIGN_VERSION_OPTION, EBI_CAMPAIGN_VERSION, false);
	Ebi_Campaign_Api::flush_cache();
}
register_activation_hook(__FILE__, 'ebi_campaign_activate');

/**
 * غیرفعال‌سازی: پاک‌کردن کش محلی؛ داده‌ها و تنظیمات حفظ می‌شوند.
 */
function ebi_campaign_deactivate(): void {
	Ebi_Campaign_Api::flush_cache();
}
register_deactivation_hook(__FILE__, 'ebi_campaign_deactivate');

/**
 * بارگذاری ترجمه و راه‌اندازی اجزا.
 */
function ebi_campaign_bootstrap(): void {
	load_plugin_textdomain('ebi-campaign', false, dirname(plugin_basename(__FILE__)) . '/languages');

	Ebi_Campaign_Settings::maybe_upgrade();
	Ebi_Campaign_Pages::init();
	Ebi_Campaign_Admin::init();
}
add_action('plugins_loaded', 'ebi_campaign_bootstrap');

/**
 * دکمهٔ تنظیمات در فهرست افزونه‌ها.
 *
 * @param array<int,string> $links پیوندهای موجود.
 * @return array<int,string> پیوندهای به‌روزشده.
 */
function ebi_campaign_action_links(array $links): array {
	$url = admin_url('admin.php?page=ebi-campaign-settings');
	array_unshift($links, '<a href="' . esc_url($url) . '">' . esc_html__('تنظیمات', 'ebi-campaign') . '</a>');
	return $links;
}
add_filter('plugin_action_links_' . plugin_basename(__FILE__), 'ebi_campaign_action_links');