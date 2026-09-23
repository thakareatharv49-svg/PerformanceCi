# Benchmark Suites

Two BenchmarkDotNet projects ship with the repository, each demonstrating a different deployment posture for the gates.

| Project | Class | Deployment posture |
|---|---|---|
| `PerformanceCI.Benchmarks` | `PortableBenchmarks` | **Portable** — no Windows-only APIs, runs anywhere .NET runs |
| `PerformanceCI.Integration` | `SqliteVocabularyBenchmarks` | **Integration** — SQLite-backed I/O, isolates temporary data |

---

## Running

```powershell
# Run both; the exit code is the perf verdict (0 = green).
dotnet run --project PerformanceCI.Benchmarks -c Release
dotnet run --project PerformanceCI.Integration -c Release
```

Each `Program.Main`:

1. `BenchmarkRunner.Run<T>()` with `[MemoryDiagnoser]` (so per-method allocation means are captured).
2. Maps `{ workloadMethodName → meanNs }` and `{ workloadMethodName → meanBytes }`.
3. Calls `PerfConsoleGate.RunAndReport(typeof(T), meanNs, meanBytes, reportPath)`.

`perf-report.txt` is written next to the runner binary; the process exit code carries the verdict.

---

## `PerformanceCI.Benchmarks` — `PortableBenchmarks`

```csharp
[MemoryDiagnoser]
[SimpleJob(launchCount: 1, warmupCount: 1, iterationCount: 3)]
[PerfCritical(2000)]        // class-level budget
[PerfAllocation(512)]       // class-level byte budget
public class PortableBenchmarks
```

| Method | Budget | Demonstrates |
|---|---|---|
| `CachePrediction()` | `[PerfCritical(1000)]` | method-level override |
| `NormalizePhrase()` | class (2000 ns / 512 B) | class-inherited gates |
| `BaselinePhrase()` | `[PerfCritical(500)]` + `[PerfBaseline("phrase")]` | baseline side of a comparison |
| `ChallengerPhrase()` | `[PerfComparison(2.0, "phrase")]` | ratio gate vs baseline |
| `ReferenceOnly()` | `[PerfIgnore]` | report-only, never gates |

### Why `SimpleJob(1, 1, 3)`?

Short launch/warmup/iteration counts keep the suite *fast enough to run in the loop / on CI*. These portable scenarios are smoke-level gates; the Integration suite carries the heavier, I/O-bound work.

---

## `PerformanceCI.Integration` — `SqliteVocabularyBenchmarks`

```csharp
[PerfCritical(250_000)]     // 250 µs class-level budget for the I/O-bound suite
public class SqliteVocabularyBenchmarks
```

| Method | Budget | Demonstrates |
|---|---|---|
| `InsertTile()` | `[PerfCritical(150_000)]` + `[PerfAllocation(20_000)]` | real DB-write gate with a byte budget |
| `ReadByCategory()` | class-level 250 µs | filtered read gate via inheritance |

Setup/cleanup use `tiles`-table fixtures with a temporary database file, exercised under BDN's `[GlobalSetup]`/`[GlobalCleanup]`.

> **Isolation contract:** the Integration suite `Delete`s its temp DB in cleanup so it never leaks data between runs — per project convention, integration scenarios must isolate temporary data.

---

## Hard-target enforcement (`Test-PerformanceTargets.ps1`)

The BDN runners are machine-sensitive; on **dedicated hardware** you can enforce hard time/byte targets from the exported CSV:

```powershell
.\Test-PerformanceTargets.ps1
```

Current targets parsed from `BenchmarkDotNet.Artifacts\results\*-report.csv`:

| Benchmark | Method | Max time | Max bytes |
|---|---|---|---|
| `PortableBenchmarks` | `CachePrediction` | 1 000 ns | 512 B |
| `PortableBenchmarks` | `NormalizePhrase` | 1 000 ns | 512 B |
| `SqliteVocabularyBenchmarks` | `ReadByCategory` | 100 000 ns | 4 096 B |
| `SqliteVocabularyBenchmarks` | `InsertTile` | 250 ms | 20 MB |

- Reads the most recent matching CSV.
- Converts `Mean` to nanoseconds (handles `ns`/`us`/`ms`/`s` and `NA`).
- Compares both time and `Allocated`.
- Exits `1` (fails) if **any** target is exceeded or results are missing.

Update the `$targets` table inside the script to tune for your hardware.

---

## Report regeneration (`Update-PerformanceReport.ps1`)

```powershell
.\Update-PerformanceReport.ps1          # run benchmarks, then splice reports into README
.\Update-PerformanceReport.ps1 -SkipBenchmark   # only splice already-generated reports
```

Splices the `perf-report.txt` contents (one section per project) between the `<!-- PERFORMANCE-REPORT:START -->` / `<!-- PERFORMANCE-REPORT:END -->` markers in `README.md`. Prepends an environment line (`OS`, runtime, timestamp) so the report is reproducibly attributable.

---

*Next: [CI & Automation](CI-&-Automation) · [Testing](Testing)*