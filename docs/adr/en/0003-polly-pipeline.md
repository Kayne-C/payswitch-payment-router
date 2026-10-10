# ADR 0003 — Polly pipeline order and probing the breaker back

**Status:** Accepted

## Decision

**One** `ResiliencePipeline` per provider, shared by all operations (authorize/capture/reverse), because the breaker represents the health of the provider, not of one endpoint.

Order (outermost to innermost): **retry → circuit breaker → per-attempt timeout.**

- Retry outside the breaker: every attempt is seen by the breaker, and when the circuit is open the retries end immediately.
- `BrokenCircuitException` is deliberately not retried; the router moves on to the next provider.
- Only transient failures (`5xx`, `429`, connection errors, timeouts) are retried and counted by the breaker. **A decline is a business result**, not a failure: it is not retried and does not affect the breaker.
- The per-attempt timeout comes from the pipeline, not from `HttpClient.Timeout` (a single owner).

## Two subtle points (found by tests)

1. **The final error type does not say whether a call went out.** Because retry is outside the breaker, attempt 1 can be processed and its response lost, that failure opens the breaker, and attempt 2 is rejected with `circuit open`. The resulting error looks like "no call was made", yet the bank did act. Decision: the compensation decision never looks at the error type (ADR 0002).
2. **Polly moves `Open → HalfOpen` only when a call arrives.** If the router excludes a provider because "the state is Open", no call is ever made and the provider stays out forever. `ResilientProvider.Circuit` reports `HalfOpen` once `BreakDuration` has passed, so a single probe goes through and, if the provider is healthy, the breaker closes.
   Test: the breaker opens after 5 failures → zero requests reach the dead provider for 5 payments → when the period ends the first payment goes to the provider and the state is `Closed`.
