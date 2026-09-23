# PerformanceCI.Core

Zero-allocation performance-gate attributes, scoped watchdogs, a heavy-event bus, and CI report writers for .NET.

## What it gives you

- **PerfGate** — assert time/allocations inline on any call site. Branch-predictable, zero-allocation checkers with assert-equivalent failure semantics (`PerfViolationException` / `PerfAllocationViolationException`, lazy culture-invariant messages).
- **PerfWatchdog / PerfWatchdogNative** — scoped, stack-only measurement (`ref struct`). Zero heap traffic; an optional raw `delegate*` breach hook for `unsafe` code.
- **PerfHeavy** — process-wide observer bus (`in PerfSample`) with zero-allocation notifications.
- **Performance attributes** — `PerfCriticalAttribute`, `PerfAllocationAttribute`, `PerfBaselineAttribute`, `PerfComparisonAttribute`, `PerfIgnoreAttribute`. Declare budgets on benchmark methods or classes (inherited); method overrides class.
- **PerfReport / PerfConsoleGate** — streaming, allocation-light text reports and a BDN-agnostic CI exit-code verdict.

## Quick start

```csharp
// Inline assert on a hot path.
PerfGate.AssertTime(() => FullTextSearch(q), budgetNs: 50_000, operation: "search");

// Allocation-free scope measurement.
using var watchdog = PerfWatchdog.StartAndThrow(150_000, "publish-batch");
Publish(records);

// Observe breaches without failing.
PerfHeavy.Violation += (in PerfSample s) => Telemetry.Record(s.Operation, s.ElapsedNanoseconds, s.BudgetNanoseconds);
```

## Attribute-driven CI verdict

```csharp
[PerfCritical(1000)]          // method-level budget: 1000 ns
[PerfAllocation(512)]         // byte budget
[PerfBaseline("phrase")]
public string BaselinePhrase() => "hello";

[PerfComparison(maxRatio: 2.0, group: "phrase")]
public string ChallengerPhrase() => "hello" + " world";
```

After a BenchmarkDotNet run, feed `(method → mean)` maps to `PerfConsoleGate.RunAndReport(...)` and use the returned `0`/`1` as your CI exit code.

## When to use what

| Need | Use |
|---|---|
| Reject a delegate that takes too long | `PerfGate.AssertTime` |
| Reject a delegate that allocates too much | `PerfGate.AssertAllocations` |
| Measure a scope, zero allocation | `PerfWatchdog.Start` / `StartAndThrow` |
| Observe breaches process-wide | `PerfHeavy.Violation` |
| Declare budgets for CI enforcement | `PerfCritical` / `PerfAllocation` / `PerfComparison` attributes |
| Show a number, never gate | `PerfIgnoreAttribute` |

Full documentation, a 12-page wiki, benchmark suites, and a 98%+ coverage test suite live in the repository at https://github.com/Divide-By-Zero-Solutions/PerformanceCi.