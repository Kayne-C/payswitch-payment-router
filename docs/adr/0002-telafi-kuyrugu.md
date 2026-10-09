# ADR 0002 — Telafi: tek `reverse` işlemi ve kalıcı kuyruk

**Durum:** Kabul edildi

## Bağlam

Bir sağlayıcıda para tutuluyor olabilir ama bunu bilmiyoruz (authorize zaman aşımına uğradı), ya da tutuluyor ve işi bırakıyoruz (defter yazılamadı).
Telafinin hangi durumda olduğumuzu bilmesine gerek kalmamalı; ve telafinin kendisi de sağlayıcı kapalıyken başarısız olabilir.

## Karar

- Sağlayıcı arayüzünde `Reverse(reference)`: yetki varsa void, capture edilmişse iade, hiçbir şey yoksa başarı (404 = "geri alınacak bir şey yok", hedef durum zaten sağlanmış).
  Böylece "belirsiz sonuç" için telafi, sonucu bilmeden güvenle çağrılabilir.
- Telafi `compensations` tablosunda `(payment, provider)` başına tek kayıt (unique index): iki kez kuyruğa yazmak iki iade üretemez.
- Kurtarma işçisi bekleyen kayıtları üstel geri çekilmeyle **sonsuza dek** dener. Gecikme kabul edilebilir, kayıp değil.
- Her belirsiz authorize hatasında telafi kuyruğa yazılır; "istek gitmedi" varsayımı yapılmaz (circuit breaker kararı bile yetmez, bkz. ADR 0003).
- Telafisi kuyrukta olan sağlayıcı o ödeme için "yanık" sayılır: aynı ödemede tekrar seçilmez, böylece hem iptal edip hem capture etmeye çalışmayız.

## Sonuçlar

Kaos testi bunun ne kadar gerekli olduğunu gösterdi: telafi koşulunu kodla çıkarmaya çalışan ilk sürüm, breaker'ın devreye girdiği bir yarışta hold'u unuttu.
Basit ve her zaman uygulanan kural (belirsizsen telafi yaz) bu sınıfı kapatır; bedeli, gereksiz birkaç idempotent `reverse` çağrısıdır.
