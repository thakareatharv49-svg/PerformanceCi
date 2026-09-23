# Testing

The library verifies itself with a dedicated xUnit project, `PerformanceCI.Tests` — 98%+ line coverage across `PerformanceCI.Core`.

---

## Project layout

| File | Focus |
|---|---|
| `PerformanceCI.Tests.csproj` | net10.0, xunit 2.9.3, `AllowUnsafeBlocks` (for the native watchdog), coverlet collector, `ProjectReference` to Core |
| `AssemblyInfo.cs` | `CollectionBehavior(DisableTestParallelization = true)` |
| `PerformanceAttributesTests.cs` | attribute validation rules (`ArgumentOutOfRangeException` cases) |
| `PerfGateTests.cs` | gate primitives + exception members |
| `PerfWatchdogTests.cs` | `PerfSample`, `PerfHeavy` bus, managed + native watchdogs |
| `PerfReportTests.cs` | `WriteText`/`ToText`/`Evaluate`, invariant formatting, statuses |
| `PerfEvaluatorTests.cs` | budget resolution, inheritance, comparisons, verdicts |

Access to internals (e.g. `PerfSample`, `PerfHeavy.NotifyViolation`, internal `PerfMeasurement` ctor) comes from `InternalsVisibleTo` declared in the Core csproj:

```xml
<ItemGroup>
  <InternalsVisibleTo Include="PerformanceCI.Tests" />
</ItemGroup>
```

---

## Why parallelization is disabled

- `PerfHeavy` is a **static, process-wide bus** — tests subscribe/clear it.
- Tests capture `Console.Out` (e.g. `PerfConsoleGate` with `reportPath: null`).

Running single-threaded (`CollectionBehavior`) makes every run deterministic instead of racing on shared state.

---

## Running the suite

```powershell
# Quick
dotnet test PerformanceCI.Tests -c Release

# With line/branch coverage into TestResults/
dotnet test PerformanceCI.Tests -c Release `
  --collect:"XPlat Code Coverage" `
  --results-directory TestResults
```

Coverage is collected with **coverlet** (`coverlet.collector` + cobertura output) — inspect `TestResults/**/coverage.cobertura.xml` for per-class breakdown.

---

## Current coverage (Core)

| Class | Line coverage |
|---|---|
| `PerfGate` | 100% |
| `PerfViolationException` / `PerfAllocationViolationException` | 100% (incl. lazy invariant messages) |
| `PerfWatchdog` / `PerfWatchdogNativeScope` / `PerfSample` / `PerfHeavy` | 100% |
| `PerfReport` / `PerfConsoleGate` / `PerfVerdict` | 100% / 100% / 100% |
| `PerfEvaluator` | ~96% |
| All attributes | 100% |
| **Overall (line)** | **98%+** |

The few uncovered lines are deliberate: `TryFormat` fallbacks for numbers that cannot exceed the stack buffer, and the `else if (classCritical)` branch made unreachable by `Inherited = true` inheritance (the method-level `GetCustomAttribute(inherit:true)` already surfaces the class attribute). They are defensive code, not tested behavior.

---

## Test surface highlights

### Attributes

```csharp
[Theory]
[InlineData(0)]
[InlineData(-1)]
public void Critical_Budget_Must_Be_Positive(int ns)
    => Assert.Throws<ArgumentOutOfRangeException>(() => new PerfCriticalAttribute(ns));

[Fact]
public void Comparison_Ratio_Rejects_One_And_Nan()
{
    Assert.Throws<ArgumentOutOfRangeException>(() => new PerfComparisonAttribute(1.0));
    Assert.Throws<ArgumentOutOfRangeException>(() => new PerfComparisonAttribute(double.NaN));
}
```

### Gates

```csharp
[Fact]
public void Ensure_Passes_When_At_Budget()
    => PerfGate.Ensure(100, 100, "at-limit");           // no throw

[Fact]
public void Ensure_Ticks_Passes_Nanosecond_Budget_Conversion()
    => PerfGate.EnsureTicks(PerfGate.NanosecondsToTicks(50), PerfGate.NanosecondsToTicks(100));
```

### Watchdogs

```csharp
[Fact]
public void Start_Notifies_Bus_On_Breach()
{
    PerfHeavy.Violation += (in PerfSample s) => observed = s;
    using (PerfWatchdog.Start(1, "tiny"))
        Spin(50_000);                                   // exceeds 1 ns window
    Assert.True(observed.Violated);
}
```

### Reports & evaluator

```csharp
[Fact]
public void Evaluate_Ignores_Report_Only_Rows()
{
    var rows = new[]
    {
        Ms("ign", meanNs: 999_999, budgetNs: 1, ignored: true),
        Ms("ok",  meanNs: 10, budgetNs: 100)
    };
    Assert.True(PerfReport.Evaluate(rows, out int t, out int a));
    Assert.Equal(0, t);
}
```

(The suite builds `PerfMeasurement` rows through an internal `Ms(...)` helper backed by `InternalsVisibleTo`; public consumers get measurements from `PerfEvaluator.BuildMeasurements`.)

Full sample set lives in the project. Coverage is generated per commit in CI via the workflow's `dotnet test` step.

---

## Gotchas that informed the suite

1. **Culture invariance** — `Message` must read `50.0%` (not `50,0%`) on every machine. Tests pin the invariant formatting under the machine's real culture.
2. **`\r\n` in streamed reports** — `EndsWith` assertions must account for platform newlines (`Assert.EndsWith("\tpass\r\n", ...)` / `Contains`).
3. **Time-source sensitivity** — NS-scale budgets (`Start(1)`) are only stable with artificial spin; the suite gives a tiny budget and a *long* spin so the compare is decisive.
4. **`ImmutableArray` has `Length`, not `Count`** — the BDN `Summary.Reports` API bit the runner code once and is now pinned by the tests/build.

---

*Next: [FAQ](FAQ) · Back to [Home](Home)*