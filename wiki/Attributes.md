# Attributes Reference

All PerformanceCI attributes are **pure metadata tokens** — sealed classes with readonly fields. They are instantiated once at reflection time and **never executed on a measured hot path**. Every attribute accepts `AttributeTargets.Method | AttributeTargets.Class`, and all are `Inherited = true`.

---

## Inheritance model

Because `Inherited = true`, the attribute resolver walks up:

1. A **method-level** attribute always wins.
2. Otherwise the **class-level** attribute applies.
3. Otherwise no budget (row reports as `info`).

```csharp
[PerfCritical(2000)]              // class default
public class Suite
{
    [PerfCritical(100)]           // method wins: 100 ns
    public void Fast() { }

    public void Inherited() { }   // uses 2000 ns

    [PerfIgnore]                  // suppression; see below
    public void Ignored() { }
}
```

> **Consequence:** to change the *class* default for one method, write a `[PerfCritical(...)]` on that method — you cannot "un-set" a class budget except with `[PerfIgnore]`.

---

## `PerfCriticalAttribute`

**Hard time budget, nanoseconds. Exceeding it is a build-breaking violation.**

```csharp
public PerfCriticalAttribute(int thresholdNanoseconds)
```

| Rule | Behavior |
|---|---|
| `thresholdNanoseconds <= 0` | `ArgumentOutOfRangeException("Budget must be positive.")` |
| Valid | Budget = `ThresholdNanoseconds`, enforced as `meanNs > budget` ⇒ violation |

### Examples

```csharp
[PerfCritical(100_000)]                          // 100 µs
[PerfCritical(5_000_000)]                        // 5 ms
[PerfCritical(1)]                                // aggressive: sub-microsecond floor
```

---

## `PerfAllocationAttribute`

**Hard allocation budget, bytes. Exceeding it is a build-breaking violation.**

```csharp
public PerfAllocationAttribute(int maxBytes)
```

Measured via `GC.GetAllocatedBytesForCurrentThread()` deltas — a zero-allocation probe.

| Rule | Behavior |
|---|---|
| `maxBytes <= 0` | `ArgumentOutOfRangeException("Budget must be positive.")` |
| Valid | Budget = `MaxBytes`, enforced as `meanBytes > budget` ⇒ violation |

### Examples

```csharp
[PerfAllocation(0)]              // throws at runtime when instantiated
[PerfAllocation(1024)]           // 1 KiB ceiling
[PerfAllocation(64)]             // struct-only hot loop
```

---

## `PerfBaselineAttribute`

**Marks the baseline side of a comparison group.**

```csharp
public PerfBaselineAttribute()               // group ""
public PerfBaselineAttribute(string group)
```

A baseline is *not* gated by its own time budget for comparison purposes — other methods (challengers) are measured against it. A baseline that also carries `[PerfCritical]` is still independently time-gated.

### Matching rules

- Explicit group: `FindBaseline` prefers a baseline whose **method name equals the group key**.
- Otherwise: the **first baseline entry** in the reflection batch wins.
- A **class-level** `[PerfBaseline]` marks every method as a baseline fallback.

```csharp
[PerfBaseline("phrase")]     // group "phrase"
public void BaselinePhrase() { }

[PerfComparison(1.5, "phrase")]
public void ChallengerPhrase() { }
```

---

## `PerfComparisonAttribute`

**Ratio gate: challenger mean must stay ≤ `MaxRatio × baseline mean`.**

```csharp
public PerfComparisonAttribute(double maxRatio, string group = "")
```

| Rule | Behavior |
|---|---|
| `double.IsNaN(maxRatio) \|\| maxRatio <= 1.0` | `ArgumentOutOfRangeException("MaxRatio must be > 1.0.")` |
| `double.PositiveInfinity` | Valid — effectively unlimited |
| Valid ratio + matching baseline | Challenger `BudgetNanoseconds = ceil(baselineMean × MaxRatio)` |
| No baseline found, or baseline mean is `NaN` | Challenger stays unbudgeted (`-1`) — comparison is skipped |

### Example

```csharp
// Baseline averages 100 ns. MaxRatio 1.5 => challenger budget = ceil(150) = 150 ns.
[PerfBaseline("lookup")]
public void BaselineLookup() { }

[PerfComparison(maxRatio: 1.5, group: "lookup")]
public void LookupWithHash() { }

[PerfComparison(maxRatio: 2.0, group: "lookup")]
public void LookupWithScan() { }
```

Challenger at 161 ns ⇒ `> 150` ⇒ violation. The ratio budget is computed with `Math.Ceiling`, so an exact `baseline × ratio` is a *pass*.

> With `group` defaulting to `""`, a challenger and one ungrouped baseline pair up automatically. Give explicit group keys once a class holds more than one baseline.

---

## `PerfIgnoreAttribute`

**Opts a method or class out of every gate AND the verdict. The row still appears in the report with status `ignored`.**

```csharp
[PerfIgnore]
```

- Suppresses `PerfCritical`, `PerfAllocation`, and `PerfComparison`.
- Class-level `[PerfIgnore]` silences every method in the class.
- `PerfReport.Evaluate` / `PerfVerdict.Pass` skip ignored rows entirely — they can never fail CI.

```csharp
[PerfIgnore]
public string ReferenceTotal() => GetCacheSize().ToString();  // report-only
```

### `PerfIgnore` vs "no attribute"

| | No attribute | `[PerfIgnore]` |
|---|---|---|
| Class budget applies? | Yes (inherited) | No (suppressed) |
| Report status | `pass`/`info` | `ignored` |
| Verdict impact | Counted | Never counted |

---

## Quick validation matrix

| Attribute | Invalid input | Throws |
|---|---|---|
| `PerfCritical(int)` | `<= 0` | `ArgumentOutOfRangeException` |
| `PerfAllocation(int)` | `<= 0` | `ArgumentOutOfRangeException` |
| `PerfComparison(double, string)` | `NaN` or `<= 1.0` | `ArgumentOutOfRangeException` |
| `PerfBaseline(string?)` | — | never |
| `PerfIgnore` | — | never |

---

*Next: [Inline Gates](Inline-Gates) · [When to Use What](When-to-Use-What)*