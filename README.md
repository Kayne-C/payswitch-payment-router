# PaySwitch — A Resilient Payment Routing Engine

**English** · [Türkçe](README.tr.md)

A backend that routes each payment to the **cheapest healthy provider (PSP)** and does not lose money, charge twice or leave funds on hold, even when a provider is down,
a response is lost, or the process dies halfway. .NET 10, PostgreSQL, Polly.

The core claim: the hard part of a payment system is not the happy path, it is the moment when "no response came and I do not know what happened". This repo tests that moment with
deterministic tests and with a chaos test that injects random faults.

## What it does

| Area | Approach | Evidence |
|---|---|---|
| **Cost-based routing** | Total fee for the amount (`bps × amount + fixed`), currency support, unhealthy providers left out | Unit tests: ordering changes with the amount; ties are broken deterministically by name |
| **Failover** | If a provider is down, the next one is used; a card **decline** does **not** fail over (the same bank would give the same answer) | Test: alpha down → completes via beta; on a decline, no request reaches beta/gamma |
| **Polly resilience** | A pipeline per provider: retry (exponential + jitter) → circuit breaker → per-attempt timeout | Test: a transient error is retried silently; once the breaker opens, **0 requests** reach the dead provider, and a single probe brings it back after the break |
| **Saga** | Authorize → capture → ledger entry. Every decision is written to the database **before** acting on it; compensations (reverse) sit in a durable queue | 8 saga tests: capture outage, deadline, ledger failure, process crash at 3 different points |
| **Zero data loss** | The compensation queue retries forever; even if a provider is down for hours, the customer's hold is eventually released | Test: with capture and reverse both down the payment is `Failed`; when the provider returns the hold is released |
| **No double charge** | Every provider call is idempotent on the payment reference; a request whose response was lost gets the same result when retried | Test: "processed but the response was lost" → 2 requests, **1** authorization |
| **API idempotency** | `Idempotency-Key` + a database unique index on `(merchant, key)`; the same key with a different body → 422 | Test: 30 simultaneous double-clicks → 1 payment, 1 charge, 1 ledger row |
| **Recovery** | Stuck payments are found through `NextAttemptAt` (visibility timeout); a database lease keeps a single worker on a payment | Test: crash after authorize / capture / ledger → resumed, no double hold, charge or entry |

### The chaos test (the main evidence)

300 payments across three providers: 25% errors before processing, 15% **response lost after processing**, 25% ledger failures, 10% of cards declined. At the end the test verifies:

- no payment is left in flight and no compensation is pending;
- the money of every `Completed` payment is captured at **exactly one** provider; the money of `Failed` payments is at **none**, with no open holds;
- the providers' total captured amount = what was sold = the ledger total.

A sample run (random; the numbers vary between runs, the invariants do not):

```
payments=300 completed=268 failed=32 (Declined=30, NoProviderAvailable=2)
provider_requests authorize=449 capture=417 reverse=56
compensations=38  multi_provider_payments=33 (fell through to failover)
captured = ledger = 1,294,476 (minor units)
```

**This test found a real bug.** In the first version, when an authorize attempt was processed at the provider but its response was lost, and that very failure opened the breaker,
the retry's second attempt was rejected with `circuit open`. The final error type said "the request never went out", yet the first attempt had reached the bank, and because no compensation
was queued the customer's money stayed on hold. The fix: for every uncertain authorize failure a compensation is **always** queued.
A deterministic regression test lives in [`ResilienceTests`](tests/PaySwitch.Tests/ResilienceTests.cs) (red without the fix, green with it).
The second bug: Polly moves the breaker `Open → HalfOpen` only when a call arrives, so once the router excluded an "open" provider up front it could never be probed back. See [ADR 0003](docs/adr/en/0003-polly-pipeline.md).

## Quick start

```bash
docker compose up -d --build      # API (5200) + PostgreSQL + 3 fake providers (5301-5303)

curl -i localhost:5200/v1/payments -H 'content-type: application/json' \
  -H 'Idempotency-Key: order-1001' -H 'X-Merchant-Id: shop-1' \
  -d '{"amount":12500,"currency":"TRY","paymentMethodToken":"tok_ok"}'
# 201: state=Completed, provider=alpha (cheapest). Repeat the request -> 200 + Idempotent-Replayed: true, no second charge.
curl localhost:5200/v1/payments/<id>      # includes attempt history and compensations
curl localhost:5200/v1/providers          # circuit breaker states
curl localhost:5200/v1/ops/summary        # payments per state, pending compensations
```

Test cards: `tok_declined` (bank decline), `tok_insufficient` (insufficient funds).

### Break it yourself

Providers can be broken at runtime:

```bash
curl -X POST localhost:5301/_control -H 'content-type: application/json' -d '{"down":true}'            # alpha is down
curl -X POST localhost:5301/_control -H 'content-type: application/json' -d '{"failOps":["capture"]}'    # only capture is broken
curl -X POST localhost:5301/_control -H 'content-type: application/json' -d '{"dropNext":1}'             # next request is processed, its response is lost
curl -X POST localhost:5301/_control -H 'content-type: application/json' -d '{"errorRate":0.3,"dropResponseRate":0.1}'
curl localhost:5301/_stats                                                                                # the money the provider really holds
```

Then send a payment and follow it with `GET /v1/payments/{id}` to see which provider was tried and which compensations were queued.

## State machine

```
Created → Authorizing → Authorized → Capturing → Captured → Completed
              │              │            │           │
              └──────────────┴────────────┴───────────┴──→ Failed  (Declined | NoProviderAvailable | CaptureFailed | LedgerFailed)
                                                               └─ if needed: a durable compensation record (reverse), retried until the provider confirms
```

- Transitions are enforced by a table in code (`Payment.Transition`); an illegal transition throws.
- **Write-ahead:** "I am about to call this provider" is persisted before the call. After a crash the work resumes by asking the same provider (idempotent) rather than moving to another and creating a second hold.
- A compensation is written in the **same database transaction** as the decision that requires it, so there is no window of "decided but no compensation recorded".
- No failover at capture: the authorization lives only at the provider that issued it. There, the system is patient, then after a deadline (`CaptureDeadlineSeconds`) it compensates. Meanwhile the API honestly answers `202 Accepted`.

Decisions: [docs/adr/en](docs/adr/en).

## Tests

```bash
dotnet test --solution PaySwitch.slnx      # needs Docker (real PostgreSQL 17 via Testcontainers)
```

29 tests: routing, API and idempotency, resilience (retry, lost response, failover, breaker, everything down), saga (outage, deadline, ledger, 3 crash points, racing workers), chaos.
The fake providers are real HTTP servers (on random ports), not mocks or stubs. Without Docker the tests **fail instead of being skipped**.
Five consecutive full runs passed.

## Known limits (an honest list)

- The providers are a **simulation**. 3-D Secure, webhooks, partial capture/refund, settlement/reconciliation and chargebacks are not modeled. It has **not** been tried against a real PSP.
- Compensation rests on one `reverse` operation (void if not yet captured, refund if captured). Some PSPs expose this as a single endpoint (a "cancelOrRefund"-style call); others need separate void/refund logic.
- The ledger lives in the same database but behind an interface. "Ledger entry written, then I crashed" is handled by an idempotent entry; a ledger that fails **permanently** after writing an entry and then requires a refund (an orphan entry) is not modeled.
- The chaos test is random and unseeded, so a failure cannot be reproduced exactly (the two bugs it found were turned into deterministic tests).
- Leases and timers use the application clock; clock skew between nodes is not handled. The "racing workers" test runs concurrent runners in one process, not on separate machines.
- The schema is created with `EnsureCreated`; there are no EF migrations. In production, run migrations from CI.
- Routing is cost plus health only; no routing by success rate, merchant rules or A/B.
- This service has no authentication (put a gateway such as [NetRoute](https://github.com/Kayne-C/netroute-api-gateway) in front); `X-Merchant-Id` is read as a trusted header. Card numbers are never accepted, only tokens (outside PCI scope).
- Compensations retry forever, but there is no alerting or dead-letter mechanism; only the `Attempts` counter and `/v1/ops/summary`.
- No load or performance measurement was done; this repo measures correctness, not speed.
