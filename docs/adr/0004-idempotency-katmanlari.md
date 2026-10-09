# ADR 0004 — Üç katmanlı idempotency

**Durum:** Kabul edildi

| Katman | Anahtar | Kim zorlar |
|---|---|---|
| İstemci → API | `Idempotency-Key` (merchant başına) | `payments(MerchantId, IdempotencyKey)` unique index. Uygulama kodu yalnızca okuyup hızlı yol sağlar; eşzamanlı yarışı veritabanı çözer |
| API → Sağlayıcı | ödeme referansı (`PaymentId`) + `Idempotency-Key: {ref}:{op}` | Sağlayıcı (burada simülasyon): authorize/capture/reverse referansa göre tekrar edilebilir |
| Saga → Defter | `ledger_entries(PaymentId)` unique index | Veritabanı; tekrar eden kayıt başarı sayılır |

- Aynı anahtar, **farklı gövdeyle** gelirse 422 döner: sessizce eski ödemeyi döndürmek yanlış tutarın sanıldığı bir hatayı gizler. Gövde SHA-256 özetiyle karşılaştırılır.
- Tekrar edilen istek `200` + `Idempotent-Replayed: true` döner; ilk yanıt `201` (bitti) veya `202` (sürüyor).
- Anahtar kapsamı merchant başınadır; iki merchant aynı anahtarı kullanabilir.

Sınır: anahtarlar süresiz saklanıyor (temizleme/TTL yok). Gerçek kullanımda 24-72 saat sonrası arşivlenmeli.
