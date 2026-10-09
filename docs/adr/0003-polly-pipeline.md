# ADR 0003 — Polly pipeline sırası ve breaker'ı geri yoklama

**Durum:** Kabul edildi

## Karar

Sağlayıcı başına **bir** `ResiliencePipeline`, tüm işlemler (authorize/capture/reverse) için paylaşılır, çünkü breaker bir uç noktanın değil sağlayıcının sağlığını temsil eder.

Sıra (dıştan içe): **retry → circuit breaker → deneme başına timeout.**

- Retry breaker'ın dışında: her deneme breaker tarafından görülür; breaker açıksa retry'lar anında biter.
- `BrokenCircuitException` bilerek yeniden denenmez; yönlendirici sıradaki sağlayıcıya geçer.
- Yalnızca geçici hatalar (`5xx`, `429`, bağlantı hatası, timeout) retry ve breaker sayacına girer. **Decline iş sonucudur**, hata değildir: yeniden denenmez, breaker'ı etkilemez.
- Deneme başına timeout, `HttpClient.Timeout` değil pipeline'dan gelir (tek sahip).

## İki ince nokta (testlerle bulundu)

1. **Son hata türü, çağrının gidip gitmediğini söylemez.** Retry breaker'ın dışında olduğundan, 1. deneme işlenip yanıtı kaybolabilir, bu hata breaker'ı açar, 2. deneme `circuit open` ile reddedilir. Sonuç hatası "çağrı yapılmadı" gibi görünür, oysa banka işlemi yapmıştır. Karar: telafi kararı hata türüne bakmaz (ADR 0002).
2. **Polly `Open → HalfOpen` geçişini yalnızca bir çağrı gelince yapar.** Yönlendirici "durum Open" diye sağlayıcıyı eleyince hiçbir çağrı gitmez ve sağlayıcı sonsuza dek dışarıda kalır. `ResilientProvider.Circuit`, `BreakDuration` dolduktan sonra `HalfOpen` raporlar; böylece tek bir probe geçer ve sağlayıcı sağlıklıysa breaker kapanır.
   Test: 5 başarısızlıkla breaker açılır → 5 ödeme boyunca ölü sağlayıcıya sıfır istek → süre dolunca ilk ödeme sağlayıcıya gider, `Closed`.
