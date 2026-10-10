# ADR 0004 — Three layers of idempotency

**Status:** Accepted

| Layer | Key | Enforced by |
|---|---|---|
| Client → API | `Idempotency-Key` (per merchant) | The unique index `payments(MerchantId, IdempotencyKey)`. Application code only reads it for a fast path; the database resolves concurrent races |
| API → provider | the payment reference (`PaymentId`) + `Idempotency-Key: {ref}:{op}` | The provider (a simulation here): authorize/capture/reverse can be repeated per reference |
| Saga → ledger | the unique index `ledger_entries(PaymentId)` | The database; a repeated entry counts as success |

- If the same key arrives with a **different body**, the answer is 422: silently returning the old payment would hide a mistaken amount. Bodies are compared by SHA-256 digest.
- A repeated request returns `200` + `Idempotent-Replayed: true`; the first response is `201` (finished) or `202` (in progress).
- The key scope is per merchant; two merchants may use the same key.

Limit: keys are stored indefinitely (no cleanup / TTL). In real use they should be archived after 24–72 hours.
