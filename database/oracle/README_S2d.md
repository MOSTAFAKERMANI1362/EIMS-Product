# S2d — اجرا

۱) اول مطمئن شو S2c در `main` merge شده، بعد:
```
git checkout main
git pull
git checkout -b feature/s2d-g04-voting-oracle
```
۲) محتوای `EIMS_S2d.zip` را روی `EIMS-git` کپی کن (Replace).
۳) **دو تغییر یک‌کلمه‌ای** در `src\EIMS.Persistence.Recovery\G04VotingTransactionalStore.cs`:
```
$f = (Resolve-Path "src\EIMS.Persistence.Recovery\G04VotingTransactionalStore.cs").Path
$utf8 = New-Object System.Text.UTF8Encoding $false
$t = [System.IO.File]::ReadAllText($f)
$n = $t.Replace("private static string? ValidateCommitShape(", "public static string? ValidateCommitShape(").Replace("private static bool SameOptionalSnapshot(", "public static bool SameOptionalSnapshot(")
if ($n -eq $t) { "NO CHANGE" } else { [System.IO.File]::WriteAllText($f, $n, $utf8); "Patched" }
```
۴) بازسازی دیتابیس (V003 و اسکریپت‌های verify عوض شده‌اند؛ حالا ۱۹ جدول):
```
cd database\oracle
sqlplus EIMS_OWNER@localhost:1521/FREEPDB1
@dev_rebuild_all.sql
EXIT
Select-String -Path dev_rebuild_output.txt -Pattern "FAIL|ORA-"
```
فقط خط عنوان RESET باید بیاید.
۵) build و تست:
```
cd ..\..
dotnet build tests\EIMS.P2.OracleConformance.Tests
$env:EIMS_ORACLE_CONNECTION = "User Id=EIMS_APP;Password=رمزشما;Data Source=localhost:1521/FREEPDB1"
dotnet run --project tests\EIMS.P2.OracleConformance.Tests
```
انتظار: ۶۰ خط PASS برای InMemory و ۶۰ برای Oracle و `ALL PASS`.
