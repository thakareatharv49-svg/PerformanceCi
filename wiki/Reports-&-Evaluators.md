# Reports & Evaluators

The reporting layer is where "measured" becomes "documented and decided." It is deliberately **text-stream** — output goes straight to a caller-provided `TextWriter`, so even the reporting phase is allocation-light.

---

## Data model

### `PerfMeasurement` (readonly struct)

One measured entry destined for a report:

| Field | Meaning |
|---|---|
| `Name` | Simple method / operation name |
| `MeanNanoseconds` | Mean elapsed, ns (`NaN` when not measured) |
| `MeanAllocatedBytes` | Mean allocated bytes (`NaN` when not measured) |
| `BudgetNanoseconds` | Declared time budget, or `-1` (unbudgeted) |
| `BudgetBytes` | Declared byte budget, or `-1` (unbudgeted) |
| `IsBaseline` | Comparison baseline? |
| `Ignored` | `[PerfIgnore]` — report only, never gates |
| `TimeViolated` | `BudgetNs >= 0 && MeanNs > BudgetNs` |
| `AllocationViolated` | `BudgetBytes >= 0 && !NaN && MeanBytes > BudgetBytes` |

Value-typed and caller-buffer-friendly; the aggregation phase allocates nothing beyond the buffer itself.

---

## `PerfReport` — the writer

### `WriteText(TextWriter, span)` — streaming, zero-alloc per row

```csharp
using var file = File.CreateText("perf-report.txt");
PerfReport.WriteText(file, measurements);
```

- One `WriteLine` per measurement.
- Numbers format via `double.TryFormat` into a single reused `stackalloc` buffer — no per-row number strings.
- Rows: `Name:\t<mean ns> / budget <budget> ns\t<mean B> / budget <budget> B\t<status>`.

Status is one of:

| status | Meaning |
|---|---|
| `pass` | under every applied budget |
| `VIOLATION` | breach by time and/or allocation |
| `ignored` | `[PerfIgnore]` — report only |
| `info` | no budget declared |

Example output:

```text
# PerformanceCI Report

HotPath:	123.4 ns / budget 100 ns	512.0 B / budget 512 B	VIOLATION
Normalize:	45.6 ns / budget 2000 ns	0.0 B / budget 512 B	pass
ReferenceOnly:	12.3 ns	0.0 B	ignored
```

### `ToText(span)` — convenience, one allocation

```csharp
string report = PerfReport.ToText(measurements);
```

Returns the report as a single string. Allocates exactly the one result buffer.

### `Evaluate(span, out timeViolations, out allocationViolations)`

Verdict over a batch, **skipping ignored rows**:

```csharp
bool green = PerfReport.Evaluate(measurements, out int t, out int a);
```

`green == (t == 0 && a == 0)`.

---

## `PerfEvaluator` — the resolver

`PerfEvaluator.BuildMeasurements(type, meanNs, meanBytes)` turns a benchmark class + measured values into `PerfMeasurement[]`:

1. Reads class-level and method-level attributes (honoring `Inherited = true`).
2. Resolves `BudgetNanoseconds` and `BudgetBytes` per method (method wins over class).
3. Flags baseline and ignored entries.
4. Runs comparison gates (see [Comparison Gating](Comparison-Gating)) — challenger budgets become `ceil(baseMean × ratio)`.
5. Returns the array.

It is **BDN-agnostic**: feed it any `(name → value)` dictionaries and a `Type`, no BenchmarkDotNet dependency required.

---

## `PerfVerdict` — the primitive

`PerfVerdict.Pass(span, out timeViolations, out allocationViolations)` is the CI-facing verdict — pure delegation to `PerfReport.Evaluate`, `[AggressiveInlining]`, zero state.

---

## `PerfConsoleGate` — the end-to-end entry point

`PerfConsoleGate.RunAndReport(benchType, meanNs, meanBytes, reportPath)`:

```csharp
return PerfConsoleGate.RunAndReport(
    typeof(PortableBenchmarks), meanNs, meanBytes,
    reportPath: Path.Combine(AppContext.BaseDirectory, "perf-report.txt"));
```

1. Builds measurements.
2. Verdicts pass/fail.
3. Writes the text report to `reportPath` (or `Console.Out` when `null`).
4. Prints `[PerfCI] time violations: N, allocation violations: M -> PASS|FAIL`.
5. Returns **`0` pass / `1` fail** — the process exit code your CI reads.

---

## Why text-stream, not a big report object?

| Approach | Allocation per row | Failure mode |
|---|---|---|
| Build full report string, then write | O(rows) garbage | report phase contaminates benchmarks |
| Stream to `TextWriter` | 0 | output is bounded, incremental |

For CI you mostly want two things: a small exit code and a readable diff. `WriteText` gives both with the least garbage.

---

*Next: [Comparison Gating](Comparison-Gating) · [Testing](Testing)*