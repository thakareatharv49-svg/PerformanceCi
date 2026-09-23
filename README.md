# PerformanceCI

A professional-grade, **zero-allocation** performance-gating library for .NET. Declare budgets as attributes or inline asserts, run benchmarks with BenchmarkDotNet, and let CI fail the build the moment a measured operation regresses — the same "assert false" semantics as a unit-test framework, but for performance.

## Why PerformanceCI?

Performance regressions are silent until they are painful. `PerformanceCI` makes them **loud and structural**:

- **Assert-equivalent semantics** — a budget breach throws, which fails the benchmark run, which fails the build. A perf regression blocks a merge exactly like a failing test does.
- **Zero allocation on every hot path** — gates, watchdogs, and the event bus are measured code themselves; they add no noise to what you are measuring.
- **Text-stream reports** — reports stream straight to a `TextWriter` (file or stdout) with no intermediate report strings, so even the reporting phase is allocation-light.

## When to use X instead of Y

| Need | Use | Why / when |
|---|---|---|
| Reject a call that takes too long | [`PerfGate.AssertTime`](#perfgateasserttime) | One-shot measurement around a delegate. Golden for tests and pre-flight checks. |
| Reject a call that allocates too much | [`PerfGate.AssertAllocations`](#perfgateassertallocations) | Uses `GC.GetAllocatedBytesForCurrentThread()` — a zero-allocation probe. |
| Measure + assert inside production hot code, allocation-free | [`PerfWatchdog.Start` / `StartAndThrow`](#perfwatchdogstart--startandthrow) | `ref struct` + `using` scope: the compiler keeps it entirely on the stack. |
| Cheapest possible breach hook (zero delegate indirection) | [`PerfWatchdogNative.Start`](#perfwatchdognativestart-unsafe) | Needs `unsafe`; invokes a raw `delegate*<long,long,void>` on breach. |
| Observe breaches without failing | [`PerfHeavy.Violation`](#perfheavyviolation-event-bus) | Subscribe once, get notified on every breach. Never throws by itself. |
| Declare budgets on benchmarks, enforced after a BenchmarkDotNet run | [`PerfCriticalAttribute`](#perfcriticalattribute) | The BDN-runner integration (`Program.cs` + `PerfConsoleGate`). |
| Cap bytes allocated by a benchmark | [`PerfAllocationAttribute`](#perfallocationattribute) | Same runner integration, byte budgets. |
| "This must not regress vs a sibling method" | [`PerfBaselineAttribute` + `PerfComparisonAttribute`](#perfbaselineattribute--perfcomparisonattribute) | Ratio gate: challenger ≤ ratio × baseline mean. |
| Show a number in the report but never gate on it | [`PerfIgnoreAttribute`](#perfignoreattribute) | Reference-only rows; never fails the verdict. |
| Compare against a hard budget **or** a sibling | Both `PerfCriticalAttribute` **and** `PerfComparisonAttribute` on one method | These compose; budget and ratio check independently. |

### Decision tree

```
Do I need to fail fast on THIS call site? ──yes──► PerfWatchdog.StartAndThrow / PerfGate.AssertTime
        │no (measurement), but keep the hot path clean ──► PerfWatchdog.Start
        │no (benchmark-time), reflect over declared budgets ──► attributes + PerfConsoleGate
        │no (observe only) ──► PerfHeavy.Violation subscription
```

## Projects

| Project | Purpose |
|---|---|
| `PerformanceCI.Core` | The library: attributes, `PerfGate`, `PerfWatchdog`, `PerfReport`, `PerfConsoleGate`. Ships as a NuGet package. |
| `PerformanceCI.Benchmarks` | Portable BenchmarkDotNet scenarios (`PortableBenchmarks`) that run without a Windows host. |
| `PerformanceCI.Integration` | SQLite-backed scenario (`SqliteVocabularyBenchmarks`) with `[PerfCritical]`/`[PerfAllocation]` gates. |
| `PerformanceCI.Tests` | xUnit suite covering the Core API. 98%+ line coverage on Core (see `TestResults/`). |

## Quick start

### 1. Declare budgets with attributes (benchmark-time)

```csharp
[MemoryDiagnoser]
[PerfCritical(2000)]            // class-level budget: 2000 ns
[PerfAllocation(512)]           // class-level budget: 512 B
public class PortableBenchmarks
{
    [Benchmark]
    [PerfCritical(1000)]         // method overrides class: 1000 ns
    public void CachePrediction() { /* ... */ }

    [Benchmark]                  // inherits class 2000 ns / 512 B gates
    public void NormalizePhrase() => "I Need A Break".ToLowerInvariant();

    [Benchmark]
    [PerfCritical(500)]
    [PerfBaseline("phrase")]
    public string BaselinePhrase() => "hello";

    [Benchmark]
    [PerfComparison(maxRatio: 2.0, group: "phrase")]
    public string ChallengerPhrase() => "hello" + " world";

    [Benchmark]
    [PerfIgnore]                 // report-only: never gates
    public string ReferenceOnly() => "always shown, never failed";
}
```

### 2. Run benchmarks and turn the verdict into an exit code

```csharp
public static int Main(string[] args)
{
    var summary = BenchmarkRunner.Run<PortableBenchmarks>();

    var meanNs = new Dictionary<string, double>(StringComparer.Ordinal);
    var meanBytes = new Dictionary<string, double>(StringComparer.Ordinal);
    foreach (var report in summary.Reports)
    {
        meanNs[report.BenchmarkCase.Descriptor.WorkloadMethod.Name]
            = report.ResultStatistics?.Mean ?? double.NaN;
        if (report.Metrics is not null
            && report.Metrics.TryGetValue("Allocated", out var metric) && metric.Value is double b)
            meanBytes[report.BenchmarkCase.Descriptor.WorkloadMethod.Name] = b;
    }

    return PerfConsoleGate.RunAndReport(
        typeof(PortableBenchmarks), meanNs, meanBytes,
        reportPath: Path.Combine(AppContext.BaseDirectory, "perf-report.txt"));
}
```

`RunAndReport` returns **0 when every gate passed, 1 when any breached** — wire it into CI:

```powershell
dotnet run --project PerformanceCI.Integration -c Release   # exit code gates the build
```

### 3. Inline asserts inside tests or production code

```csharp
PerfGate.AssertTime(() => FullTextSearch(query), budgetNs: 50_000, operation: "search");

PerfGate.AssertAllocations(() => index.Build(), budgetBytes: 4096, operation: "index-build");
```

### 4. Allocation-free scoped measurement

```csharp
using (var watchdog = PerfWatchdog.StartAndThrow(150_000, "publish-batch"))
{
    Publish(records);
}
```

`Start` measures-and-notifies (never throws); `StartAndThrow` adds the assert. Both are `ref struct`s — zero heap traffic.

### 5. Observe breaches without failing

```csharp
PerfHeavy.Violation += (in PerfSample sample) =>
    telemetry.Record(sample.Operation, sample.ElapsedNanoseconds, sample.BudgetNanoseconds);
```

## API reference

### PerfGate

`PerfGate` is the low-level, branch-predictable assertion primitive.

| Member | Description |
|---|---|
| `long NowTicks()` | Current QPC timestamp. Zero allocation. |
| `long ElapsedNanoseconds(start, end)` | Nanoseconds between two `NowTicks`. |
| `long ElapsedTicks(start, end)` | Raw ticks — the fastest elapsed measure, no conversion. |
| `long NanosecondsToTicks(ns)` | Bake a ns budget into the tick domain once, so hot comparisons skip the multiply. |
| `bool Exceeded(elapsedNs, budgetNs)` | `elapsedNs > budgetNs`. |
| `void Ensure(elapsedNs, budgetNs, operation?)` | Assert; throws `PerfViolationException` on breach. One compare+branch on the pass path. |
| `void EnsureTicks(elapsedTicks, budgetTicks, operation?)` | Assert in tick domain — zero conversion on the pass path. |
| `long AssertTime(Action, budgetNs, operation?)` | Measure + `Ensure`. The closure may allocate. |
| `long AssertAllocations(Action, budgetBytes, operation?)` | `GC.GetAllocatedBytesForCurrentThread()` delta + assert. |

**Hot-path usage** — measure in the tick domain and assert in it too:

```csharp
long start = PerfGate.NowTicks();
DoWork();
PerfGate.EnsureTicks(PerfGate.ElapsedTicks(start, PerfGate.NowTicks()),
                     PerfGate.NanosecondsToTicks(200_000), "DoWork");
```

Only the compare and branch execute when the work is under budget.

### PerfWatchdog / PerfWatchdogNative

`PerfWatchdog` is a `readonly ref struct` — `using`/scope never boxes it, so the whole lifecycle is stack-only. Constructing it bakes the budget into ticks once; disposal is one QPC read + one subtract + one compare on the pass path.

| Entry | Behavior on breach |
|---|---|
| `PerfWatchdog.Start(budgetNs, operation?)` | Raises `PerfHeavy.Violation` only. |
| `PerfWatchdog.StartAndThrow(budgetNs, operation?)` | Raises `PerfHeavy.Violation` **and** throws `PerfViolationException`. |
| `PerfWatchdogNative.Start(budgetNs, delegate*<long,long,void>, operation?)` (unsafe) | Invokes a raw function pointer, then notifies the bus. |

**When to use native:** when you need the absolute lowest-ceremony breach hook and the caller accepts `unsafe`. The function pointer has no delegate object and no thunk. Use the managed ref struct in every other case.

### PerfHeavy

`static event PerfHeavyHandler? Violation` where `PerfHeavyHandler` is `void (in PerfSample)`. The sample is passed by-ref — a notification is one static event-field read + one indirect call, with zero heap traffic. The event field is `null` (one branch) until subscribed, so an unobserved run costs nothing.

### PerfReport / PerfConsoleGate

| Member | Description |
|---|---|
| `PerfReport.WriteText(TextWriter, ReadOnlySpan<PerfMeasurement>)` | Streams the report, allocating per-row (numbers flow through a reused `stackalloc` buffer). |
| `PerfReport.ToText(ReadOnlySpan<PerfMeasurement>)` | Returns the report as one string (one allocation). |
| `PerfReport.Evaluate(...)` | Verdict `true`/`false` + violation counts, skipping `PerfIgnore` rows. |
| `PerfConsoleGate.RunAndReport(Type, meanNs, meanBytes, reportPath?)` | Evaluates declared attribute budgets against measured means, writes a report, returns 0/1. |

Output columns: `Name: <mean> ns / budget <budget> ns\t<mean> B / budget <budget> B\t<status>` with status one of `pass`, `VIOLATION`, `ignored`, `info`.

### Attributes

| Attribute | Target | Validation | Override / inheritance |
|---|---|---|---|
| `PerfCriticalAttribute(int ns)` | method, class | `ns > 0`, else `ArgumentOutOfRangeException` | method overrides class; `Inherited = true` |
| `PerfAllocationAttribute(int bytes)` | method, class | `bytes > 0` | method overrides class; `Inherited = true` |
| `PerfBaselineAttribute(string? group = "")` | method, class | — | marks the comparison baseline |
| `PerfComparisonAttribute(double maxRatio, string group = "")` | method, class | `maxRatio > 1.0` and not NaN | pairs challenger with baseline |
| `PerfIgnoreAttribute` | method, class | — | report-only; suppresses all gates |

**Important nuance:** because the attributes are `Inherited = true`, a method with no attribute of its own inherits the class-level one — that is how class budgets propagate (`NormalizePhrase` above gets the class `2000 ns / 512 B` gates). A method-level attribute always wins.

## When to use X instead of Y — in depth

### `PerfGate.AssertTime` vs `PerfWatchdog.StartAndThrow`

- `AssertTime` is one-shot: measure a delegate, assert, done. It returns the measured ns. The delegate invocation itself is not measured storage — but if `action` captures locals, **the closure allocates**, polluting an allocation measurement. Use it for time-only checks in tests and pre-flight code.
- `StartAndThrow` is scoped and allocation-free. It records a *start tick*, and `Dispose` (triggered by the scope exit) measures the window. Use it on production hot paths, and you can wrap a `try`/`finally`-free `using` block around logically grouped work. Prefer `Ensure`/`EnsureTicks` when you already hold timestamps.

### `PerfHeavy.Violation` vs a throwing gate

The event bus never inherits your failure policy. Subscribe and decide: log, count, alert, or re-raise as your own exception. A throwing gate (`StartAndThrow` / `Ensure`) embeds the failure policy in the measurement site. For production observability with hard test gating, keep both: watchdogs notify the bus in prod; benchmarks/users assert in tests.

### Attribute budgets vs inline `PerfGate`

- **Attributes** apply at benchmark/CI time: they are static metadata read once by the runner and cannot be bypassed by a fast path skipping code. Use them to make a *project's* performance contract explicit and enforceable in CI.
- **`PerfGate`** applies *now, inline*: the cost is enforced at the exact call site, useful for a function whose performance is load-bearing and cannot wait for a benchmark run to reflect it.

### `PerfIgnore` vs a missing attribute

Both produce an "info" row with no gate. The difference is intent and ordering: `PerfIgnore` marks *deliberate* report-only signals (e.g. reference totals), while a missing attribute means "no budget declared." `PerfIgnore` also suppresses class-level inherited gates — relevant when a class default would otherwise apply.

## Test coverage

`PerformanceCI.Tests` (xUnit) covers the Core API — gates, exceptions (lazy culture-invariant messages), watchdogs (including the unsafe native scope), reports, evaluator budget resolution, comparison ratios, baseline pairing, and CI verdicts. Generating coverage:

```powershell
dotnet test PerformanceCI.Tests -c Release --collect:"XPlat Code Coverage" --results-directory TestResults
```

Current result: **98%+ line coverage** on `PerformanceCI.Core`. Parallelization is disabled in the test project because `PerfHeavy` is a static bus and tests capture `Console.Out` — deterministic single-threaded runs by design.

## Running benchmarks and gate CI locally

```powershell
# 1. Run both suites; the exit code is the perf verdict (0 = green).
dotnet run --project PerformanceCI.Benchmarks -c Release
dotnet run --project PerformanceCI.Integration -c Release

# 2. Enforce hard time/byte targets from BenchmarkDotNet CSV output.
.\Test-PerformanceTargets.ps1

# 3. Regenerate the performance section of this README from perf-report.txt files.
.\Update-PerformanceReport.ps1
```

## CI

`.github/workflows/performanceCI.yml` (`.NET 10`, `windows-latest`): restore → build → **test** → pack `PerformanceCI.Core` → push to GitHub Packages. The benchmark runners are intentionally not CI step here (they are machine-sensitive); run `Test-PerformanceTargets.ps1` on dedicated hardware when you want hard gating.

## Installation

Every push to `main` publishes a `PerformanceCI.Core.nupkg` to the GitHub Packages feed. Add the source and credentials, then reference the package:

```powershell
dotnet nuget add source "https://nuget.pkg.github.com/Divide-By-Zero-Solutions/index.json" -n github --username <your-github-username> --password <PAT-with-read:packages>
dotnet add package PerformanceCI.Core
```

> `GITHUB_TOKEN` works inside Actions only; a PAT is required for local restore. `--skip-duplicate` keeps the publish idempotent — bump `Version` in `PerformanceCI.Core.csproj` to ship a new one.