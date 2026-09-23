# Comparison Gating

Absolute budgets answer *"is this fast enough?"* Comparison gating answers the more interesting *"did we regress **relative to our own baseline**?"* — a `PerfComparisonAttribute` challenger is measured against a `PerfBaselineAttribute` baseline's mean.

---

## How it works

`PerfEvaluator.ApplyComparisonGates` runs after absolute budgets are resolved:

1. For each method carrying `[PerfComparison(maxRatio, group)]`:
   - Find the baseline for `group`.
   - If the baseline mean is `NaN`, **skip** — the challenger stays unbudgeted.
2. Rewrite the challenger's `BudgetNanoseconds` to `ceil(baselineMean × maxRatio)`.
3. The **generic time gate** then evaluates it — no special-case code path in the verdict.

> Comparison gating is *composed on top of* the ordinary budget resolution; it reuses the exact same compare-and-verdict machinery. The challenger may also carry its own `[PerfCritical]` — both gates are checked independently.

---

## Baseline matching (explicit vs fallback)

`FindBaseline(measurements, group)`:

1. **Explicit match wins:** the first measurement with `IsBaseline && Name == group` (ordinal).
2. **Fallback:** otherwise the **first baseline entry** in the reflection batch.
3. `null` if none — comparison skipped.

```csharp
[PerfBaseline("phrase")]       // baseline for group "phrase"
public void BaselinePhrase() { }

[PerfComparison(2.0, "phrase")]
public void ChallengerPhrase() { }
```

> **Naming subtlety:** the explicit match compares the baseline's *method name* to the group *key*. With a single baseline, the "first baseline" fallback handles any names. Use matching names to make the pairing explicit and portable.

### Single-baseline shortcut (default group `""`)

```csharp
[PerfBaseline]                     // group ""
public void Base() { }

[PerfComparison(1.5)]              // group "" pairs automatically
public void Over() { }
```

---

## Budget math

`ChallengerBudgetNs = ceil(baselineMeanNs × maxRatio)` — because of `Math.Ceiling`, landing *exactly* on `base × ratio` is a **pass**.

| Baseline mean | MaxRatio | Challenger budget | 200 ns challenger |
|---|---|---|---|
| 100 ns | 1.5 | 150 ns | **violation** |
| 100 ns | 2.0 | 200 ns | pass (at limit) |
| 101 ns | 2.0 | 202 ns | pass |

---

## Validation

`PerfComparisonAttribute` constructor rejects `NaN` and `maxRatio <= 1.0` with `ArgumentOutOfRangeException`. **`PositiveInfinity` is valid** — an effectively unlimited ratio, useful for ticket items you want documented as a challenger without a real ceiling.

---

## Failure modes

| Condition | Result |
|---|---|
| Baseline missing | Challenger unbudgeted (`BudgetNanoseconds == -1`) — **no comparison applied** |
| Baseline mean `NaN` | Same — skipped |
| Challenger > computed budget | `PerfViolationException` in `StartAndThrow` / `Test-PerformanceTargets.ps1` failure |
| Ratio ≤ 1.0 or `NaN` | `ArgumentOutOfRangeException` at construction |

> **Semantics note:** a skipped comparison does **not** fail — it reports `info`. If your contract demands a challenger *always* be gated, make the baseline method a first-class part of the suite and fail CI when you see unexpected `info` rows.

---

## Full example — a regression-proof suite

```csharp
[MemoryDiagnoser]
[PerfCritical(5_000)]                // absolute ceiling for everything
public class LookupBenchmarks
{
    [Benchmark]
    [PerfBaseline("lookup")]
    public int BaselineScan() => _rows.Count(r => r.Id == target);

    [Benchmark]
    [PerfComparison(maxRatio: 1.5, group: "lookup")]
    public int Hashed() => _index.Get(target);          // must stay ≤ 1.5× baseline

    [Benchmark]
    [PerfComparison(maxRatio: 1.1, group: "lookup")]
    public int HashedCache() => _cache.GetOrAdd(target); // tight: ≤ 1.1× baseline
}
```

Baseline at 100 ns ⇒ `Hashed` ceiling 150 ns, `HashedCache` ceiling 110 ns. If a future change bloats `BaselineScan`, every challenger's ceiling rises too — comparisons track regressions *as a ratio*, not absolutely.

---

*Next: [When to Use What](When-to-Use-What) · [Benchmark Suites](Benchmark-Suites)*