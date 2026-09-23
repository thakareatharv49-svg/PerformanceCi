using System;

namespace PerformanceCI;

/// <summary>
/// Declares a hard time budget (in nanoseconds) for a benchmark method or class.
/// Exceeding the budget is a build-breaking violation, equivalent to failing an assertion.
/// </summary>
/// <remarks>
/// Semantics: <c>elapsedNanoseconds &gt; ThresholdNanoseconds</c> ⇒ violation.
/// A method-level attribute overrides a class-level one. Absence on an
/// <see cref="PerfIgnoreAttribute"/>-marked member suppresses the check.
///
/// This type is a pure metadata token; it performs zero work and zero allocation
/// when instantiated (a sealed class + one readonly int). It is read once by the
/// runner at reflection time, never on a measured hot path.
/// </remarks>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
public sealed class PerfCriticalAttribute : Attribute
{
    /// <summary>Maximum allowed elapsed time in nanoseconds.</summary>
    public readonly int ThresholdNanoseconds;

    /// <summary>Creates a budget of <paramref name="thresholdNanoseconds"/> nanoseconds.</summary>
    /// <param name="thresholdNanoseconds">Positive upper bound.</param>
    public PerfCriticalAttribute(int thresholdNanoseconds)
    {
        if (thresholdNanoseconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(thresholdNanoseconds), "Budget must be positive.");
        ThresholdNanoseconds = thresholdNanoseconds;
    }
}

/// <summary>
/// Opts a method or class out of every performance gate and report.
/// Inherited; a class-level mark propagates to all benchmark methods unless one
/// carries its own <see cref="PerfCriticalAttribute"/>.
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
public sealed class PerfIgnoreAttribute : Attribute
{
}

/// <summary>
/// Marks the baseline side of a comparison group. See <see cref="PerfComparisonAttribute"/>.
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
public sealed class PerfBaselineAttribute : Attribute
{
    /// <summary>Group key shared with the matching comparison attribute (default "").</summary>
    public readonly string Group;

    /// <summary>Creates a default-group baseline (group "").</summary>
    public PerfBaselineAttribute() => Group = string.Empty;

    /// <summary>Creates a baseline bound to <paramref name="group"/>.</summary>
    public PerfBaselineAttribute(string group) => Group = group ?? string.Empty;
}

/// <summary>
/// Declares a hard allocation budget (in bytes) for a benchmark method or class.
/// Exceeding it is a build-breaking violation (assert-equivalent).
/// </summary>
/// <remarks>
/// Reads <see cref="GC.GetAllocatedBytesForCurrentThread()"/> before/after the
/// method. Method-level overrides class-level. Not evaluated on
/// <see cref="PerfIgnoreAttribute"/> members.
/// </remarks>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
public sealed class PerfAllocationAttribute : Attribute
{
    /// <summary>Maximum allowed allocation in bytes.</summary>
    public readonly int MaxBytes;

    /// <param name="maxBytes">Positive upper bound in bytes.</param>
    public PerfAllocationAttribute(int maxBytes)
    {
        if (maxBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxBytes), "Budget must be positive.");
        MaxBytes = maxBytes;
    }
}

/// <summary>
/// Declares that the annotated method must not degrade beyond
/// <see cref="MaxRatio"/> × the mean of the group's baseline method.
/// Violation ⇒ build failure (assert-equivalent).
/// </summary>
/// <remarks>
/// Example: a baseline at 100 ns with <c>MaxRatio = 1.5</c> fails any challenger at &gt; 150 ns.
/// Pairing: challenger groups with <see cref="PerfBaselineAttribute.Group"/>. A single
/// baseline method in the same benchmark class is used when no explicit group is given.
/// </remarks>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
public sealed class PerfComparisonAttribute : Attribute
{
    /// <summary>Maximum tolerated ratio challenger-mean / baseline-mean (&gt; 1).</summary>
    public readonly double MaxRatio;

    /// <summary>Group key shared with the baseline (default "").</summary>
    public readonly string Group;

    /// <param name="maxRatio">Strictly greater than 1.</param>
    /// <param name="group">Optional group key.</param>
    public PerfComparisonAttribute(double maxRatio, string group = "")
    {
        if (double.IsNaN(maxRatio) || maxRatio <= 1.0)
            throw new ArgumentOutOfRangeException(nameof(maxRatio), "MaxRatio must be > 1.0.");
        MaxRatio = maxRatio;
        Group = group ?? string.Empty;
    }
}