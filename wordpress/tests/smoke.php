<?php
/**
 * آزمون دود: بارگذاری وردپرس محلی از خط فرمان و گزارش محیط واقعی.
 * اجرا: WP_ROOT=C:/xampp/htdocs/hypermarket php wordpress/tests/smoke.php
 */

$_SERVER['HTTP_HOST'] = $_SERVER['HTTP_HOST'] ?? 'localhost';
$_SERVER['REQUEST_URI'] = $_SERVER['REQUEST_URI'] ?? '/';
$_SERVER['SERVER_NAME'] = $_SERVER['SERVER_NAME'] ?? 'localhost';

$root = getenv('WP_ROOT') ?: 'C:/xampp/htdocs/hypermarket';
if (!file_exists($root . '/wp-load.php')) {
    fwrite(STDERR, "wp-load.php در مسیر $root پیدا نشد.\n");
    exit(2);
}

define('WP_USE_THEMES', false);
require $root . '/wp-load.php';

echo "wp_version=" . get_bloginfo('version') . "\n";
echo "php_version=" . PHP_VERSION . "\n";
echo "site_url=" . site_url() . "\n";
echo "home_url=" . home_url() . "\n";
echo "active_theme=" . wp_get_theme()->get('Name') . " (" . get_stylesheet() . ")\n";
echo "is_multisite=" . (is_multisite() ? '1' : '0') . "\n";
echo "active_plugins=" . implode(',', (array) get_option('active_plugins')) . "\n";
echo "rest_server=" . (class_exists('WP_REST_Server') ? 'yes' : 'no') . "\n";
echo "current_user_can=" . (function_exists('current_user_can') ? 'yes' : 'no') . "\n";
echo "wp_content_dir=" . WP_CONTENT_DIR . "\n";
echo "uploads=" . wp_upload_dir()['basedir'] . "\n";
echo "object_cache=" . (function_exists('wp_using_ext_object_cache') && wp_using_ext_object_cache() ? 'external' : 'none') . "\n";
echo "rest_url=" . rest_url('ebi/v1') . "\n";
echo "plugin_dir_exists=" . (is_dir(WP_CONTENT_DIR . '/plugins') ? 'yes' : 'no') . "\n";