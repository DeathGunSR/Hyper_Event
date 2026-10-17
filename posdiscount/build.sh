#!/bin/sh
# ساخت برنامه «تخفیف لحظه‌ای صندوق هولو + موبایل و امتیاز مشتریان» (بدون نیاز به Visual Studio)
# خروجی: bin/PosDiscount.exe و bin/SelfTest.exe
#
# نکته: مسیرهای نسبی در خط فرمان csc باید با «\» نوشته شوند؛ نسخه قدیمی csc
# (in-box .NET Framework) مسیرهای نسبی با «/» را درست باز نمی‌کند.
#
# دیتابیس محلی: کتابخانه System.Data.SQLite (در پوشه lib) به همراه SQLite.Interop.dll
# برای هر دو معماری 32 و 64 بیتی کنار فایل اجرایی کپی می‌شود.
set -e

CSC="/c/Windows/Microsoft.NET/Framework64/v4.0.30319/csc.exe"
if [ ! -x "$CSC" ]; then
  CSC="/c/Windows/Microsoft.NET/Framework/v4.0.30319/csc.exe"
fi
if [ ! -x "$CSC" ]; then
  echo "کامپایلر .NET Framework (csc.exe) پیدا نشد."
  exit 1
fi

REFS='-r:System.dll -r:System.Core.dll -r:System.Data.dll -r:System.Drawing.dll -r:System.Windows.Forms.dll -r:System.Xml.dll -r:System.Security.dll -r:System.Web.Extensions.dll -r:lib\System.Data.SQLite.dll'

SRC='src\ApiClient.cs src\AppSettings.cs src\CashierSelectForm.cs src\ConnectionFixer.cs src\CsvLog.cs src\CustomerInvoicesForm.cs src\DashboardForm.cs src\DiscountEngine.cs src\HolooDb.cs src\InvoicePopupForm.cs src\LocalStore.cs src\Lottery.cs src\LotteryForm.cs src\Loyalty.cs src\MobileEntryForm.cs src\PointsForm.cs src\Program.cs src\SettingsForm.cs src\SyncAgent.cs src\SyncForm.cs src\Util.cs'

GUI_SRC="$SRC"
ALL_SRC="$SRC src\\SelfTest.cs src\\Phase1Tests.cs src\\Phase2Tests.cs"

mkdir -p bin/logs bin/x64 bin/x86

echo "1/3  PosDiscount.exe (رابط گرافیکی ویندوز)"
"$CSC" -nologo -codepage:65001 -langversion:5 -target:winexe -optimize+ \
  '-win32manifest:src\app.manifest' -main:PosDiscount.Program '-out:bin\PosDiscount.exe' \
  $REFS $GUI_SRC

echo "2/3  SelfTest.exe (تست بدون رابط گرافیکی)"
"$CSC" -nologo -codepage:65001 -langversion:5 -target:exe -optimize+ \
  -main:PosDiscount.SelfTest '-out:bin\SelfTest.exe' \
  $REFS $ALL_SRC

echo "3/3  کپی کتابخانه دیتابیس محلی (SQLite) کنار فایل اجرایی"
cp -f lib/System.Data.SQLite.dll bin/System.Data.SQLite.dll
cp -f lib/x64/SQLite.Interop.dll bin/x64/SQLite.Interop.dll
cp -f lib/x86/SQLite.Interop.dll bin/x86/SQLite.Interop.dll

echo "build OK → bin/PosDiscount.exe , bin/SelfTest.exe (+ System.Data.SQLite.dll و x86/x64)"
