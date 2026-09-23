# FAQ

Answers to the questions that come up again and again.

---

## General

### What is PerformanceCI, in one sentence?
A zero-allocation .NET library that turns performance budgets into **assert-equivalent gates** — declare a budget on a benchmark method (or inline in code) and a breach fails the run, the CI step, and ultimately the merge.

### How is this different from `Stopwatch` + an `if`?
`Stopwatch` + `if` is fine for a one-off. PerformanceCI gives you: tick-domain budgets baked once (`NanosecondsToTicks`), zero-allocation scoped measurement (`PerfWatchdog`), a process-wide observer bus (`PerfHeavy`), lazy culture-invariant exception messages, streaming allocation-light reports, attribute-driven CI verdicts, and comparison (baseline ratio) gating.

### Does it work without BenchmarkDotNet?
Yes. The **Core library has no BDN dependency**. `PerfGate`, `PerfWatchdog`, `PerfHeavy`, and `PerfReport` work anywhere. The *runner integration* (`PerfConsoleGate.RunAndReport`) is what takes `(method → mean)` dictionaries and attribute-resolves verdicts — you can feed it from BDN or from your own measurement loop.

---

## Budgets & gates

### What exactly happens on a breach?
- `PerfGate.Ensure` / `EnsureTicks` / `AssertTime` → throws `PerfViolationException`.
- `PerfGate.AssertAllocations` (or `[PerfAllocation]` exceeded via runner) → throws `PerfAllocationViolationException`.
- `PerfWatchdog.Start` → raises `PerfHeavy.Violation` only.
- `PerfWatchdog.StartAndThrow` → raises event **and** throws.
- Attribute gate exceeded in a runner → `PerfConsoleGate.RunAndReport` returns `1` and the row prints `VIOLATION`.

### Why nanoseconds, not milliseconds?
The library targets hot paths where sub-millisecond resolution matters and raw QPC ticks are natural. Report formatting still renders human-readable numbers; you can author budgets in any unit you like via `PerfGate.NanosecondsToTicks(ms * 1_000_000)`.

### Is `0` a valid budget?
For **time**: `PerfCritical(0)` throws `ArgumentOutOfRangeException` (budget must be positive). For **allocations**: `AssertAllocations(action, budgetBytes: 0)` is valid and means *"any allocation at all fails"* — but `PerfAllocationAttribute(0)` throws, because the attribute constructor requires positive values.

### Why does the inheritance story matter for class budgets?
All attributes are `Inherited = true`, so a method with no attribute of its own inherits the class one. `[PerfCritical(2000)]` on a class gates every non-annotated method. If that surprises your mental model, remember: **method always wins, class is the default, `[PerfIgnore]` is the opt-out**.

---

## Watchdogs & events

### Does `PerfWatchdog` allocate?
No. It is a `readonly ref struct` — it cannot be boxed or captured in a closure, so `using` keeps it entirely on the stack. Construction bakes the budget to ticks; disposal is a compare. The `PerfHeavy` notification path allocates nothing either (`in` by-ref sample).

### Should I throw in a `PerfHeavy.Violation` handler?
You can — but the default contract is *observe*. Throwing inside an event handler changes the failure mode of every subscribing measurement path. Prefer handling the decision in the handler, or use `StartAndThrow`/`Ensure` where the policy belongs to the measurement site.

### Can I un-subscribe everything?
Yes — `PerfHeavy.Clear()`.

---

## Comparisons

### What if my baseline regresses too?
The challenger's budget is `ceil(baselineMean × maxRatio)`, so it adapts upward. If you want an absolute floor, also put `[PerfCritical]` on the baseline (or on the challenger). See the [anti-patterns](When-to-Use-What#anti-patterns) table.

### Why is `maxRatio = PositiveInfinity` allowed?
It is a valid, documentable "challenger, no real ceiling yet" — checked and understood rather than silently skipped. Only `NaN` and `<= 1.0` throw.

### My comparison was skipped — why?
No matching baseline or a baseline mean of `NaN`. The challenger reports `info` (no budget). This is a quiet signal by design; treat `info` on a comparison row as a contract gap in CI review.

---

## Reporting & CI

### Where does `perf-report.txt` go?
Next to the runner binary: `PerformanceCI.Benchmarks/bin/Release/.../perf-report.txt` (and the same under `PerformanceCI.Integration`). `Update-PerformanceReport.ps1` splices those into `README.md`.

### Why is the report text-stream and not a rich object?
Streaming to a `TextWriter` allocates nothing per row; a full report string is one big allocation that would contaminate the measured process.

### Why don't the BDN runners run as GitHub Actions steps?
Results are machine-sensitive and flaky on shared runners. The Actions pipeline builds + runs the **unit suite** (deterministic); hard performance targets run on dedicated hardware via `Test-PerformanceTargets.ps1`.

### How do I gate my own CI on performance?
1. Run the runner: `dotnet run --project <suite> -c Release` — exit code `0`/`1`.
2. On hardware you trust: `.\Test-PerformanceTargets.ps1` (fails the step on breach).
3. `dotnet test` in the workflow keeps the library's own logic honest in every PR.

---

## Versioning & environment

### What .NET does PerformanceCI target?
`net10.0` (Dev-Optimized `Directory.Build.props` sets `GenerateDocumentationFile=true`; the Core library emits full XML docs on build).

### Which BenchmarkDotNet version?
0.15.8 in the runner projects. The Core library does **not** depend on it.

### How do I consume the package?
The GitHub Packages feed is in [`NuGet.Config`](../NuGet.Config). `dotnet add package PerformanceCI.Core`, then restore against `GITHUB_TOKEN` (or your PAT) for the `Divide-By-Zero-Solutions` scope.

---

## Troubleshooting

| Symptom | Cause / fix |
|---|---|
| Flaky `Start(1)` breaches | NS-scale budgets are sensitivity-bound; give a decisive window (e.g. `Start(1000)` + long spin) |
| `EndsWith("\tpass")` failing in my own tests | Platform `\r\n`; assert `EndsWith("\tpass\r\n")` or `Contains("\tpass\r\n")` |
| `50,0%` in exception messages | You formatted your own message with current culture; PerformanceCI's `Message` is invariant — verify via `e.Message` untouched |
| `ImmutableArray` has no `Count` | `Summary.Reports` is `ImmutableArray` — use `.Length` |
| Comparison silently `info` | Baseline missing / `NaN` mean — see *Comparisons* above |
| Package restore 401 | Don't use `GITHUB_TOKEN` locally; generate a PAT with `read:packages` for GitHub Packages |

---

*Back to [Home](Home) · Next: nothing — you've read the whole guide.*