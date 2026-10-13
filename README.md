# Oxira Design API

الخادم الخلفي لموقع [design.oxira.sa](https://design.oxira.sa) (تحويل مخططات أوتوكاد إلى تصميم ثلاثي الأبعاد، رندرات الذكاء الاصطناعي، مصمم المخططات، طلبات التصميم). يحل محل workflows الـ n8n الثمانية بالكامل (مصادرها في مستودع الموقع `n8n/*.workflow.ts`).

- **التقنية:** ASP.NET Core 10 و EF Core 10 على SQL Server (قاعدة بيانات خاصة `OxiraDesign`).
- **البنية:** Clean Architecture مع MediatR 12.5 (أوامر `ICommand` واستعلامات `IQuery` و`LoggingBehavior`) و Controllers.
- **الحساب:** JWT لحساب أوكسيرا الموحّد (يصدره `oxira-web-api`، وهذا الـ API يتحقق منه فقط).
- **التكاملات:** Claude (Anthropic)، OpenAI Images، بريد Zoho عبر SMTP (طابور في الخلفية)، مدفوعات ميسر (Moyasar).
- **التوثيق والتحديث اللحظي:** Swagger و SignalR. **الاستضافة:** IIS.

## البنية

| المشروع | المحتوى |
|---|---|
| `Oxira.Design.Domain` | الكيانات (الطلبات وملفاتها ورسائلها، المصممون، المخططات، المحافظ، الشراءات، الإعلانات، أسعار البناء …) والقواعد: الأسعار، الحالات، اللغات، توقيت الرياض |
| `Oxira.Design.Application` | أوامر واستعلامات لكل ميزة في `Features/*`، المنافذ (`IAppDbContext`، `IEmailSender`، `IAnthropic`، `IImageEditor`، `IPayments`، `IFileStore`، `IQuota`، `IRealtime`)، وقوالب الإيميل |
| `Oxira.Design.Infrastructure` | EF Core / SQL Server، SMTP في الخلفية مع 3 محاولات، Claude، OpenAI، ميسر (ومحاكي للتطوير)، تخزين الملفات على القرص، عدّادات الحصص اليومية (`MERGE … HOLDLOCK`) |
| `Oxira.Design.Api` | Controllers، JWT، Swagger، SignalR (`/hubs/design`)، حدود الطلبات لكل IP، المهام المجدولة |
| `tests/Oxira.Design.Tests` | اختبارات تكامل على SQL Server حقيقي (في CI) مع بدائل وهمية للبريد و Claude و OpenAI وميسر |

## الواجهات

كل الطلبات تقبل JSON، أو JSON بنوع `text/plain` (يتجنب طلب CORS المسبق)، أو `application/x-www-form-urlencoded` (نافذة المحادثة). الردود بنفس أسماء الحقول التي يقرؤها الموقع.

| الطريقة | المسار | الوظيفة |
|---|---|---|
| POST | `/api/chat` | المساعد الذكي: Claude مع أداة `create_lead`، ذاكرة 14 رسالة، 60 رسالة للجلسة و3000 للموقع يوميًا؛ الخادم يرسل إيميل الطلب للفريق |
| GET | `/api/chat/{sessionId}` | سجل المحادثة |
| POST | `/api/orders` | طلب تصميم (multipart): الباقة والبيانات والملفات (dxf dwg pdf png jpg webp heic zip glb، حتى 15 ميجابايت). السعر يُحسب في الخادم؛ الباقة السريعة 249 + 15% = 286.35 ريال تُدفع عبر ميسر → `{success, orderId, payUrl?}` |
| GET | `/api/orders/{id}/payment` | هل دُفع الطلب |
| POST | `/api/payments/moyasar/callback` | رد ميسر: الفاتورة تُجلب من ميسر للتحقق، ثم تُطبَّق حسب `metadata.kind` (design / redesign / listing) مرة واحدة فقط |
| POST | `/api/renders` | رندر الاستوديو (OpenAI images edits): 3 مجانًا للزائر يوميًا، 6 لكل IP، 150 للموقع؛ بعدها رصيد من المحفظة، والرصيد يرجع إذا فشل الرندر |
| POST | `/api/redesign/renders` | أعد تصميم غرفتك: 1 مجاني للزائر، 2 لكل IP، 60 يوميًا (نموذج اقتصادي)، ثم رصيد بنموذج أدق |
| GET | `/api/wallets/{wallet}?sessionId=` | الرصيد والمجاني المتبقي |
| POST | `/api/wallets/purchases` | شراء باقة: p10 = 29 ريال / 10، p30 = 69 / 30، p100 = 179 / 100 (شامل الضريبة) → رابط ميسر؛ بعد الدفع إيصال برابط الاستعادة `/redesign/?w=` |
| POST | `/api/plan-ai` | مصمم المخططات: `brief` (وصف نصي → إعدادات) أو `image` (صورة مخطط → غرف بالأمتار). الحدود: 40 وصفًا و8 صور لكل IP يوميًا |
| GET | `/api/account` | الحساب (JWT): المخططات، الطلبات مع رسائلها، ولمصممي الشبكة الطلبات المتاحة وطلباتهم |
| GET/POST/DELETE | `/api/projects[/{pid}]`، `/api/projects/{pid}/publish` | المخططات المحفوظة (50 مخططًا، 400 كيلوبايت لكل مخطط)، المشاركة، والنشر في المعرض |
| POST | `/api/orders/{id}/comments` · `claim` · `rating` · `delivery` | رسالة للطرف الآخر (مع إيميل)، استلام مصمم للطلب، تقييم العميل بعد التسليم، تسليم الملفات (multipart حتى 20 ميجابايت، تصل العميل بالإيميل) |
| GET / POST | `/api/designers` · `/api/designers/apply` | دليل المصممين النشطين (بدون إيميلات)، وطلب الانضمام |
| GET | `/api/share/{s}` · `/api/gallery` | مخطط مشارك برابط، وأحدث 36 مخططًا منشورًا |
| POST | `/api/waitlist` | قائمة انتظار Pro |
| POST / GET | `/api/listings` · `/api/listings/{id}` | جولة إعلان ثلاثية الأبعاد: Claude يكتب نص الإعلان، مسودة حتى دفع 49 ريال؛ رقم المالك يظهر بعد التفعيل فقط |
| POST | `/api/finishing` | طلب عرض تشطيب من الاستوديو (3 لكل IP يوميًا) |
| GET | `/api/costs/rates` | أحدث أسعار بناء معتمدة (`Cache-Control: public, max-age=3600`) |
| POST | `/api/costs/quotes` | طلب عروض مقاولين (5 لكل IP، 300 يوميًا) |
| GET | `/api/costs/approve?t=` | رابط اعتماد الأسعار الشهرية (صفحة HTML) |
| GET | `/api/optout?e=&t=` | إيقاف رسائل المتابعة (صفحة HTML) |

**الأخطاء:** `{ success:false, reason, message }` مع حالة HTTP حسب السبب، ويبقى جسم الرد (مثلًا `remaining` و`credits` في الرندر):

| reason | HTTP |
|---|---|
| invalid، missing، email، empty، image | 400 |
| session | 401 |
| credits | 402 |
| denied | 403 |
| not_found | 404 |
| exists، not_available | 409 |
| too_big | 413 |
| unreadable، parse | 422 |
| failed، ai، payments (تعذّر مزوّد الذكاء الاصطناعي أو ميسر) | 424 |
| limit | 429 |

**حالات الطلب** تُحفظ وتُرجع بالإنجليزية: `new` جديد، `unpaid` بانتظار الدفع، `paid` مدفوع، `progress` قيد التنفيذ، `delivered` تم التسليم، `cancelled` ملغي.

## الحساب الموحّد (JWT)

- **الإصدار:** `oxira-web-api` (رابط الدخول بالإيميل، ثم `token` و`refreshToken`). هنا تحقق فقط: HS256، `Jwt:Issuer` = `https://api.oxira.sa`، `Jwt:Audience` = `oxira`، و`Jwt:Key` نفسه في كل APIs أوكسيرا.
- **الهوية:** الادعاء `email`؛ الطلبات تُربط بالحساب بالإيميل (بدون حساسية لحالة الأحرف).
- **المصممون:** حالة المصمم (`pending` ثم `active` أو `admin`) في جدول `Designers`؛ للموافقة على مصمم غيّر `Status` إلى `active`. دور `staff` أو `admin` في حساب أوكسيرا يُعامل كـ admin هنا.
- **للتجربة:** في بيئة التطوير `POST /api/dev/token {email, role}` يعطي رمزًا (404 في الإنتاج).

## SignalR

- **الرابط:** `/hubs/design` (الرمز في `?access_token=` للحساب).
- **عند الاتصال بحساب:** ينضم تلقائيًا لمجموعات طلباته، ويصله `order` عند رسالة جديدة أو تغيّر الحالة أو التسليم أو الدفع.
- **بدون حساب:** `JoinWallet(walletId)` ليصله `wallet` بالرصيد الجديد بعد الشراء أو الرندر، و`JoinPayment(orderId)` ليصله `paid`.
- **طلب معيّن:** `JoinOrder(orderId)` (للعميل أو مصمم الطلب أو admin).

## المهام المجدولة

تعمل داخل الـ API (`JobsHost`)، كل دقيقة تفحص ما حان وقته، وكل فترة تُنفَّذ مرة واحدة (جدول `JobRuns`):

- **يوميًا 10:00 الرياض:** متابعة من حفظ مخططًا قبل 2–14 يومًا ولم يطلب، طلب تقييم بعد 3–30 يومًا من التسليم، وإبلاغ المصممين النشطين (bcc) بالطلبات الجديدة بدون مصمم.
- **الأحد 09:00:** تقرير الأسبوع بالأرقام + نصوص سوشيال لمخطط الأسبوع يكتبها Claude.
- **يوم 1 من الشهر 09:00:** Claude مع البحث في الويب يقترح أسعار بناء جديدة؛ لا تُقبل حركة أكبر من 35%؛ تُحفظ معلّقة ويصل الفريق إيميل برابط الاعتماد.
- **رسائل المتابعة:** فيها رابط إيقاف موقّع (HMAC بـ `Jobs:OptOutSecret` أو `Jwt:Key`)، وروابط n8n القديمة تعمل أيضًا.

## الإعدادات (متغيرات البيئة)

لا تضع أي سر في المستودع (المستودع عام).

| المتغير | الوصف |
|---|---|
| `ConnectionStrings__Design` | اتصال SQL Server |
| `Jwt__Key` | مفتاح JWT المشترك مع oxira-web-api (32 حرفًا على الأقل) |
| `Smtp__Password` | كلمة مرور تطبيق Zoho لـ info@oxira.sa |
| `Anthropic__ApiKey` | مفتاح Claude |
| `OpenAi__ApiKey` | مفتاح OpenAI Images |
| `Payments__SecretKey` | المفتاح السري لميسر (sk_live_…) |
| `Payments__Simulate` | `true` لتجربة الدفع بدون ميسر: الرابط يدفع الفاتورة فورًا (للتطوير فقط) |
| `Site__ApiUrl` | العنوان العام لهذا الـ API، مثل `https://api.oxira.sa/design` (رابط رد ميسر وروابط الاعتماد والإيقاف) |
| `Storage__Root` | مجلد ملفات الطلبات (الافتراضي `App_Data/files`)؛ أعطِ Application Pool صلاحية الكتابة |
| `Jobs__Enabled` | `false` لإيقاف المهام المجدولة (مثلًا على نسخة ثانية) |
| `Jobs__OptOutSecret` | اختياري: مفتاح توقيع روابط الإيقاف |

إعدادات أخرى في `appsettings.json`: النماذج (`Ai:Model`، `Renders:Model`، `Redesign:FreeModel/PaidModel`، `PlanAi:Model`، `Forms:ListingModel`، `Jobs:ResearchModel`)، الحصص اليومية، `Cors:Origins`، `RateLimit:*`، `Proxy:TrustCloudflare`.

## ميسر

- **مكان رد الدفع:** الـ API يمرر `callback_url` = `Site:ApiUrl` + `/api/payments/moyasar/callback` مع كل فاتورة. يمكن أيضًا إضافة الرابط نفسه كـ Webhook في لوحة ميسر.
- **التحقق:** الرد لا يُصدَّق كما هو؛ الفاتورة تُجلب من ميسر بالمفتاح السري وتُطبَّق مرة واحدة.
- **قبل تفعيل الحساب:** شغّل `Payments__Simulate=true` للتجربة فقط.

## التشغيل محليًا

```bash
dotnet run --project src/Oxira.Design.Api   # https://localhost:7102/swagger
dotnet test                                  # يحتاج SQL Server (OXIRA_DESIGN_TEST_DB)
```

## النشر على IIS

1. **السيرفر:** ثبّت .NET 10 Hosting Bundle، وفعّل **WebSocket Protocol** من Windows Features.
2. **الملفات:** نزّل artifact باسم `oxira-design-api-iis` من آخر تشغيل لـ CI، أو نفّذ `dotnet publish -c Release`.
3. **الموقع في IIS:** Site أو Application (مثلًا `api.oxira.sa/design`)، و Application Pool على **No Managed Code**. الملف `web.config` يرفع حد الطلب إلى 26 ميجابايت، ويفعّل WebSockets، ويمرر ردود الأخطاء كما هي.
4. **متغيرات البيئة:** من الجدول أعلاه، تحت `<environmentVariables>` في `web.config` أو في إعدادات السيرفر.
5. **قاعدة البيانات:** تُنشأ وتُحدَّث تلقائيًا عند التشغيل. لتطبيقها يدويًا اضبط `Database__MigrateOnStartup=false` واستخدم `db/oxira-design.sql`.
6. **Cloudflare:** اترك `Proxy:TrustCloudflare=true` ليقرأ عنوان الزائر من `CF-Connecting-IP`.
7. **الموقع:** في مستودع `oxira-design` أضف متغيرات GitHub `PUBLIC_API_URL` (هذا الـ API) و`PUBLIC_AUTH_URL` (oxira-web-api) ثم أعد النشر.

## Migrations

**Actions → Add EF Core migration → Run workflow** مع اسم الـ migration: يولّدها ويضيف السكربت `db/oxira-design.sql`، ثم يحفظهما في المستودع. بعدها شغّل CI يدويًا (دفعات البوت لا تشغّله).
