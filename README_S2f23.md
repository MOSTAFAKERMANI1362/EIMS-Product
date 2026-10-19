# S2f-2 + S2f-3 (یک PR) — اجرا

وضعیت فعلی: PR مربوط به S2f-1 هنوز merge نشده، پس اول آن را ببند، بعد این بسته را روی `main` به‌روز اعمال کن.

## الف) ذخیره‌ی کارهای نیمه‌کاره و merge کردن S2f-1
```
cd C:\Users\mostafa\Desktop\EIMS-git
git stash push -u -m "s2f3-wip"
git checkout feature/s2f1-portfolio-oracle
Test-Path src\EIMS.Persistence.Oracle\OracleAuthorityStore.Portfolio.cs
git push
```
`Test-Path` باید `True` بدهد. بعد در مرورگر PR بساز و merge کن:
`https://github.com/MOSTAFAKERMANI1362/EIMS-Product/pull/new/feature/s2f1-portfolio-oracle`
(منتظر سبز شدن چک‌ها بمان، بعد Merge pull request.)

## ب) شاخه‌ی جدید روی main به‌روز
```
git checkout main
git pull
git checkout -b feature/s2f23-execution-benefit-knowledge-oracle
git stash drop
```
(`git stash drop` بقایای قدیمی را پاک می‌کند؛ همه‌ی فایل‌های لازم در zip این بسته هست.)

## ج) اعمال بسته
محتوای `EIMS_S2f23.zip` را روی `EIMS-git` کپی کن (Replace). بعد **چهار تغییر یک‌کلمه‌ای** در سه فایل:
```
$utf8 = New-Object System.Text.UTF8Encoding $false
function Patch($path, $pairs) {
  $f = (Resolve-Path $path).Path
  $t = [System.IO.File]::ReadAllText($f)
  $n = $t
  foreach ($p in $pairs) { $n = $n.Replace($p[0], $p[1]) }
  if ($n -eq $t) { "NO CHANGE: $path" } else { [System.IO.File]::WriteAllText($f, $n, $utf8); "Patched: $path" }
}
Patch "src\EIMS.Persistence.Recovery\ExecutionTransactionalStoreWave10.cs" @(,@("private static void ValidateCommit(", "public static void ValidateCommit("))
Patch "src\EIMS.Persistence.Recovery\BenefitTransactionalStoreWave11.cs"  @(,@("private static void ValidateCommit(", "public static void ValidateCommit("))
Patch "src\EIMS.Persistence.Recovery\KnowledgeTransactionalStoreWave13.cs" @(@("private static void ValidateCreate(", "public static void ValidateCreate("), @("private static void ValidateMutation(", "public static void ValidateMutation("))
```
انتظار: سه خط `Patched: …`.

## د) بازسازی دیتابیس و تست
```
cd database\oracle
sqlplus EIMS_OWNER@localhost:1521/FREEPDB1
@dev_rebuild_all.sql
EXIT
Select-String -Path dev_rebuild_output.txt -Pattern "FAIL|ORA-"
cd ..\..
dotnet build tests\EIMS.P2.OracleConformance.Tests
$env:EIMS_ORACLE_CONNECTION = "User Id=EIMS_APP;Password=رمزشما;Data Source=localhost:1521/FREEPDB1"
dotnet run --project tests\EIMS.P2.OracleConformance.Tests
```
انتظار: فقط خط عنوان RESET در `Select-String`، و ۱۵۴ خط PASS برای InMemory و ۱۵۴ برای Oracle و `ALL PASS`. خط رمز را از خروجی حذف کن.

## ه) commit و PR
```
Move-Item README_S2f23.md database\oracle\README_S2f23.md
git add -A
git grep --cached -n -i "Mostafa1362"
git --no-pager status --short
git commit -m "S2f-2/S2f-3: Oracle execution lifecycle, benefit and knowledge persistence (ACR-S2f2, ACR-S2f3)"
git push -u origin feature/s2f23-execution-benefit-knowledge-oracle
```
`git grep` باید خالی باشد. لینک PR: `https://github.com/MOSTAFAKERMANI1362/EIMS-Product/pull/new/feature/s2f23-execution-benefit-knowledge-oracle`
