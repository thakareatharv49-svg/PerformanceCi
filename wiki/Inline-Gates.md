# Inline Gates (`PerfGate`)

`PerfGate` is the imperative counterpart to the attribute system. Where attributes gate **at benchmark/CI time**, `PerfGate` gates **at the exact call site, right now** — the same "assert false" failure semantics, inline in code.

Every checker is **branch-predictable, zero-allocation, and safe on a measured hot path**.

---

## API surface

| Member | Signature | Returns | Notes |
|---|---|---|---|
| `NowTicks` | `long NowTicks()` | raw QPC timestamp | `Stopwatch.GetTimestamp()`; no allocation |
| `ElapsedNanoseconds` | `long ElapsedNanoseconds(start, end)` | ns | converts ticks → ns via `NanosPerTick` |
| `ElapsedTicks` | `long ElapsedTicks(start, end)` | raw ticks | fastest elapsed measure — no conversion |
| `NanosecondsToTicks` | `long NanosecondsToTicks(ns)` | tick-domain budget | bake once; hot compares skip the multiply |
| `Exceeded` | `bool Exceeded(elapsedNs, budgetNs)` | `elapsedNs > budgetNs` | the primitive every gate shares |
| `Ensure` | `void Ensure(elapsedNs, budgetNs, operation?)` | — | throws `PerfViolationException` on breach |
| `EnsureTicks` | `void EnsureTicks(elapsedTicks, budgetTicks, operation?)` | — | tick-domain assert; zero conversion on pass |
| `AssertTime` | `long AssertTime(Action, budgetNs, operation?)` | measured ns | measures, asserts, returns elapsed ns |
| `AssertAllocations` | `long AssertAllocations(Action, budgetBytes, operation?)` | allocated bytes | `GC.GetAllocatedBytesForCurrentThread()` delta |

**Cost model:** the pass path is one compare + one branch. The throwing body is in `[MethodImpl(MethodImplOptions.NoInlining)]` helpers so it never inflates the hot call site. Fail-fast with zero try/finally ceremony.

---

## Hot-path usage (preferred)

Measure in the tick domain, assert in the tick domain — the elapsed computation never multiplies on the pass path:

```csharp
long start = PerfGate.NowTicks();
DoWork();
PerfGate.EnsureTicks(
    PerfGate.ElapsedTicks(start, PerfGate.NowTicks()),
    PerfGate.NanosecondsToTicks(200_000),        // bake the 200 µs budget once
    "DoWork");
```

For a once-per-call budget that is computed every call, the ns form is fine:

```csharp
long start = PerfGate.NowTicks();
DoWork();
PerfGate.Ensure(PerfGate.ElapsedNanoseconds(start, PerfGate.NowTicks()), 200_000, "DoWork");
```

---

## `AssertTime`

One-shot, measure-and-assert. Returns the measured nanoseconds so you can reuse it (e.g. logging).

```csharp
long elapsed = PerfGate.AssertTime(
    () => FullTextSearch(needle),
    budgetNs: 50_000,
    operation: "search");
Telemetry.Record("search", elapsed);
```

> **Watch out:** if `action` captures surrounding state, the compiler may allocate a **closure**. `AssertTime` measures *that* too. For allocation-sensitive checks prefer scoped [`PerfWatchdog`](Watchdogs) or assert on time only.

---

## `AssertAllocations`

Measures bytes allocated by the delegate via `GC.GetAllocatedBytesForCurrentThread()`, asserts, returns the delta.

```csharp
long bytes = PerfGate.AssertAllocations(
    () => index.Build(records),
    budgetBytes: 4096,
    operation: "index-build");
```

`budgetBytes: 0` is exact-value testing: any allocation at all throws `PerfAllocationViolationException`.

---

## Exceptions

| Gate | Exception | Message (lazy, invariant) |
|---|---|---|
| `Ensure` / `EnsureTicks` / `AssertTime` | `PerfViolationException` | `[PerfCI] {op} took {elapsed} ns (budget {budget} ns, {+0.0%} over).` |
| `AssertAllocations` (and `[PerfAllocation]`) | `PerfAllocationViolationException` | `[PerfCI] {op} allocated {bytes} B (budget {budget} B, {+0.0%} over).` |

Both expose rich properties — `Operation`, `ElapsedNanoseconds`/`AllocatedBytes`, and `Budget*` — and compute `Message` **lazily**, only when accessed. Formats use `CultureInfo.InvariantCulture` so `50.0%` is `50.0%` on every machine, never `50,0%`.

```csharp
try
{
    PerfGate.AssertTime(() => Run(), 1000, "run");
}
catch (PerfViolationException e)
{
    Log($"run breached: {e.ElapsedNanoseconds} > {e.BudgetNanoseconds}");
}
```

---

## Why ticks matter

`Stopwatch.Frequency` is the OS QPC rate (e.g. 10 MHz). Converting ns → ticks once per budget removes a multiply from every pass-path comparison, and the same conversion is baked into [`PerfWatchdog`](Watchdogs) construction. `ElapsedTicks` is strictly cheaper than `ElapsedNanoseconds` when you only need a compare.

---

*Next: [Watchdogs](Watchdogs) · [When to Use What](When-to-Use-What)*