@echo off
rem ساخت برنامه «تخفیف لحظه‌ای صندوق هولو + موبایل و امتیاز مشتریان» — فقط با .NET Framework نصب‌شده روی ویندوز
rem خروجی: bin\PosDiscount.exe و bin\SelfTest.exe (+ System.Data.SQLite.dll و x86/x64 برای دیتابیس محلی)
rem نکته: این اسکریپت با build.sh هم‌ارز است؛ هر دو باید همان خروجی را بسازند.
setlocal

set CSC=%windir%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%windir%\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist "%CSC%" (
  echo.
  echo [خطا] کامپایلر .NET Framework یافت نشد: csc.exe
  echo        Microsoft .NET Framework 4.0 یا بالاتر را نصب کنید.
  exit /b 1
)

if not exist bin mkdir bin
if not exist bin\logs mkdir bin\logs
if not exist bin\x64 mkdir bin\x64
if not exist bin\x86 mkdir bin\x86

set REFS=-r:System.dll -r:System.Core.dll -r:System.Data.dll -r:System.Drawing.dll -r:System.Windows.Forms.dll -r:System.Xml.dll -r:System.Security.dll -r:System.Web.Extensions.dll -r:lib\System.Data.SQLite.dll

set SRC=src\ApiClient.cs src\AppSettings.cs src\CashierSelectForm.cs src\ConnectionFixer.cs src\CsvLog.cs src\CustomerInvoicesForm.cs src\DashboardForm.cs src\DiscountEngine.cs src\HolooDb.cs src\InvoicePopupForm.cs src\LocalStore.cs src\Lottery.cs src\LotteryForm.cs src\Loyalty.cs src\MobileEntryForm.cs src\PointsForm.cs src\Program.cs src\SettingsForm.cs src\SyncAgent.cs src\SyncForm.cs src\Util.cs

echo 1/3  PosDiscount.exe ...
"%CSC%" -nologo -codepage:65001 -langversion:5 -target:winexe -optimize+ -win32manifest:src\app.manifest -main:PosDiscount.Program -out:bin\PosDiscount.exe %REFS% %SRC%
if errorlevel 1 goto fail

echo 2/3  SelfTest.exe ...
"%CSC%" -nologo -codepage:65001 -langversion:5 -target:exe -optimize+ -main:PosDiscount.SelfTest -out:bin\SelfTest.exe %REFS% %SRC% src\SelfTest.cs src\Phase1Tests.cs
if errorlevel 1 goto fail

echo 3/3  کپی کتابخانه دیتابیس محلی (SQLite) کنار فایل اجرایی ...
copy /y lib\System.Data.SQLite.dll bin\System.Data.SQLite.dll >nul
copy /y lib\x64\SQLite.Interop.dll bin\x64\SQLite.Interop.dll >nul
copy /y lib\x86\SQLite.Interop.dll bin\x86\SQLite.Interop.dll >nul

echo.
echo build OK  ^-^>  bin\PosDiscount.exe  ,  bin\SelfTest.exe  (+ System.Data.SQLite.dll و x86/x64)
exit /b 0

:fail
echo.
echo [خطا] کامپایل ناموفق بود. متن خطای بالا را ببینید.
exit /b 1
