<?php
/**
 * کاوشگر عملیات مدیریتی در فرایند جداگانه.
 *
 * چون handler واقعی پس از اجرا با wp_safe_redirect خارج می‌شود، هر عملیات در یک
 * فرایند مستقل اجرا می‌شود و نتیجه (پیام موقت یا خطای مجوز) در خروجی چاپ می‌گردد.
 *
 * استفاده: php action-probe.php op=start_round user=1 nonce=valid confirm=1
 */

$_SERVER['HTTP_HOST']   = 'localhost';
$_SERVER['REQUEST_URI'] = '/wp-admin/admin-post.php';
$_SERVER['SERVER_NAME'] = 'localhost';
$_SERVER['REQUEST_METHOD'] = 'POST';

$root = getenv('WP_ROOT') ?: 'C:/xampp/htdocs/hypermarket';

/**
 * خواندن آرگومان‌های خط فرمان به شکل key=value.
 *
 * @return array<string,string>
 */
function probe_args(): array {
	$args = array();
	foreach (array_slice($GLOBALS['argv'], 1) as $raw) {
		$parts = explode('=', (string) $raw, 2);
		if (count($parts) === 2) {
			$args[ $parts[0] ] = $parts[1];
		}
	}
	return $args;
}

$args    = probe_args();
$user_id = isset($args['user']) ? (int) $args['user'] : 0;
$op      = isset($args['op']) ? (string) $args['op'] : '';
$nonce   = isset($args['nonce']) ? (string) $args['nonce'] : '';

define('WP_USE_THEMES', false);
require $root . '/wp-load.php';
require_once ABSPATH . 'wp-admin/includes/plugin.php';

$plugin = 'ebi-campaign/ebi-campaign.php';
if (!is_plugin_active($plugin)) {
	require_once dirname(__DIR__) . '/ebi-campaign/ebi-campaign.php';
	ebi_campaign_bootstrap();
}

wp_set_current_user($user_id);

$_POST = array(
	'action'  => Ebi_Campaign_Admin::ACTION,
	'ebi_op'  => $op,
	'confirm' => !empty($args['confirm']) ? '1' : '',
);
foreach (array('round_id', 'run_id', 'invoice_id', 'verdict', 'reason', 'limit', 'template_id', 'recipient_source', 'test_mobile') as $field) {
	if (isset($args[ $field ])) {
		$_POST[ $field ] = (string) $args[ $field ];
	}
}

// check_admin_referer نونس را از $_REQUEST می‌خواند؛ در CLI باید دستی پر شود.
if ($nonce === 'valid') {
	$_POST['_wpnonce']    = wp_create_nonce(Ebi_Campaign_Admin::ACTION);
	$_REQUEST['_wpnonce'] = $_POST['_wpnonce'];
} elseif ($nonce !== '') {
	$_POST['_wpnonce']    = $nonce;
	$_REQUEST['_wpnonce'] = $nonce;
}

// حالت آزمون پیامک: انتقال‌دهندهٔ جعلی که هیچ ارتباط شبکه‌ای ندارد.
if (isset($args['sms_mock']) && '1' === $args['sms_mock']) {
	add_filter('ebi_campaign_sms_transport', static function ($custom) {
		return static function (array $call) {
			echo 'SMS_CALL:' . $call['to'] . ':' . md5((string) $call['text']) . "\n";
			return array('status' => 'sent', 'recId' => 'probe-recid', 'code' => '1', 'message' => 'ok');
		};
	});
}

// خروجی HTML خطاهای wp_die را ساده و متنی می‌کنیم تا در آزمون قابل بررسی باشد.
add_filter('wp_die_handler', static function () {
	return static function ($message, $title = '', $args = array()) {
		$text = is_wp_error($message) ? $message->get_error_message() : (string) $message;
		echo "WP_DIE: " . wp_strip_all_tags($text) . "\n";
		exit(0);
	};
});

/**
 * چاپ پیام موقت پیش از خروج ناشی از هدایت.
 *
 * handler واقعی با wp_safe_redirect و exit تمام می‌شود، پس پیام باید در همان
 * لحظه چاپ شود تا در آزمون قابل خواندن باشد.
 */
function probe_print_notice(): void {
	global $user_id;
	$notice = get_transient(Ebi_Campaign_Admin::NOTICE_KEY . $user_id);
	if (is_array($notice)) {
		echo 'NOTICE[' . ($notice['type'] ?? '') . ']: ' . ($notice['text'] ?? '') . "\n";
		return;
	}
	echo "NOTICE[none]\n";
}

add_filter('wp_redirect', static function ($location) {
	probe_print_notice();
	return $location;
});

Ebi_Campaign_Admin::handle_action();

probe_print_notice();