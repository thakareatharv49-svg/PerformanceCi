using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace PerformanceCI;

/// <summary>
/// The CI-side gate: reflects over a benchmark class, reads every performance
/// attribute, and turns measured rows into pass/fail verdicts + a report buffer.
///
/// This class does the translation the attributes promise: a method marked
/// <see cref="PerfCriticalAttribute"/> whose measured mean exceeds its budget is a
/// FAIL; a <see cref="PerfComparisonAttribute"/> entry is FAIL when its mean exceeds
/// the group baseline mean × <see cref="PerfComparisonAttribute.MaxRatio"/>; and
/// <see cref="PerfAllocationAttribute"/> gates bytes. <see cref="PerfIgnoreAttribute"/>
/// marks report-only entries that never gate.
///
/// The hot loop is attribute reads with zero per-row allocations; the only allocations
/// on the whole path are the output <see cref="PerfMeasurement"/> array and the report
/// writer's reuse buffer.
/// </summary>
public static class PerfEvaluator
{
    /// <summary>
    /// Builds the attribute-resolved measurement set for <paramref name="benchType"/>.
    /// <paramref name="measuredMeanNs"/> and <paramref name="measuredMeanBytes"/> must
    /// be parallel arrays keyed by method name (NaN = not measured for that axis).
    /// Class-level budgets apply to methods that don't declare their own.
    /// Inherited attributes are honored (Attribute.GetCustomAttribute with inherit).
    /// </summary>
    public static PerfMeasurement[] BuildMeasurements(
        Type benchType,
        Dictionary<string, double> measuredMeanNs,
        Dictionary<string, double> measuredMeanBytes)
    {
        bool classIgnored = benchType.IsDefined(typeof(PerfIgnoreAttribute), inherit: true);

        var classCritical = benchType.GetCustomAttribute<PerfCriticalAttribute>(inherit: true);
        var classAlloc = benchType.GetCustomAttribute<PerfAllocationAttribute>(inherit: true);
        var classBaseline = benchType.GetCustomAttribute<PerfBaselineAttribute>(inherit: true);

        MethodInfo[] methods = benchType.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly);
        var list = new List<PerfMeasurement>(methods.Length);

        foreach (MethodInfo method in methods)
        {
            string name = method.Name;
            bool methodIgnored = method.IsDefined(typeof(PerfIgnoreAttribute), inherit: true);
            bool ignored = classIgnored || methodIgnored;

            long budgetNs = -1;
            long budgetBytes = -1;
            bool isBaseline = classBaseline is not null;

            var critical = method.GetCustomAttribute<PerfCriticalAttribute>(inherit: true);
            if (critical is not null)
                budgetNs = critical.ThresholdNanoseconds;
            else if (classCritical is not null)
                budgetNs = classCritical.ThresholdNanoseconds;

            var alloc = method.GetCustomAttribute<PerfAllocationAttribute>(inherit: true);
            if (alloc is not null)
                budgetBytes = alloc.MaxBytes;
            else if (classAlloc is not null)
                budgetBytes = classAlloc.MaxBytes;

            if (method.IsDefined(typeof(PerfBaselineAttribute), inherit: true))
                isBaseline = true;

            double meanNs = measuredMeanNs.TryGetValue(name, out double v) && !double.IsNaN(v) ? v : double.NaN;
            double meanBytes = measuredMeanBytes.TryGetValue(name, out double a) && !double.IsNaN(a) ? a : double.NaN;

            list.Add(new PerfMeasurement(name, meanNs, meanBytes, budgetNs, budgetBytes, isBaseline, ignored));
        }

        ApplyComparisonGates(benchType, list);

        return list.ToArray();
    }

    /// <summary>
    /// Applies <see cref="PerfComparisonAttribute"/>: for each group, finds the baseline
    /// entry (marked <see cref="PerfBaselineAttribute"/> or the type-level baseline) and
    /// rewrites challenger <see cref="PerfMeasurement.BudgetNanoseconds"/> to baseline × ratio,
    /// so the generic time gate evaluates comparison automatically.
    /// </summary>
    private static void ApplyComparisonGates(Type benchType, List<PerfMeasurement> measurements)
    {
        var comparisons = benchType.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly);

        foreach (MethodInfo method in comparisons)
        {
            var comparison = method.GetCustomAttribute<PerfComparisonAttribute>(inherit: true);
            if (comparison is null)
                continue;

            PerfMeasurement? baseline = FindBaseline(measurements, comparison.Group);
            if (baseline is null || double.IsNaN(baseline.Value.MeanNanoseconds))
                continue;

            for (int i = 0; i < measurements.Count; i++)
            {
                if (string.Equals(measurements[i].Name, method.Name, StringComparison.Ordinal))
                {
                    var m = measurements[i];
                    var updated = new PerfMeasurement(
                        m.Name, m.MeanNanoseconds, m.MeanAllocatedBytes,
                        budgetNs: (long)Math.Ceiling(baseline.Value.MeanNanoseconds * comparison.MaxRatio),
                        m.BudgetBytes, m.IsBaseline, m.Ignored);
                    measurements[i] = updated;
                    break;
                }
            }
        }
    }

    private static PerfMeasurement? FindBaseline(List<PerfMeasurement> measurements, string group)
    {
        // Explicit group match wins; otherwise the first baseline entry in the batch.
        for (int i = 0; i < measurements.Count; i++)
        {
            if (measurements[i].IsBaseline && string.Equals(measurements[i].Name, group, StringComparison.Ordinal))
                return measurements[i];
        }
        for (int i = 0; i < measurements.Count; i++)
        {
            if (measurements[i].IsBaseline)
                return measurements[i];
        }
        return null;
    }
}

/// <summary>
/// Aggregation helper that pairs the <see cref="PerfEvaluator"/> output with the
/// verdict primitive used by CI exit codes. Purely static; zero mutable state.
/// </summary>
public static class PerfVerdict
{
    /// <summary>True when no time or allocation gate fired across the batch (ignoring report-only entries).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool Pass(ReadOnlySpan<PerfMeasurement> measurements, out int timeViolations, out int allocationViolations)
        => PerfReport.Evaluate(measurements, out timeViolations, out allocationViolations);
}

/// <summary>
/// BDN-agnostic end-to-end gate: maps measured rows to attribute-resolved verdicts and
/// returns the CI exit code. The benchmark exes feed their (method-name → value) maps
/// here, so the gate caller never touches reflection. Purely static; zero mutable state.
/// </summary>
public static class PerfConsoleGate
{
    /// <summary>
    /// Evaluates <paramref name="benchType"/> attributes against the measured maps,
    /// writes the text report to <paramref name="reportPath"/> (or Console when null),
    /// and returns 0 on pass / 1 on any time or allocation violation.
    /// </summary>
    public static int RunAndReport(
        System.Type benchType,
        System.Collections.Generic.Dictionary<string, double> meanNs,
        System.Collections.Generic.Dictionary<string, double> meanBytes,
        string? reportPath)
    {
        PerfMeasurement[] measurements = PerfEvaluator.BuildMeasurements(benchType, meanNs, meanBytes);
        bool pass = PerfVerdict.Pass(measurements, out int timeV, out int allocV);

        if (reportPath is null)
        {
            PerfReport.WriteText(System.Console.Out, measurements);
        }
        else
        {
            using var report = System.IO.File.CreateText(reportPath);
            PerfReport.WriteText(report, measurements);
        }

        System.Console.WriteLine($"[PerfCI] time violations: {timeV}, allocation violations: {allocV} -> {(pass ? "PASS" : "FAIL")}");
        return pass ? 0 : 1;
    }
}