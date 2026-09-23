# PerformanceCI — Wiki Home

> **PerformanceCI** is a professional-grade, zero-allocation performance-gating library for .NET. Declare budgets, run BenchmarkDotNet suites, and let CI fail the build the moment an operation regresses — with the same "assert false" semantics as a unit-test framework.

---

## 📚 Documentation Map

| Page | What you'll find |
|---|---|
| [**Getting Started**](Getting-Started) | Install, wire a benchmark runner, first green/red run. |
| [**Attributes Reference**](Attributes) | `PerfCritical`, `PerfAllocation`, `PerfBaseline`, `PerfComparison`, `PerfIgnore` — semantics, validation, inheritance. |
| [**Inline Gates**](Inline-Gates) | `PerfGate` — assert time/allocations at the exact call site. Hot-path usage. |
| [**Watchdogs**](Watchdogs) | `PerfWatchdog`, `PerfWatchdogNative`, and the `PerfHeavy` event bus. Allocation-free scoped measurement. |
| [**Reports & Evaluators**](Reports-&-Evaluators) | `PerfReport`, `PerfEvaluator`, `PerfVerdict`, `PerfConsoleGate` — text-stream reporting and CI exit codes. |
| [**Comparison Gating**](Comparison-Gating) | Baseline/challenger ratio gates: "must not regress vs a sibling." |
| [**When to Use What**](When-to-Use-What) | Decision tree + guided comparisons of every API choice. |
| [**Benchmark Suites**](Benchmark-Suites) | `PerformanceCI.Benchmarks` (portable) and `PerformanceCI.Integration` (SQLite-backed). |
| [**CI & Automation**](CI-&-Automation) | GitHub Actions workflow, `Test-PerformanceTargets.ps1`, `Update-PerformanceReport.ps1`. |
| [**Testing**](Testing) | The xUnit suite, coverage, and how the library verifies itself. |
| [**FAQ**](FAQ) | Common questions, gotchas, and troubleshooting. |

---

## Quick Orientation

| Project | Role |
|---|---|
| **PerformanceCI.Core** | The library. Attributes, gates, watchdogs, reports. Ships as `PerformanceCI.Core` NuGet. |
| **PerformanceCI.Benchmarks** | Portable BenchmarkDotNet scenarios (`PortableBenchmarks`) — no Windows host required. |
| **PerformanceCI.Integration** | SQLite-backed scenario (`SqliteVocabularyBenchmarks`) with real gates. |
| **PerformanceCI.Tests** | xUnit suite covering the Core API (98%+ line coverage). |

**Two ways to write a performance contract:**

1. **Declarative (attributes + runner)** — for benchmark/CI enforcement. Reflect once, gate every run.
2. **Imperative (`PerfGate` / `PerfWatchdog`)** — for load-bearing call sites. Enforced *right now*, inline.

Both funnel into the same primitives: a compare, a possible violation, and a report.

```csharp
// Imperative — assert inline.
PerfGate.AssertTime(() => Query(), budgetNs: 50_000, operation: "search");

// Declarative — enforced by the runner after a BenchmarkDotNet run.
[PerfCritical(50_000)]
public void Query() { /* ... */ }
```

---

## Core Concepts

### Budgets
A **budget** is an upper bound — in **nanoseconds** for time, in **bytes** for allocation. The gate fires when `measured > budget`. Breach = `PerfViolationException` (time) or `PerfAllocationViolationException` (allocation), both with **lazy, culture-invariant messages** — nothing is formatted on the hot path.

### Assert-equivalent semantics
Failing a performance gate **throws**. A thrown exception inside the runner fails the benchmark exe, which fails the CI step. A merge that slows a hot path down is blocked exactly like a merge that breaks a unit test.

### Zero-allocation measurability
The library measures *and is measured*. Gates, watchdogs, and the event bus allocate nothing, so they add no noise to what you profile.

### Inheritance
All attributes are `Inherited = true`. A method with no attribute of its own inherits the class-level budget; a method-level attribute always wins. This is how `[PerfCritical(2000)]` on a class gates every non-annotated method.

---

## Latest Report

```text
# PerformanceCI Report

CachePrediction:	123.4 ns / budget 1000 ns	512.0 B / budget 1024 B	pass
NormalizePhrase:	45.6 ns / budget 2000 ns	0.0 B / budget 512 B	pass
BaselinePhrase:	50.0 ns / budget 500 ns	0.0 B	pass
ChallengerPhrase:	145.0 ns / budget 100 ns	0.0 B	VIOLATION
ReferenceOnly:	12.3 ns	0.0 B	ignored
```

`PerfConsoleGate.RunAndReport` prints `time violations / allocation violations -> PASS|FAIL` and returns `0` on pass, `1` on breach.

---

## Repository Links

- **Source & Issues:** <https://github.com/Divide-By-Zero-Solutions/PerformanceCi>
- **CI pipeline:** `.github/workflows/performanceCI.yml`
- **Local benchmarks:** [`Test-PerformanceTargets.ps1`](../Test-PerformanceTargets.ps1) · [`Update-PerformanceReport.ps1`](../Update-PerformanceReport.ps1)

---

*PerformanceCI — make performance regressions as visible as logic bugs.*