# PaySwitch — Dayanıklı Ödeme Yönlendirme Motoru

Birden çok ödeme sağlayıcısı (PSP) arasında ödemeyi **en ucuz sağlıklı sağlayıcıya** yönlendiren, sağlayıcı çökse de, yanıt kaybolsa da,
süreç ortada ölse de **para kaybetmeyen, iki kez çekmeyen, askıda bırakmayan** bir backend. .NET 10, PostgreSQL, Polly.

Asıl iddia şu: bir ödeme sisteminin zor kısmı mutlu yol değil, "yanıt gelmedi, ne oldu bilmiyorum" anıdır. Bu repo o anı
deterministik testlerle ve rastgele hata enjekte eden bir kaos testiyle sınar.

## Neler yapıyor

| Alan | Yaklaşım | Kanıt |
|---|---|---|
| **Maliyet bazlı yönlendirme** | Tutara göre toplam komisyon (`bps × tutar + sabit`), para birimi desteği, sağlıksız sağlayıcı dışarıda | Birim testler: tutara göre sıralama değişir, eşitlikte ada göre deterministik |
| **Failover** | Sağlayıcı çökerse sıradakine geçer; reddedilen kart (decline) ise **geçmez** (aynı banka aynı cevabı verir) | Test: alpha kapalı → beta ile tamamlanır; decline'da beta/gamma'ya hiç istek gitmez |
| **Polly dayanıklılığı** | Her sağlayıcıya özel pipeline: retry (üstel + jitter) → circuit breaker → deneme başına timeout | Test: geçici hata sessizce tekrarlanır; breaker açılınca ölü sağlayıcıya **0 istek**, süre dolunca tek probe ile geri döner |
| **Saga** | Authorize → capture → defter kaydı. Her karar **eyleme geçmeden önce** veritabanına yazılır; hata olursa telafi (reverse) sırası kalıcı kuyrukta | 8 saga testi: capture kesintisi, deadline, defter hatası, 3 farklı noktada süreç çökmesi |
| **Sıfır veri kaybı** | Telafi kuyruğu sonsuz tekrar eder; sağlayıcı saatlerce kapalı olsa da müşterinin bloke tutarı sonunda serbest kalır | Test: capture ve reverse aynı anda kapalıyken ödeme `Failed`, sağlayıcı dönünce hold kalkar |
| **İki kez çekmeme** | Sağlayıcıya giden her işlem ödeme referansıyla idempotent; yanıtı kaybolan istek tekrarlanınca aynı sonucu alır | Test: "işlendi ama yanıt kayboldu" → 2 istek, **1** authorization |
| **API idempotency** | `Idempotency-Key` + `(merchant, key)` üzerinde veritabanı unique index; aynı anahtar farklı gövdeyle → 422 | Test: 30 eşzamanlı çift tıklama → 1 ödeme, 1 çekim, 1 defter satırı |
| **Kurtarma** | Takılan ödemeler `NextAttemptAt` (visibility timeout) ile bulunur, veritabanı kirası (lease) ile aynı anda tek işçi sürer | Test: authorize / capture / ledger sonrası çökme → devam ettirilir, çift hold/çekim/kayıt yok |

### Kaos testi (asıl kanıt)

300 ödeme, üç sağlayıcı: %25 işlemeden hata, %15 **işledikten sonra yanıtı kaybetme**, defter %25 hata, kartların %10'u reddedilmiş. Test sonunda doğrulananlar:

- hiçbir ödeme yarım kalmadı, hiçbir telafi beklemede değil;
- her `Completed` ödemenin parası **tam bir** sağlayıcıda yakalanmış; `Failed` olanların parası **hiçbir** sağlayıcıda yok, açık hold yok;
- sağlayıcıların toplam yakalanan tutarı = satılanlar = defterdeki toplam.

Örnek bir koşu (rastgele; her koşuda sayılar biraz değişir, değişmezler değişmez):

```
payments=300 completed=268 failed=32 (Declined=30, NoProviderAvailable=2)
provider_requests authorize=449 capture=417 reverse=56
compensations=38  multi_provider_payments=33 (failover'a düşen)
captured = ledger = 1.294.476 (minör birim)
```

**Bu test gerçek bir hata buldu.** İlk sürümde, bir authorize denemesi sağlayıcıda işlenip yanıtı kaybolduğunda ve bu hata breaker'ı
açtığında, retry'ın ikinci denemesi `circuit open` ile reddediliyordu. Son hata türü "istek hiç gitmedi" diyordu ama ilk deneme bankaya ulaşmıştı;
telafi kuyruğa yazılmadığı için müşterinin parası askıda kalıyordu. Düzeltme: belirsiz her authorize hatasında telafi **her zaman** kuyruğa yazılır.
Deterministik regresyon testi [`ResilienceTests`](tests/PaySwitch.Tests/ResilienceTests.cs) içinde (düzeltme olmadan kırmızı, ile yeşil).
İkinci hata: Polly breaker'ı `Open → HalfOpen` yalnızca bir çağrı geldiğinde geçirir; yönlendirici "açık" sağlayıcıyı baştan eleyince sağlayıcı asla geri yoklanamıyordu. Bkz. [ADR 0003](docs/adr/0003-polly-pipeline.md).

## Hızlı başlangıç

```bash
docker compose up -d --build      # API (5200) + PostgreSQL + 3 sahte sağlayıcı (5301-5303)

curl -i localhost:5200/v1/payments -H 'content-type: application/json' \
  -H 'Idempotency-Key: siparis-1001' -H 'X-Merchant-Id: magaza-1' \
  -d '{"amount":12500,"currency":"TRY","paymentMethodToken":"tok_ok"}'
# 201: state=Completed, provider=alpha (en ucuz). Aynı isteği tekrarla -> 200 + Idempotent-Replayed: true, ikinci çekim yok.
curl localhost:5200/v1/payments/<id>      # deneme geçmişi ve telafiler dahil
curl localhost:5200/v1/providers          # circuit breaker durumları
curl localhost:5200/v1/ops/summary        # durum başına ödeme sayısı, bekleyen telafi
```

Test kartları: `tok_declined` (banka reddi), `tok_insufficient` (yetersiz bakiye).

### Kendin boz

Sağlayıcılar çalışma anında bozulabilir:

```bash
curl -X POST localhost:5301/_control -H 'content-type: application/json' -d '{"down":true}'            # alpha çöktü
curl -X POST localhost:5301/_control -H 'content-type: application/json' -d '{"failOps":["capture"]}'    # yalnız capture bozuk
curl -X POST localhost:5301/_control -H 'content-type: application/json' -d '{"dropNext":1}'             # sonraki istek işlenir, yanıt kaybolur
curl -X POST localhost:5301/_control -H 'content-type: application/json' -d '{"errorRate":0.3,"dropResponseRate":0.1}'
curl localhost:5301/_stats                                                                                # sağlayıcının gerçekte tuttuğu para
```

Sonra ödeme gönder ve `GET /v1/payments/{id}` ile hangi sağlayıcının denendiğini, hangi telafilerin kuyruğa girdiğini izle.

## Durum makinesi

```
Created → Authorizing → Authorized → Capturing → Captured → Completed
              │              │            │           │
              └──────────────┴────────────┴───────────┴──→ Failed  (Declined | NoProviderAvailable | CaptureFailed | LedgerFailed)
                                                               └─ gerekiyorsa: kalıcı telafi kaydı (reverse), sağlayıcı onaylayana kadar tekrarlanır
```

- Geçişler kodda tabloyla zorlanır (`Payment.Transition`); geçersiz geçiş istisna fırlatır.
- **Write-ahead:** "şu sağlayıcıyı çağıracağım" bilgisi çağrıdan önce yazılır. Çökerse devam eden işlem aynı sağlayıcıya sorar (idempotent), başkasına geçip ikinci hold oluşturmaz.
- Telafi, onu gerektiren karar ile **aynı veritabanı işleminde** yazılır; "karar verildi ama telafi yazılmadı" aralığı yoktur.
- Capture'da failover yok: yetki yalnızca authorize edilen sağlayıcıdadır. Orada sabır, süre dolarsa (`CaptureDeadlineSeconds`) telafi vardır. API bu sırada dürüstçe `202 Accepted` döner.

Kararlar: [docs/adr](docs/adr).

## Testler

```bash
dotnet test --solution PaySwitch.slnx      # Docker gerekir (Testcontainers ile gerçek PostgreSQL 17)
```

29 test: yönlendirme, API ve idempotency, direnç (retry, kayıp yanıt, failover, breaker, hepsi çökük), saga (kesinti, deadline, defter, 3 çökme noktası, yarışan işçiler), kaos.
Sahte sağlayıcılar gerçek HTTP sunucularıdır (rastgele portta), mock/stub değil. Docker yoksa testler **atlanmaz, başarısız olur**.
Art arda 5 tam koşu geçti.

## Bilinen sınırlar (dürüst liste)

- Sağlayıcılar **simülasyon**. 3-D Secure, webhook'lar, kısmi capture/iade, mutabakat (settlement/reconciliation), chargeback modellenmedi. Gerçek bir PSP ile **denenmedi**.
- Telafi tek bir `reverse` işlemine dayanır (yetki henüz capture edilmediyse void, edildiyse iade). Bunu tek uçta sunan PSP'ler var (örn. "cancelOrRefund" benzeri), sunmayanlar için ayrı void/refund mantığı gerekir.
- Defter aynı veritabanında ama arayüz arkasında. "Defter kaydı yazıldı, sonra çöktüm" durumu idempotent kayıtla çözülüyor; defterin kayıt yazdıktan sonra **kalıcı** hata verip iade gerektirmesi (yetim kayıt) modellenmedi.
- Kaos testi tohumsuz rastgele; bir hatayı birebir tekrar üretmek için tohum yok (bulunan iki hata deterministik testlere çevrildi).
- Kiralar (lease) ve zamanlayıcılar uygulama saatini kullanır; düğümler arası saat kayması yönetilmiyor. "Yarışan işçiler" testi tek süreçte eşzamanlı çalıştırıcıları dener, ayrı makineleri değil.
- Şema `EnsureCreated` ile kurulur, EF migration yok. Üretimde CI'dan migration çalıştırılmalı.
- Yönlendirme yalnızca maliyet + sağlık; başarı oranına, merchant kuralına veya A/B'ye göre yönlendirme yok.
- Kimlik doğrulama bu serviste yok (önüne [NetRoute](https://github.com/Kayne-C/netroute-api-gateway) gibi bir gateway konur); `X-Merchant-Id` güvenilen başlık olarak okunur. Kart numarası alınmaz, yalnızca token (PCI kapsamı dışı).
- Telafiler sonsuz tekrar eder ama uyarı/dead-letter mekanizması yok; yalnızca `Attempts` sayacı ve `/v1/ops/summary` var.
- Yük/performans ölçümü yapılmadı; bu repo doğruluğu ölçer, hızı değil.
