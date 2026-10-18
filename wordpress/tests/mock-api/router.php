<?php
/**
 * سرویس مرکزی جعلی برای آزمون افزونه.
 *
 * همان شکل پاسخ‌های واقعی سرویس را برمی‌گرداند تا افزونه در محیطی قطعی و
 * بی‌شبکه آزمون شود. اجرا:
 *   php -S 127.0.0.1:8099 bareindex.php
 *
 * وضعیت پاسخ‌ها از فایل state.json خوانده می‌شود و همهٔ فراخوانی‌ها در
 * calls.log ثبت می‌شوند تا رفتار کش و احراز هویت قابل بررسی باشد.
 */

$dir    = __DIR__;
$state  = json_decode((string) file_get_contents($dir . '/state.json'), true);
$state  = is_array($state) ? $state : array();
$path   = (string) parse_url($_SERVER['REQUEST_URI'] ?? '/', PHP_URL_PATH);
$method = strtoupper($_SERVER['REQUEST_METHOD'] ?? 'GET');

file_put_contents(
	$dir . '/calls.log',
	$method . ' ' . $path . "\n",
	FILE_APPEND
);

header('Content-Type: application/json; charset=utf-8');

/**
 * پاسخ JSON و پایان.
 *
 * @param array<string,mixed> $data بدنه.
 * @param int                 $code کد وضعیت.
 */
function mock_reply(array $data, int $code = 200): void {
	http_response_code($code);
	echo wp_json_encode_like($data);
	exit;
}

/**
 * جایگزین سبک wp_json_encode برای محیط بدون وردپرس.
 *
 * @param array<string,mixed> $data داده.
 * @return string
 */
function wp_json_encode_like(array $data): string {
	return (string) json_encode($data, JSON_UNESCAPED_UNICODE);
}

if (str_starts_with($path, '/api/v1/public/')) {
	if (!empty($state['fail_public'])) {
		mock_reply(array('code' => 'service_unavailable', 'message' => 'سرویس موقتاً در دسترس نیست.'), 503);
	}

	if ('/api/v1/public/campaign/current' === $path) {
		mock_reply($state['campaign'] ?? array());
	}
	if ('/api/v1/public/winners' === $path) {
		mock_reply($state['winners'] ?? array('page' => 1, 'perPage' => 10, 'total' => 0, 'hasMore' => false, 'winners' => array()));
	}
	if (preg_match('#^/api/v1/public/winners/(\d+)$#', $path, $m)) {
		$round = (int) $m[1];
		foreach (($state['winners']['winners'] ?? array()) as $winner) {
			if ((int) ($winner['roundNo'] ?? 0) === $round) {
				mock_reply($winner);
			}
		}
		mock_reply(array('code' => 'not_found', 'message' => 'برنده پیدا نشد.'), 404);
	}
	if (preg_match('#^/api/v1/public/lottery/(\d+)/(receipt|verify|entries)$#', $path, $m)) {
		$key = 'lottery_' . $m[2];
		mock_reply($state[ $key ] ?? array('runId' => (int) $m[1]));
	}
	mock_reply(array('code' => 'not_found', 'message' => 'مسیر عمومی ناشناخته.'), 404);
}

if (str_starts_with($path, '/api/v1/admin/')) {
	$expected = (string) ($state['admin_key'] ?? 'mock-admin-key');
	$provided = (string) ($_SERVER['HTTP_X_ADMIN_KEY'] ?? '');
	if ($provided !== $expected) {
		mock_reply(array('code' => 'admin_unauthorized', 'message' => 'کلید مدیر نامعتبر است.'), 401);
	}

	if ('POST' === $method) {
		if (str_contains($path, '/publish')) {
			mock_reply(array('runId' => 1, 'publishApproved' => true, 'status' => 'Finalized'));
		}
		if (str_contains($path, '/image')) {
			mock_reply(array('runId' => 1, 'winnerImageApproved' => true, 'winnerImageUrl' => 'https://localhost/hypermarket/wp-content/uploads/winner.jpg'));
		}
		if (str_contains($path, '/lottery') && str_ends_with($path, '/select')) {
			mock_reply(array('runId' => 1, 'status' => 'Selected'));
		}
		if (str_contains($path, '/lottery') && str_ends_with($path, '/finalize')) {
			mock_reply(array('runId' => 1, 'status' => 'Finalized'));
		}
		if (str_ends_with($path, '/rounds/start')) {
			mock_reply(array('roundId' => 7, 'roundNo' => 3, 'status' => 'Collecting', 'target' => 1000, 'count' => 0, 'remaining' => 1000));
		}
		mock_reply(array('ok' => true, 'assigned' => 2, 'message' => 'انجام شد.'));
	}

	if ('/api/v1/admin/rounds' === $path) {
		mock_reply($state['rounds'] ?? array());
	}
	if ('/api/v1/admin/held' === $path) {
		mock_reply($state['held'] ?? array());
	}
	if ('/api/v1/admin/invoices' === $path) {
		mock_reply($state['invoices'] ?? array());
	}
	if ('/api/v1/admin/lottery' === $path) {
		mock_reply($state['runs'] ?? array());
	}
	if (preg_match('#^/api/v1/admin/lottery/(\d+)$#', $path, $m)) {
		mock_reply($state['run_detail'] ?? array('runId' => (int) $m[1]));
	}
	mock_reply(array('code' => 'not_found', 'message' => 'مسیر مدیریتی ناشناخته.'), 404);
}

if ('/livez' === $path) {
	mock_reply(array('ok' => true));
}

mock_reply(array('code' => 'not_found', 'message' => 'ناشناخته.'), 404);