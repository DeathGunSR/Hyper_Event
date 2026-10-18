<?php
/**
 * تصویر برندگان: ذخیره در رسانهٔ وردپرس و انتشار کنترل‌شده در سرویس مرکزی.
 *
 * فایل روی سایت میزبانی می‌شود و آدرس آن به سرویس مرکزی فرستاده می‌شود تا خروجی
 * عمومی فقط با تأیید صریح تصویر منتشر شود. هیچ دادهٔ شخصی از مشتری خوانده نمی‌شود.
 *
 * @package EbiCampaign
 */

defined('ABSPATH') || exit;

/**
 * مدیریت تصویر برنده.
 */
final class Ebi_Campaign_Images {

	public const MAX_BYTES   = 2097152; // ۲ مگابایت.
	public const META_RUN    = '_ebi_campaign_run';
	public const META_STATE  = '_ebi_campaign_image_state';

	private const ALLOWED_MIME = array('image/jpeg', 'image/png', 'image/webp');

	/**
	 * پردازش فایل بارگذاری‌شده و انتشار آن در سرویس مرکزی.
	 *
	 * @param array<string,mixed> $file   یک عضو از $_FILES.
	 * @param int                 $run_id شناسهٔ فراگرفت.
	 * @param string              $actor  هویت اقدام‌کننده.
	 * @return array<string,mixed>|WP_Error
	 */
	public static function upload_and_publish(array $file, int $run_id, string $actor) {
		$validated = self::validate_file($file);
		if (is_wp_error($validated)) {
			return $validated;
		}

		require_once ABSPATH . 'wp-admin/includes/file.php';
		require_once ABSPATH . 'wp-admin/includes/media.php';
		require_once ABSPATH . 'wp-admin/includes/image.php';

		$_FILES['ebi_campaign_image'] = $file;
		$attachment_id                = media_handle_upload('ebi_campaign_image', 0, array(), array('test_form' => false));
		if (is_wp_error($attachment_id)) {
			return new WP_Error('ebi_upload_failed', __('ذخیرهٔ تصویر در رسانهٔ وردپرس انجام نشد.', 'ebi-campaign'));
		}

		$url = wp_get_attachment_url((int) $attachment_id);
		if (!$url) {
			return new WP_Error('ebi_upload_failed', __('آدرس تصویر ذخیره‌شده پیدا نشد.', 'ebi-campaign'));
		}

		$result = self::publish_url($run_id, (string) $url, $actor);
		if (is_wp_error($result)) {
			// فایل در رسانه می‌ماند، ولی وضعیت «منتشرنشده» ثبت می‌شود تا ناهماهنگی پنهان نماند.
			self::mark((int) $attachment_id, $run_id, 'local_only');
			return $result;
		}

		self::mark((int) $attachment_id, $run_id, 'published');
		return array(
			'attachment_id' => (int) $attachment_id,
			'url'           => (string) $url,
			'run'           => $result,
		);
	}

	/**
	 * انتشار آدرس تصویر در سرویس مرکزی با تأیید صریح.
	 *
	 * @param int    $run_id شناسهٔ فراگرفت.
	 * @param string $url    آدرس تصویر روی همین سایت.
	 * @param string $actor  هویت اقدام‌کننده.
	 * @return array<string,mixed>|WP_Error
	 */
	public static function publish_url(int $run_id, string $url, string $actor) {
		if (!self::is_local_url($url)) {
			return new WP_Error(
				'ebi_image_not_local',
				__('تصویر برنده باید در رسانهٔ همین سایت میزبانی شود.', 'ebi-campaign')
			);
		}
		$checked = self::validate_url($url);
		if (is_wp_error($checked)) {
			return $checked;
		}

		return Ebi_Campaign_Api::admin_request(
			'lottery.publish',
			'POST',
			'lottery/' . max(1, $run_id) . '/image',
			array(
				'actor'    => $actor,
				'imageUrl' => $url,
				'approved' => true,
			)
		);
	}

	/**
	 * حذف تصویر برنده از خروجی عمومی (فایل در رسانه می‌ماند).
	 *
	 * @param int    $run_id شناسهٔ فراگرفت.
	 * @param string $actor  هویت اقدام‌کننده.
	 * @return array<string,mixed>|WP_Error
	 */
	public static function withdraw(int $run_id, string $actor) {
		return Ebi_Campaign_Api::admin_request(
			'lottery.publish',
			'POST',
			'lottery/' . max(1, $run_id) . '/image',
			array(
				'actor'    => $actor,
				'imageUrl' => null,
				'approved' => false,
			)
		);
	}

	/**
	 * اعتبارسنجی فایل بارگذاری‌شده.
	 *
	 * @param array<string,mixed> $file عضو $_FILES.
	 * @return true|WP_Error
	 */
	public static function validate_file(array $file) {
		$check = self::validate_upload($file);
		if (is_wp_error($check)) {
			return $check;
		}
		return self::validate_image_bytes((string) $file['tmp_name'], (string) ($file['name'] ?? ''));
	}

	/**
	 * کنترل‌های مبدأ بارگذاری: خطای انتقال، حجم و بارگذاری واقعی HTTP.
	 *
	 * @param array<string,mixed> $file عضو $_FILES.
	 * @return true|WP_Error
	 */
	public static function validate_upload(array $file) {
		if (empty($file) || !isset($file['tmp_name']) || !isset($file['error'])) {
			return new WP_Error('ebi_image_missing', __('فایلی برای بارگذاری انتخاب نشده است.', 'ebi-campaign'));
		}
		if ((int) $file['error'] !== UPLOAD_ERR_OK) {
			return new WP_Error('ebi_image_upload_error', __('بارگذاری فایل ناتمام ماند. دوباره تلاش کنید.', 'ebi-campaign'));
		}
		if (!empty($file['size']) && (int) $file['size'] > self::MAX_BYTES) {
			return new WP_Error('ebi_image_too_large', __('حجم تصویر باید کمتر از ۲ مگابایت باشد.', 'ebi-campaign'));
		}
		if (!is_uploaded_file((string) $file['tmp_name'])) {
			return new WP_Error('ebi_image_spoofed', __('فایل بارگذاری‌شده معتبر نیست.', 'ebi-campaign'));
		}
		return true;
	}

	/**
	 * کنترل محتوای واقعی تصویر (مستقل از مبدأ فایل).
	 *
	 * @param string $tmp_name مسیر فایل.
	 * @param string $name     نام اصلی فایل.
	 * @return true|WP_Error
	 */
	public static function validate_image_bytes(string $tmp_name, string $name = '') {
		if ($tmp_name === '' || !is_readable($tmp_name)) {
			return new WP_Error('ebi_image_invalid', __('فایل انتخابی یک تصویر معتبر نیست.', 'ebi-campaign'));
		}
		$info = @getimagesize($tmp_name); // phpcs:ignore WordPress.PHP.NoSilencedErrors.Discouraged
		if (!is_array($info) || empty($info['mime'])) {
			return new WP_Error('ebi_image_invalid', __('فایل انتخابی یک تصویر معتبر نیست.', 'ebi-campaign'));
		}
		if (!in_array((string) $info['mime'], self::ALLOWED_MIME, true)) {
			return new WP_Error('ebi_image_type', __('فقط تصویر JPG، PNG یا WebP پذیرفته می‌شود.', 'ebi-campaign'));
		}
		$ext = strtolower((string) pathinfo($name, PATHINFO_EXTENSION));
		if (!in_array($ext, array('jpg', 'jpeg', 'png', 'webp'), true)) {
			return new WP_Error('ebi_image_type', __('پسوند فایل تصویر مجاز نیست.', 'ebi-campaign'));
		}
		return true;
	}

	/**
	 * اعتبارسنجی آدرس تصویر (بدون دسترسی شبکه‌ای).
	 *
	 * @param string $url آدرس.
	 * @return true|WP_Error
	 */
	public static function validate_url(string $url) {
		$url = esc_url_raw($url);
		if ($url === '' || strlen($url) > 400) {
			return new WP_Error('ebi_image_url', __('آدرس تصویر معتبر نیست.', 'ebi-campaign'));
		}
		$path = strtolower((string) wp_parse_url($url, PHP_URL_PATH));
		$ok   = false;
		foreach (array('.jpg', '.jpeg', '.png', '.webp') as $ext) {
			if (substr($path, -strlen($ext)) === $ext) {
				$ok = true;
				break;
			}
		}
		if (!$ok) {
			return new WP_Error('ebi_image_url', __('پسوند تصویر باید jpg، jpeg، png یا webp باشد.', 'ebi-campaign'));
		}
		return true;
	}

	/**
	 * آدرس تصویر روی همین سایت است؟
	 *
	 * @param string $url آدرس.
	 * @return bool
	 */
	public static function is_local_url(string $url): bool {
		$host = strtolower((string) wp_parse_url($url, PHP_URL_HOST));
		$home = strtolower((string) wp_parse_url(home_url(), PHP_URL_HOST));
		return $host !== '' && $host === $home;
	}

	/**
	 * ثبت وضعیت تصویر روی پیوست رسانه.
	 *
	 * @param int    $attachment_id شناسهٔ پیوست.
	 * @param int    $run_id        شناسهٔ فراگرفت.
	 * @param string $state         published | local_only.
	 */
	private static function mark(int $attachment_id, int $run_id, string $state): void {
		update_post_meta($attachment_id, self::META_RUN, $run_id);
		update_post_meta($attachment_id, self::META_STATE, $state);
	}
}