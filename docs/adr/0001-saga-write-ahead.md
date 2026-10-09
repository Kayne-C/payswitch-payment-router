# ADR 0001 — Saga: önce yaz, sonra çağır

**Durum:** Kabul edildi

## Bağlam

Bir ödeme üç bağımsız sistemi etkiler: PSP (authorize, capture), defter ve kendi veritabanımız. Bunları tek bir ACID işlemine sığdırmak mümkün değil
(2PC yok). Süreç herhangi iki talimatın arasında ölebilir.

## Karar

Orkestrasyon tabanlı saga. Kural: **her karar, eyleme geçmeden önce kalıcı yazılır; her dış çağrı idempotent yapılır.**

- Çağrılacak sağlayıcı (`Payment.Provider`) çağrıdan önce yazılır. Çöküşten sonra aynı sağlayıcıya sorulur; sağlayıcının işlemleri referansa göre idempotent olduğundan asıl cevap alınır, ikinci hold oluşmaz.
- Telafi (`Compensation`) kaydı, onu gerektiren durum geçişiyle **aynı `SaveChanges`** içinde yazılır.
- Her adımdan sonra durum kalıcı; devam eden işlem `AdvanceAsync` ile durumdan sürer. Adımlar "bu durumdaysam sıradaki iş şu" şeklinde yazıldığı için tekrar çalıştırmak güvenli.
- Aynı anda tek işçi: veritabanı kirası (`LeaseUntil`) + iyimser eşzamanlılık (`Version`). Kira sona erip ikinci işçi devraldığında ilk işçinin yazması reddedilir.
- Takılan ödemeler `NextAttemptAt` ile bulunur (visibility timeout): her kayıt bir sonraki bakış zamanını ileri iter; kimse bitirmezse kurtarma işçisi devralır.

## Sonuçlar

- Çöküş testleri üç noktada: authorize sonrası, capture sonrası, defter sonrası. Üçünde de tek hold / tek çekim / tek kayıt.
- Bedel: her adım bir veritabanı yazması; gecikme artar, doğruluk kazanılır.
- Koordinatör süreç yok; her API düğümü kurtarma yapabilir, kira çakışmayı önler.
