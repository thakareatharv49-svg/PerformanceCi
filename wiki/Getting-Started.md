# Getting Started

This page walks you from an empty repository to a failing (then passing) performance gate.

---

## 1. Install

Add the NuGet package from the GitHub Packages feed configured in [`NuGet.Config`](../NuGet.Config):

```bash
dotnet add package PerformanceCI.Core
```

If you are cross-project compiling, reference the Core project directly:

```xml
<ProjectReference Include="..\PerformanceCI.Core\PerformanceCI.Core.csproj" />
```

## 2. Declare a budget on a benchmark

```csharp
using BenchmarkDotNet.Attributes;
using PerformanceCI;

[MemoryDiagnoser]
[PerfCritical(2000)]    // class-level: 2000 ns for every method without its own budget
[PerfAllocation(512)]   // class-level: 512 BYTES of allocation cap
[PerfComparison(maxRatio: 1.5, group: "lookup")]
public class MyBenchmarks
{
    [Benchmark]
    [PerfCritical(500)]  // method-level overrides the class budget
    public void HotPath() { /* ... */ }

    [Benchmark]
    [PerfBaseline("lookup")]
    public void BaselineLookup() { /* ... */ }

    [Benchmark]  // inherits class budget
    public string Normalize() => "HeLLo".ToLowerInvariant();
}
```

> **What gets measured at runner time:** elapsed nanoseconds *and* allocated bytes per method. Both are compared to every declared budget.

## 3. Wire the runner to return an exit code

Each benchmark project's `Program.cs` runs BenchmarkDotNet, maps `(method-name → mean value)` into dictionaries, and hands them to `PerfConsoleGate.RunAndReport`:

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

`RunAndReport`:
- Resolves every attribute budget against the measured mean.
- Writes a text report to `reportPath` (or `Console.Out` when `null`).
- Prints `[PerfCI] time violations: N, allocation violations: M -> PASS|FAIL`.
- Returns **`0` on pass, `1` on any breach**.

## 4. Run it

```powershell
dotnet run --project PerformanceCI.Benchmarks -c Release
```

Expected output tail:

```text
# PerformanceCI Report

HotPath:	123.4 ns / budget 500 ns	512.0 B / budget 512 B	pass
Normalize:	45.6 ns / budget 2000 ns	32.0 B / budget 512 B	pass
BaselineLookup:	50.0 ns / budget 2000 ns	0.0 B	pass
[PerfCI] time violations: 0, allocation violations: 0 -> PASS
```

Exit code `0`.

## 5. Make it fail (to prove the gate)

Lower a budget below the measured mean:

```csharp
[PerfCritical(50)]  // mean is 123.4 ns
public void HotPath() { /* ... */ }
```

Re-run:

```text
HotPath:	123.4 ns / budget 50 ns	512.0 B / budget 512 B	VIOLATION
[PerfCI] time violations: 1, allocation violations: 0 -> FAIL
```

Exit code `1` — your CI step fails, exactly like a red unit test.

> **Tip:** gate thresholds in CI with [`Test-PerformanceTargets.ps1`](../Test-PerformanceTargets.ps1) when you want hard time/byte targets on dedicated hardware. The BenchmarkDotNet runners themselves are machine-sensitive.

## Next steps

- Tailor gates: [`Attributes Reference`](Attributes)
- Guard a hot path inline: [`Inline Gates`](Inline-Gates)
- Observe without failing: [`Watchdogs`](Watchdogs)
- Form your own policy: [`When to Use What`](When-to-Use-What)