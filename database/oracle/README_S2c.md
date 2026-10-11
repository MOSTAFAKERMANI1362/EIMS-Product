# S2c — اجرا

قبل از هر چیز در `EIMS-git` یک شاخه‌ی جدید از `main` به‌روز بساز (اگر PR مربوط به S2b هنوز merge نشده، اول آن را merge کن):
```
git checkout main
git pull
git checkout -b feature/s2c-evaluation-completion-oracle
```
۱) محتوای `EIMS_S2c.zip` را روی `EIMS-git` کپی کن (Replace).
۲) **تغییر یک‌کلمه‌ای دوم** در `src\EIMS.Persistence.Recovery\TransactionalAuthorityStore.cs` (همان روش قبل):
```
$f = (Resolve-Path "src\EIMS.Persistence.Recovery\TransactionalAuthorityStore.cs").Path
$utf8 = New-Object System.Text.UTF8Encoding $false
$t = [System.IO.File]::ReadAllText($f)
$n = $t.Replace("private static string? ValidateEvaluationCompletionShape(", "public static string? ValidateEvaluationCompletionShape(")
if ($n -eq $t) { "NO CHANGE" } else { [System.IO.File]::WriteAllText($f, $n, $utf8); "Patched" }
```
۳) بازسازی دیتابیس (V002 و V003 عوض شده‌اند):
```
cd database\oracle
sqlplus EIMS_OWNER@localhost:1521/FREEPDB1
@dev_rebuild_all.sql
EXIT
Select-String -Path dev_rebuild_output.txt -Pattern "FAIL|ORA-"
```
فقط خط عنوان RESET باید بیاید. بعد: `Select-String -Path dev_rebuild_output.txt -Pattern "PASS" | Measure-Object` (تعداد PASSها را بفرست).
۴) build و تست:
```
cd ..\..
dotnet build tests\EIMS.P2.OracleConformance.Tests
$env:EIMS_ORACLE_CONNECTION = "User Id=EIMS_APP;Password=رمزشما;Data Source=localhost:1521/FREEPDB1"
dotnet run --project tests\EIMS.P2.OracleConformance.Tests
```
انتظار: ۴۱ خط PASS برای InMemory و ۴۱ برای Oracle و `ALL PASS`.
