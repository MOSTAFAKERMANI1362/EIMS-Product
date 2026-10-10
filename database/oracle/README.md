# راه‌اندازی S1 روی Oracle (برای کسی که Oracle بلد نیست)

هدف: ساخت ۵ جدول هسته و اثبات اینکه کار می‌کنن. بیشتر از ۱۰ دقیقه نیست.

## ۰) ورژن و نام PDB را بخوان
در SQL*Plus یا SQL Developer با کاربر SYS/SYSTEM وصل شو و اجرا کن:
```
SELECT banner FROM v$version;
SELECT name FROM v$pdbs;
```
این اسکریپت‌ها برای Oracle 12.2 و بالاتر (19c، 21c، XE 18+) نوشته شده. اگر ورژن‌ت پایین‌تر است، قبل از ادامه خروجی را بفرست.

## ۱) ساخت کاربرها (یک‌بار، با SYS/SYSTEM، داخل PDB)
```
ALTER SESSION SET CONTAINER = <نام PDB، مثلا XEPDB1 یا ORCLPDB1>;
@00_admin_create_users.sql
```
پسوردها را می‌پرسد و هیچ‌جا ذخیره نمی‌شوند. (اگر خطای tablespace USERS دادی خبر بده.)

## ۲) ساخت جدول‌ها (با EIMS_OWNER)
```
CONNECT EIMS_OWNER/<پسورد>@<host>/<PDB>
@migrations/V001__core_persistence.sql
@verify/verify_core.sql
```

## نتیجه‌ی درست
۷ خط که همه با `PASS` شروع می‌شن و آخرش `Test rows rolled back.`
اگر هر خطی `FAIL` یا خطای ORA- داشت، کل خروجی را بفرست.

## قوانین
- پسورد و connection string هرگز در گیت commit نشود (سیاست ریپو).
- برنامه فقط با `EIMS_APP` وصل می‌شود، نه `EIMS_OWNER`. این حساب اجازه‌ی UPDATE/DELETE روی audit و decision ندارد.
