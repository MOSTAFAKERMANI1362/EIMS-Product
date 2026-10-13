# S2e — اجرا

۱) مطمئن شو S2d در `main` merge شده، بعد:
```
git checkout main
git pull
git checkout -b feature/s2e-g04-final-decision-oracle
```
۲) محتوای `EIMS_S2e.zip` را روی `EIMS-git` کپی کن (Replace).
۳) **تغییر یک‌کلمه‌ای سوم** در `src\EIMS.Persistence.Recovery\G04FinalDecisionTransactionalStore.cs`:
```
$f = (Resolve-Path "src\EIMS.Persistence.Recovery\G04FinalDecisionTransactionalStore.cs").Path
$utf8 = New-Object System.Text.UTF8Encoding $false
$t = [System.IO.File]::ReadAllText($f)
$n = $t.Replace("private static void ValidateCommit(", "public static void ValidateCommit(")
if ($n -eq $t) { "NO CHANGE" } else { [System.IO.File]::WriteAllText($f, $n, $utf8); "Patched" }
```
۴) بازسازی دیتابیس (V003 و اسکریپت‌های verify عوض شده‌اند؛ حالا ۲۰ جدول):
```
cd database\oracle
sqlplus EIMS_OWNER@localhost:1521/FREEPDB1
@dev_rebuild_all.sql
EXIT
Select-String -Path dev_rebuild_output.txt -Pattern "FAIL|ORA-"
```
فقط خط عنوان RESET باید بیاید.
۵) build و تست (رمز را در خروجی‌هایی که می‌فرستی حذف کن):
```
cd ..\..
dotnet build tests\EIMS.P2.OracleConformance.Tests
$env:EIMS_ORACLE_CONNECTION = "User Id=EIMS_APP;Password=رمزشما;Data Source=localhost:1521/FREEPDB1"
dotnet run --project tests\EIMS.P2.OracleConformance.Tests
```
انتظار: ۷۸ خط PASS برای InMemory و ۷۸ برای Oracle و `ALL PASS`.
