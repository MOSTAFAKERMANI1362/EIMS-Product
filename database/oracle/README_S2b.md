# S2b — اجرا

۱) `EIMS_S2b.zip` را باز کن و محتوایش را روی پوشه‌ی `C:\Users\mostafa\Desktop\EIMS-git` کپی کن (Overwrite همه‌ی فایل‌های هم‌نام).
   فایل‌های تغییرکرده/جدید: `database\oracle\migrations\V002__evaluation_core.sql`، `database\oracle\verify\verify_evaluation_core.sql`،
   `src\EIMS.Persistence.Oracle\OracleAuthorityStore.cs` و `OracleAuthorityStore.Evaluation.cs` (جدید)، `tests\EIMS.P2.OracleConformance.Tests\Program.cs`، `architecture\decisions\ACR-S2b…`.
۲) چون V002 عوض شده، دیتابیس را دوباره بساز:
```
cd C:\Users\mostafa\Desktop\EIMS-git\database\oracle
sqlplus EIMS_OWNER@localhost:1521/FREEPDB1
@dev_rebuild_all.sql
EXIT
```
   فایل `dev_rebuild_output.txt` را بفرست (همه‌ی خطوط باید PASS باشند).
۳) برگرد به ریشه‌ی ریپو و تست‌ها را اجرا کن:
```
cd C:\Users\mostafa\Desktop\EIMS-git
dotnet build tests\EIMS.P2.OracleConformance.Tests
$env:EIMS_ORACLE_CONNECTION = "User Id=EIMS_APP;Password=رمزشما;Data Source=localhost:1521/FREEPDB1"
dotnet run --project tests\EIMS.P2.OracleConformance.Tests
```
   انتظار: برای InMemory و Oracle هر دو `ALL PASS` (۲۴ خط PASS برای هر کدام).

نکته: فایل‌های `.csproj` عمداً در این بسته نیستند (تغییری نکرده‌اند و csproj آداپتر شما اکنون مرجع پکیج Oracle را دارد؛ بازنویسی‌اش آن را حذف می‌کرد).
