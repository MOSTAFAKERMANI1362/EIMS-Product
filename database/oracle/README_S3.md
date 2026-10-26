# S3 — اجرا

۱) پیش‌نیاز: S2 در `main` است. بعد:
```
cd C:\Users\mostafa\Desktop\EIMS-git
git checkout main
git pull
git checkout -b feature/s3-outbox-dispatcher
```
۲) محتوای `EIMS_S3.zip` را روی `EIMS-git` کپی کن (Replace).
۳) **هیچ تغییر یک‌کلمه‌ای در کد موجود لازم نیست**. فقط پروژه‌ی Oracle باید به پروژه‌ی جدید رجوع کند:
```
dotnet add src\EIMS.Persistence.Oracle reference src\EIMS.Outbox\EIMS.Outbox.csproj
```
۴) بازسازی دیتابیس (V005 اضافه شد؛ همچنان ۲۱ جدول):
```
cd database\oracle
sqlplus EIMS_OWNER@localhost:1521/FREEPDB1
@dev_rebuild_all.sql
EXIT
Select-String -Path dev_rebuild_output.txt -Pattern "FAIL|ORA-"
```
فقط خط عنوان RESET باید بیاید (ممکن است چند `ORA-00942` بی‌خطر هم ببینی).
۵) تست‌ها (خط رمز را از خروجی‌ای که می‌فرستی حذف کن):
```
cd ..\..
dotnet build tests\EIMS.P3.OutboxDispatcher.Tests
dotnet run --project tests\EIMS.P3.OutboxDispatcher.Tests
$env:EIMS_ORACLE_CONNECTION = "User Id=EIMS_APP;Password=رمزشما;Data Source=localhost:1521/FREEPDB1"
dotnet run --project tests\EIMS.P3.OutboxDispatcher.Tests
dotnet run --project tests\EIMS.P2.OracleConformance.Tests
```
انتظار:
- اجرای اول (بدون Oracle): ۲۰ خط PASS برای InMemory، یک خط SKIP، و `ALL PASS`.
- اجرای دوم (با Oracle): ۲۰ خط PASS برای InMemory و ۲۰ برای Oracle و `ALL PASS`.
- تست‌های S2 هنوز ۱۵۴ خط PASS برای هر مخزن و `ALL PASS` (مطمئن می‌شویم V005 چیزی را خراب نکرده).

۶) commit و PR:
```
Move-Item README_S3.md database\oracle\README_S3.md
git add -A
git grep --cached -n -i "<کلمه-یا-بخشی-از-رمز-خودت>"
git --no-pager status --short
git commit -m "S3: outbox dispatcher (EIMS.Outbox) with Oracle claim/lease store (ACR-S3)"
git push -u origin feature/s3-outbox-dispatcher
```
`git grep` باید خالی باشد. لینک PR: `https://github.com/MOSTAFAKERMANI1362/EIMS-Product/pull/new/feature/s3-outbox-dispatcher`
