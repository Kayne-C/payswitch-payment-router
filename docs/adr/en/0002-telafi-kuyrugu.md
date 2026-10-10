# ADR 0002 — Compensation: a single `reverse` operation and a durable queue

**Status:** Accepted

## Context

Funds may be held at a provider without us knowing it (the authorize call timed out), or they are held and we are abandoning the payment (the ledger could not be written).
A compensation should not need to know which of these situations we are in, and the compensation itself can fail while the provider is down.

## Decision

- On the provider interface, `Reverse(reference)`: void if an authorization exists, refund if it was captured, success if nothing exists (404 = "nothing to undo": the goal state already holds).
  So a compensation for an "uncertain outcome" can be called safely without knowing the outcome.
- The `compensations` table has one record per `(payment, provider)` (a unique index): queueing twice cannot produce two refunds.
- The recovery worker retries pending records with exponential back-off **forever**. A delay is acceptable; a dropped compensation is money held for no reason.
- A compensation is queued on every uncertain authorize failure; the assumption "the request did not go out" is never made (not even from a circuit-breaker decision, see ADR 0003).
- A provider with a queued compensation is "burned" for that payment: it is not chosen again, so we never cancel and capture at the same time.

## Consequences

The chaos test showed how necessary this is: the first version, which tried to derive when to compensate, forgot a hold in a race involving the breaker.
A simple rule that is always applied (if uncertain, write a compensation) closes that whole class; the cost is a few unneeded idempotent `reverse` calls.
