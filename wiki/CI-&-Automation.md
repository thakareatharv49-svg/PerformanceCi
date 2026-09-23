# CI & Automation

The repository ships a complete automation stack: a GitHub Actions pipeline, a hard-target enforcement script, and a report-regeneration script.

---

## GitHub Actions workflow

`.github/workflows/performanceCI.yml` — `.NET 10`, `windows-latest`, runs on push to `main` and on PRs:

| Step | Action |
|---|---|
| `Setup .NET` | `dotnet-version: '10.0.x'` |
| Restore | `dotnet restore` |
| Build | `dotnet build -c Release --no-restore` |
| **Test** | `dotnet test -c Release --no-build` (the xUnit suite gates on 98%+ Core coverage targets) |
| Pack | `dotnet pack PerformanceCI.Core -c Release -o ./artifacts --no-build` |
| Publish | `dotnet nuget push ./artifacts/*.nupkg` → GitHub Packages (`Divide-By-Zero-Solutions` feed) — **`main` pushes only** |

> **Deployment guard:** the `Publish to GitHub Packages` step only runs on `push` to `main` (`github.event_name == 'push' && github.ref == 'refs/heads/main'`). Fork PRs never publish — their read-only tokens cannot, and a PR should never ship a package. PRs still build and run the full test suite.

> **Why are the BDN benchmark runners *not* a CI step here?** BenchmarkDotNet results are machine-sensitive; gate-checked on shared runners they produce flaky failures. The portable runners double as smoke tests locally; hard performance gates belong on dedicated hardware (see `Test-PerformanceTargets.ps1` below).

### Package publishing

Output identity:

```
Divide-By-Zero-Solutions/PerformanceCi → PerformanceCI.Core (GitHub Packages)
```

Every **push to `main`** builds, tests, packs `PerformanceCI.Core-<version>.nupkg`, and pushes it to the GitHub Packages NuGet feed:

```
https://nuget.pkg.github.com/Divide-By-Zero-Solutions/index.json
```

Consumers reference the package by adding the feed plus credentials:

```xml
<!-- NuGet.Config -->
<configuration>
  <packageSources>
    <clear />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
    <add key="github" value="https://nuget.pkg.github.com/Divide-By-Zero-Solutions/index.json" />
  </packageSources>
</configuration>
```

```bash
# Restore with a PAT that has read:packages (GITHUB_TOKEN works only in Actions).
dotnet nuget add source nuget.pkg.github.com/Divide-By-Zero-Solutions/index.json -n github --username YOUR_GITHUB_USERNAME --password YOUR_PAT
dotnet add package PerformanceCI.Core
```

`--skip-duplicate` makes the push idempotent — a version already published once is never overwritten. Version bumps come from `Version` in `PerformanceCI.Core.csproj` (currently `2.0.0`).

---

## `Test-PerformanceTargets.ps1` — hard gate enforcement

Parses BenchmarkDotNet CSV export and fails on any breach.

```powershell
# From the repo root, after running the BDN suites:
.\Test-PerformanceTargets.ps1
```

The script:

1. Locates the newest CSV per file pattern under `BenchmarkDotNet.Artifacts\results`.
2. Filters to the target method + job.
3. Converts the `Mean` cell to nanoseconds (handles `ns`, `us`, `ms`, `s`, `NA`).
4. Compares time and `Allocated` (e.g. `20MB`, `4KB`) against the declared maximums.
5. Exits `1` if anything is missing or exceeded — **a failing performance gate**.

### Wiring into your orchestrator

```powershell
.\Update-PerformanceReport.ps1        # benchmarks + README splice
if ($LASTEXITCODE -ne 0) { exit 1 }
.\Test-PerformanceTargets.ps1         # hard targets from CSV
if ($LASTEXITCODE -ne 0) { exit 1 }
```

---

## `Update-PerformanceReport.ps1` — README report regeneration

Maintains the live performance section of the repo `README.md` between markers:

```
<!-- PERFORMANCE-REPORT:START -->
... report ...
<!-- PERFORMANCE-REPORT:END -->
```

Each project's `perf-report.txt` (written to its own `bin/Release` tree by the runner) becomes a `### PerformanceCI.Benchmarks` / `### PerformanceCI.Integration` section with an environment header (`OS`, runtime, UTC timestamp). Uses `-SkipBenchmark` to re-splice from already-generated reports without re-running benchmarks.

---

## Local verification loop (dev)

```powershell
# 1. Everything compiles.
dotnet build PerformanceCI.sln -c Release

# 2. Unit/gate suite.
dotnet test PerformanceCI.Tests -c Release --no-build

# 3. Smoke the portable + integration runners.
dotnet run --project PerformanceCI.Benchmarks -c Release --no-build
dotnet run --project PerformanceCI.Integration -c Release --no-build

# 4. Hard targets (dedicated hardware) + README refresh.
.\Test-PerformanceTargets.ps1
.\Update-PerformanceReport.ps1
```

---

## Exit-code contract

| Runner / script | `0` | `1` |
|---|---|---|
| `PerformanceCI.Benchmarks` / `PerformanceCI.Integration` | every gate PASS | any time/alloc violation |
| `dotnet test` | all tests pass | any test fails |
| `Test-PerformanceTargets.ps1` | every target met | target exceeded / missing CSV |
| `Update-PerformanceReport.ps1` | report spliced | no `perf-report.txt` found |

---

*Next: [Testing](Testing) · [FAQ](FAQ)*