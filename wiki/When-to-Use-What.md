# When to Use What

The single most common question about PerformanceCI is "which API do I reach for?" This page is the definitive decision guide. Two tables, one decision tree, and the edge cases nobody documents.

---

## Quick decision table

| Need | Use | Notes |
|---|---|---|
| **Reject a delegate that takes too long** | `PerfGate.AssertTime` | One-shot, returns measured ns. Good for tests & pre-flights. |
| **Reject a delegate that allocates too much** | `PerfGate.AssertAllocations` | Reads `GC.GetAllocatedBytesForCurrentThread()` (zero-alloc probe). |
| **I already hold timestamps** | `PerfGate.Ensure` / `EnsureTicks` | Compare without re-measuring. Ticks version skips the multiply. |
| **Measure a scope in production code, allocation-free** | `PerfWatchdog.Start` / `StartAndThrow` | `ref struct`; whole lifecycle stack-only. |
| **Cheapest possible breach hook** | `PerfWatchdogNative.Start` | Raw `delegate*`; requires `unsafe`. |
| **Observe breaches without failing** | `PerfHeavy.Violation` | Process-wide event; never throws by itself. |
| **Declare budgets on benchmarks for CI** | `PerfCriticalAttribute` + runner | Reflected by `PerfConsoleGate` after the BDN run. |
| **Cap benchmark allocations** | `PerfAllocationAttribute` | Byte budget, same runner integration. |
| **Regress vs a sibling method** | `PerfBaseline` + `PerfComparison` | Ratio gate; see [Comparison Gating](Comparison-Gating). |
| **Show a number, never gate** | `PerfIgnoreAttribute` | `ignored` status; skipped by the verdict. |
| **Report/exit-code for CI** | `PerfConsoleGate.RunAndReport` | Inputs `(method→value)` maps, outputs `0`/`1`. |

---

## Decision tree

```
Do you have a benchmark/attribute workflow (BDN) OR an inline hot path?
│
├─ BDN benchmark → DECLARATIVE
│    │
│    ├─ want an absolute time ceiling → [PerfCritical(..)]
│    ├─ want an absolute byte ceiling → [PerfAllocation(..)]
│    ├─ want "vs sibling" ratio      → [PerfBaseline(..)] + [PerfComparison(.., group)]
│    └─ want report-only rows        → [PerfIgnore]
│         then → PerfConsoleGate.RunAndReport(...) → exit code
│
└─ Inline code → IMPERATIVE
     │
     ├─ one-shot measure of a delegate → PerfGate.AssertTime / AssertAllocations
     ├─ I have my own timestamps       → PerfGate.Ensure / EnsureTicks
     ├─ scoped measure, no alloc       → PerfWatchdog.Start / StartAndThrow
     ├─ observe without failing        → subscribe to PerfHeavy.Violation
     └─ unsafe ultra-low-ceremony      → PerfWatchdogNative.Start
```

*(tip: the `StartAndThrow` vs `Attribute` choice is really **where** the contract lives — inline in the method, or on the type for every CI run.)*

---

## The hard comparisons

### `PerfGate.AssertTime` vs `PerfWatchdog.StartAndThrow`

| | `AssertTime` | `StartAndThrow` |
|---|---|---|
| Shape | `fn(Action)` — whole delegate | `using` scope |
| Closure | **can allocate** (captures) | **never** boxes/allocates |
| Return | measured ns | nothing (but `ElapsedNanoseconds`) |
| Best for | tests, pre-flights, one-off checks | production hot paths, scoped batches |

> `AssertTime` measures the *delegate*. If `() => Something(x)` captures `x`, the closure is real and gets measured. For allocation-sensitive work, prefer `StartAndThrow`.

### `PerfWatchdog.Start` vs `StartAndThrow`

Same cost, different failure policy. `Start` notifies only (observe); `StartAndThrow` notifies **and** throws (assert). Use `Start` in production where a regression should be an alert, not a crash; use `StartAndThrow` in tests and correctness-critical paths where it **must** fail.

### `PerfHeavy.Violation` vs a throwing gate

The event bus does not decide your failure policy — *you* do. Subscribe and log, alert, count, or rethrow as your own exception. A throwing gate embeds the policy in the measurement site. Rule of thumb:

- **Production observability:** `PerfHeavy.Violation` + telemetry sink.
- **Hard gating:** `StartAndThrow` / `Ensure` / attributes + runner exit code.
- **Both:** let watchdogs notify the bus in prod, and assert in tests with `StartAndThrow`.

### Attributes vs `PerfGate`

| | Attributes | Inline gates |
|---|---|---|
| When enforced | **after** a BDN run (CI time) | **right now**, at the call site |
| Bypassable by code path? | No — reflected metadata | Only if you don't call it |
| Allocation | zero (metadata) | zero (primitive) |
| You get | report row + verdict + exit code | exception / event |
| Best for | *contracts of record* for a project | *load-bearing individual functions* |

### `PerfIgnore` vs no attribute

Both give an ungated row, but only `PerfIgnore` (a) marks the *intent* and (b) **suppresses inherited class budgets**. If a class has `[PerfCritical(2000)]` and one method must be exempt, `[PerfIgnore]` is the only way.

### `PerfComparison` vs `PerfCritical`

- `PerfCritical(100)` — *absolute*: this method must be ≤ 100 ns, forever.
- `PerfComparison(1.5, "group")` — *relative*: must be ≤ 1.5× the baseline **whatever the baseline becomes**.

Comparisons silently adapt upward if the baseline regresses. Combine with `[PerfCritical]` on the baseline to cap the absolute drift.

### `Ensure` vs `EnsureTicks`

Identical semantics; `EnsureTicks` just assumes you already live in the tick domain (so no ns conversion on the pass path). Prefer it anywhere you used `NowTicks` yourself, and use `PerfGate.NanosecondsToTicks` to bake a budget once.

---

## Anti-patterns

| Anti-pattern | Why it hurts | Do instead |
|---|---|---|
| Measuring with a capturing closure for allocation checks | Closure allocation pollutes the measurement | `PerfWatchdog` or time-only `Ensure` |
| Failing hard in prod with `StartAndThrow` | A jitter spike takes down a service | `Start` + `PerfHeavy.Violation` sink |
| Comparing baselines with no `[PerfCritical]` | Baseline can silently bloat and drag challengers up | cap the baseline absolutely |
| Formatting exception messages on the hot path | Allocates on breach (though rare) | rely on the lazy `Message` |
| Running the BDN suites in CI on shared runners | Machine noise → flaky gates | reserve hardware; use [`Test-PerformanceTargets.ps1`](../Test-PerformanceTargets.ps1) |

---

*Next: [Benchmark Suites](Benchmark-Suites) · [FAQ](FAQ)*