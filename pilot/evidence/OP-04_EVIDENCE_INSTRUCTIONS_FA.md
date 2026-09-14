# OP-04 — راهنمای جمع‌آوری Evidence محیط Network Pilot

این مرحله فقط برای اثبات محیط واقعی Pilot است و هیچ Business Rule یا Workflow در v6.360 را تغییر نمی‌دهد.

## اصل مهم

فقط **اطلاعات غیرحساس** در GitHub ثبت شوند. این موارد ممنوع‌اند:

- Password / Token / API Key
- Connection String دارای Credential
- Private Key / PFX / PEM
- فایل واقعی HR یا اطلاعات شخصی کارکنان
- Dump/Backup واقعی Oracle
- Production log دارای داده حساس

در GitHub فقط وضعیت، نام فناوری/نسخه، شناسه Evidence و نتیجه PASS/FAIL ثبت شود.

## IT / Windows

روی Windows Server/VM موردنظر Pilot:

1. نسخه Windows Server ثبت شود.
2. Domain membership تأیید شود.
3. IIS و Windows Authentication بررسی شوند.
4. نسخه .NET Runtime ثبت شود.
5. یک Domain User واقعی بتواند وارد محیط Test شود.
6. نتیجه با Evidence Reference ثبت شود، نه Screenshot شامل اطلاعات حساس.

## Windows Identity

باید مشخص شود Identity واقعی با کدام قالب وارد برنامه می‌شود:

- `DOMAIN\\username`
- UPN
- SID

سپس Mapping زیر باید اثبات شود:

`Windows Identity → PersonID → RoleAssignment → Scope`

Client-provided identity headers نباید Security Authority باشند.

## DBA / Oracle

DBA فقط Metadata غیرحساس زیر را اعلام کند:

- Oracle version
- .NET provider و version
- Connection mode
- Service account **name only**
- Schema owner **name only**
- نتیجه تست اتصال Least-Privilege

هیچ Password یا Connection String در Repository وارد نشود.

## HRIT

یک Export واقعی HR در محیط کنترل‌شده سازمان با قرارداد P4 مقایسه شود. در GitHub فقط نتیجه و شناسه تأیید ثبت شود.

حداقل Mapping موردنیاز:

`PersonID / UnitID / PositionID / ManagerID / validFrom / validTo`

Payroll، کد ملی، حساب بانکی، اطلاعات پزشکی و انضباطی وارد EIMS Pilot Contract نشوند مگر Requirement رسمی جداگانه وجود داشته باشد.

## TLS

ثبت شود:

- Hostname
- Certificate subject
- Expiry date
- نتیجه TLS handshake

Private key یا certificate bundle محرمانه در GitHub قرار نگیرد.

## Runtime proof

در محیط واقعی باید شواهد قابل تکرار برای موارد زیر تولید شود:

- Optimistic Concurrency
- Idempotency replay
- Atomic `State + Audit + Outbox`
- Unauthorized command rejection

## Operations

پیش از Pilot واقعی:

- روش Backup مشخص باشد.
- Restore حداقل یک‌بار تست شود.
- مقصد Log/Monitoring مشخص باشد.

## Exit Criteria

OP-04 زمانی PASS است که Template محیط با Evidence غیرحساس تکمیل شده و هیچ مورد کلیدی با حدس معماری پر نشده باشد.
