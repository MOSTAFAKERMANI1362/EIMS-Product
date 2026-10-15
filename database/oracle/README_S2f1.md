# S2f-1 — اجرا

۱) مطمئن شو S2e در `main` است (بله، merge شده)، بعد:
```
git checkout main
git pull
git checkout -b feature/s2f1-portfolio-oracle
```
۲) محتوای `EIMS_S2f1.zip` را روی `EIMS-git` کپی کن (Replace).
۳) **دو تغییر یک‌کلمه‌ای** در `src\EIMS.Persistence.Recovery\PortfolioTransactionalStoreWave9.cs`:
```
$f = (Resolve-Path "src\EIMS.Persistence.Recovery\PortfolioTransactionalStoreWave9.cs").Path
$utf8 = New-Object System.Text.UTF8Encoding $false
$t = [System.IO.File]::ReadAllText($f)
$n = $t.Replace("private static void ValidateEligibilityCommit(", "public static void ValidateEligibilityCommit(").Replace("private static void ValidateCommandCommit(", "public static void ValidateCommandCommit(")
if ($n -eq $t) { "NO CHANGE" } else { [System.IO.File]::WriteAllText($f, $n, $utf8); "Patched" }
```
۴) بازسازی دیتابیس (V004 و verify عوض شده؛ حالا ۲۱ جدول):
```
cd database\oracle
sqlplus EIMS_OWNER@localhost:1521/FREEPDB1
@dev_rebuild_all.sql
EXIT
Select-String -Path dev_rebuild_output.txt -Pattern "FAIL|ORA-"
```
فقط خط عنوان RESET باید بیاید.
۵) build و تست (خط رمز را از خروجی‌ای که می‌فرستی حذف کن):
```
cd ..\..
dotnet build tests\EIMS.P2.OracleConformance.Tests
$env:EIMS_ORACLE_CONNECTION = "User Id=EIMS_APP;Password=رمزشما;Data Source=localhost:1521/FREEPDB1"
dotnet run --project tests\EIMS.P2.OracleConformance.Tests
```
انتظار: ۱۰۲ خط PASS برای InMemory و ۱۰۲ برای Oracle و `ALL PASS`.
