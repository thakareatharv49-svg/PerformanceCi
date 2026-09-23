# Watchdogs

`PerfWatchdog` is the **scoped, allocation-free** measurement primitive. It measures a `using` block's lifetime against a budget and reacts on breach — either notifying observers, throwing, or invoking a raw function pointer.

---

## `PerfWatchdog` (managed, safe)

A `readonly ref struct`:

- `using`/scope semantics — never boxed, **whole lifecycle on the stack**.
- Construction bakes the ns budget into the tick domain once.
- Pass path on `Dispose`: one QPC read + one integer subtract + one integer compare.

| Starter | On breach |
|---|---|
| `PerfWatchdog.Start(budgetNs, operation?)` | Raises `PerfHeavy.Violation` only — never throws. |
| `PerfWatchdog.StartAndThrow(budgetNs, operation?)` | Raises `PerfHeavy.Violation` **and** throws `PerfViolationException`. |

### `Start` — observe without failing

```csharp
using (var _ = PerfWatchdog.Start(150_000, "publish-batch"))
{
    Publish(records);
}
```

A slow batch only fires the `PerfHeavy` event; your code path completes.

### `StartAndThrow` — assert-equivalent, inline

```csharp
using var watchdog = PerfWatchdog.StartAndThrow(150_000, "publish-batch");
Publish(records);
```

Slower than 150 µs ⇒ `PerfViolationException`, bubbling out of the `using` scope with the measured elapsed nanoseconds and budget intact on the exception.

### Elapsed, mid-window

```csharp
using var watchdog = PerfWatchdog.Start(5_000_000);
DoWork();
if (watchdog.ElapsedNanoseconds > 4_000_000)   // progressive check before scope ends
    Log("approaching budget");
```

---

## `PerfWatchdogNative` (unsafe)

Lowest-ceremony breach hook the library offers: the callback is a raw **managed function pointer** (`delegate*<long, long, void>`) — no delegate object, no thunk.

```csharp
public static unsafe PerfWatchdogNativeScope Start(
    long budgetNs,
    delegate*<long, long, void> onViolation,
    string? operation = null)
```

```csharp
// Callback signature: (elapsedNs, budgetNs) -> void
[MethodImpl(MethodImplOptions.AggressiveOptimization)]
static unsafe void OnBreach(long elapsedNs, long budgetNs)
    => Debug.WriteLine($"breach {elapsedNs} > {budgetNs}");

static unsafe void Guard()
{
    using var scope = PerfWatchdogNative.Start(200_000, &OnBreach, "native");
    Work();
}
```

Behavior on breach: invoke the function pointer (when non-`null`), then notify `PerfHeavy.Violation`. A `null` pointer is legal for measure-only.

| | `PerfWatchdog` | `PerfWatchdogNative` |
|---|---|---|
| Requires `unsafe` | No | Yes |
| Breach hook | event bus (and optionally throw) | plain function pointer |
| Overhead | one event-field read + indirect call | register call only |
| Best for | 99% of usage | ultra-low-ceremony, `unsafe`-accepting, hot loops |

---

## `PerfHeavy` — the event bus

`PerfHeavy.Violation` is a process-wide `event PerfHeavyHandler?`:

```csharp
public delegate void PerfHeavyHandler(in PerfSample sample);
```

```csharp
PerfHeavy.Violation += (in PerfSample sample) =>
    Telemetry.Record(sample.Operation, sample.ElapsedNanoseconds, sample.BudgetNanoseconds);
```

- The sample is passed **by reference** (`in`) — one static event-field read + one indirect call, **zero heap traffic** on the measured path.
- The event field is `null` (a single branch) until subscribed — an unobserved run pays nothing.
- `PerfSample` (a `readonly struct`) exposes `ElapsedNanoseconds`, `BudgetNanoseconds`, `Operation`, and `Violated`.

### `PerfHeavy.Clear()`

Unsubscribes every handler — useful to fully silence a noisy section:

```csharp
PerfHeavy.Clear();
```

> **Testing note:** `PerfHeavy` is a static bus and tests often capture `Console.Out`. The test project therefore runs **single-threaded** (`CollectionBehavior(DisableTestParallelization = true)`) for deterministic results.

---

## When the watchdog fires

All three termination modes feed the same pipeline on breach:

1. Compute `elapsedNs = ElapsedNanoseconds(startTick, startTick + elapsedTicks)`.
2. `PerfHeavy.NotifyViolation(elapsedNs, budgetNs, operation)` — always.
3. `StartAndThrow` also throws `PerfViolationException`.
4. `PerfWatchdogNative` additionally invokes the function pointer.

The **excessive branch** (`Dispose`) is optimized (`AggressiveOptimization`) and only ever runs the compare on the pass path.

---

## Decision: watchdog vs `PerfGate`

| Situation | Use |
|---|---|
| I own the timestamps / measure manually | `PerfGate.Ensure` / `EnsureTicks` |
| One-shot delegate measure + assert | `PerfGate.AssertTime` / `AssertAllocations` |
| Measure a scope in production code, no allocation | `PerfWatchdog.Start` / `StartAndThrow` |
| Observe every breach process-wide | `PerfHeavy.Violation` subscription |
| Absolute minimal hook, `unsafe` acceptable | `PerfWatchdogNative.Start` |

---

*Next: [Reports & Evaluators](Reports-&-Evaluators) · [When to Use What](When-to-Use-What)*