# ADR 0001 — Saga: write first, then call

**Status:** Accepted

## Context

A payment touches three independent systems: the PSP (authorize, capture), the ledger and our own database. They cannot be fitted into one ACID transaction (there is no 2PC).
The process can die between any two instructions.

## Decision

An orchestrated saga. The rule: **every decision is persisted before acting on it, and every external call is made idempotent.**

- The provider about to be called (`Payment.Provider`) is written before the call. After a crash the same provider is asked again; because provider operations are idempotent per reference, the original answer comes back and no second hold is created.
- The compensation (`Compensation`) record is written in the **same `SaveChanges`** as the state transition that requires it.
- State is durable after every step, and a payment continues from its state with `AdvanceAsync`. Steps are written as "if I am in this state, the next job is this", so running them again is safe.
- One worker at a time: a database lease (`LeaseUntil`) plus optimistic concurrency (`Version`). If the lease expires and a second worker takes over, the first worker's write is rejected.
- Stuck payments are found with `NextAttemptAt` (a visibility timeout): every write pushes the next look-at time forward; if nobody finishes, the recovery worker takes over.

## Consequences

- Crash tests at three points: after authorize, after capture, after the ledger. In all three: one hold, one charge, one entry.
- Cost: one database write per step; latency goes up, correctness comes in return.
- There is no coordinator process; every API node can run recovery, and the lease prevents collisions.
