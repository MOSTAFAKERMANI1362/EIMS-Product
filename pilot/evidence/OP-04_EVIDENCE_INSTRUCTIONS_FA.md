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

هیچ فیلد خالی با حدس، مقدار نمونه یا اطلاعات ساختگی تکمیل نشود. نبود Evidence به معنی `BLOCKED_EVIDENCE_REQUIRED` است، نه مجوز برای استنتاج.

## کلاس Evidence

دو کلاس Evidence تعریف شده است:

- `LAB_EVIDENCE`: شواهد آزمایشگاهی از PC یا محیط توسعه. برای یادگیری، Smoke Test و کاهش ریسک مفید است اما **هرگز Network Pilot را READY نمی‌کند**.
- `PILOT_ENVIRONMENT_EVIDENCE`: شواهد واقعی و تأییدشده محیط سازمانی Pilot. فقط این کلاس می‌تواند پس از عبور همه Gateها مجوز Activation بدهد.

Template رسمی `pilot-environment-evidence.template.json` از Schema `1.1` و کلاس `PILOT_ENVIRONMENT_EVIDENCE` استفاده می‌کند، اما تا زمان تکمیل Evidence واقعی عمداً همه Gateها BLOCKED می‌مانند.

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

Client-provided identity headers نباید Security Authority باشند و مقدار `clientIdentityHeadersTrusted` باید `false` بماند.

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

## اجرای Activation Gate

پس از تکمیل فایل Evidence، Validator با .NET 10 اجرا می‌شود:

```powershell
dotnet run --project .\src\EIMS.PilotEnvironment.Readiness\EIMS.PilotEnvironment.Readiness.csproj -c Release -- .\pilot\evidence\pilot-environment-evidence.json
```

خروجی ماشین‌خوان شامل وضعیت ۹ Gate و `PilotActivationReady` است. Exit Codeها:

- `0` = همه Evidenceهای واقعی کامل و `PILOT_ENVIRONMENT_EVIDENCE` آماده Activation است.
- `3` = فایل معتبر است اما یک یا چند Evidence ناقص/نامعتبر است؛ Activation باید Fail-Closed بماند.
- `2` = فایل/JSON نامعتبر یا نحوه اجرا اشتباه است.

Validator همچنین وجود Propertyهایی با نام‌های حساس مانند Password، Token، Connection String، Private Key و اطلاعات شخصی ممنوع را تشخیص می‌دهد و Activation را Block می‌کند.

## Exit Criteria

OP-04 فقط زمانی PASS است که:

1. Evidence با کلاس `PILOT_ENVIRONMENT_EVIDENCE` باشد؛
2. هر ۹ Gate شامل Windows Host، Oracle/P2، P1 Authority Package، Windows Identity/P3، HR/P4، TLS، Runtime Proof، Operations و Security Evidence PASS باشند؛
3. هیچ Property حساس در فایل Evidence وجود نداشته باشد؛
4. Validator با Exit Code `0` پایان یابد؛
5. هیچ مقدار کلیدی با حدس معماری یا Placeholder ساختگی پر نشده باشد.
