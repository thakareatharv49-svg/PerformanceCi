using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace PerformanceCI;

/// <summary>
/// Thrown when a performance budget declared by an attribute is exceeded.
/// This is the contract's "assert false": a violation must fail the build, run, or
/// benchmark step that observes it. The exception intentionally carries no message
/// formatting or string interpolation on the hot path — the details are computed
/// lazily when <see cref="Message"/> is materialized.
/// </summary>
public sealed class PerfViolationException : Exception
{
    private readonly string? _operation;
    private readonly long _elapsedNs;
    private readonly long _budgetNs;

    /// <summary>Creates the violation with the operation label, measured time, and budget.</summary>
    /// <param name="operation">Optional operation name reported in the message.</param>
    /// <param name="elapsedNs">Measured elapsed nanoseconds.</param>
    /// <param name="budgetNs">Declared budget in nanoseconds.</param>
    public PerfViolationException(string? operation, long elapsedNs, long budgetNs)
        : base(null)
    {
        _operation = operation;
        _elapsedNs = elapsedNs;
        _budgetNs = budgetNs;
    }

    /// <summary>Operation that exceeded its budget, if one was supplied.</summary>
    public string? Operation => _operation;

    /// <summary>Measured elapsed time in nanoseconds.</summary>
    public long ElapsedNanoseconds => _elapsedNs;

    /// <summary>Declared budget in nanoseconds.</summary>
    public long BudgetNanoseconds => _budgetNs;

    /// <summary>Human-readable breach description; built lazily, only when accessed.</summary>
    public override string Message
    {
        get
        {
            double pct = _budgetNs == 0 ? 0 : 100.0 * (_elapsedNs - _budgetNs) / _budgetNs;
            return string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                "[PerfCI] {0} took {1} ns (budget {2} ns, {3:+0.0;-0.0;0.0}% over).",
                _operation ?? "operation", _elapsedNs, _budgetNs, pct);
        }
    }
}

/// <summary>
/// Allocation- and time-budget gate with assert-equivalent failure semantics.
/// All checkers are branch-predictable, allocate nothing, and are safe to call on
/// measured hot paths. <see cref="Ensure"/> throws <see cref="PerfViolationException"/>
/// — the same "fail now" behavior as <see langword="assert"/>.
/// </summary>
public static class PerfGate
{
    /// <summary>Nanoseconds per timestamp tick. Rationalized once, never per call.</summary>
    private static readonly double NanosPerTick = 1_000_000_000.0 / Stopwatch.Frequency;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long TicksToNanoseconds(long ticks) => (long)(ticks * NanosPerTick);

    /// <summary>Current high-resolution timestamp (QPC). Zero allocation.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long NowTicks() => Stopwatch.GetTimestamp();

    /// <summary>Elapsed nanoseconds between two timestamps from <see cref="NowTicks"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long ElapsedNanoseconds(long startTicks, long endTicks) => TicksToNanoseconds(endTicks - startTicks);

    /// <summary>Elapsed raw ticks between two timestamps. No conversion — the fastest elapsed measure.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long ElapsedTicks(long startTicks, long endTicks) => endTicks - startTicks;

    /// <summary>Bakes a nanosecond budget into the platform's raw tick domain once, so hot comparisons skip the multiply.</summary>
    /// <remarks>Rate = <c>Frequency</c>: ticks * 1e9 / Frequency ≈ ns, so ns → ticks = ns * Frequency / 1e9.</remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long NanosecondsToTicks(long nanoseconds) => (long)(nanoseconds * (Stopwatch.Frequency / 1_000_000_000.0));

    /// <summary>True when <paramref name="elapsedNs"/> exceeds <paramref name="budgetNs"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool Exceeded(long elapsedNs, long budgetNs) => elapsedNs > budgetNs;

    /// <summary>
    /// Asserts <paramref name="elapsedNs"/> ≤ <paramref name="budgetNs"/>. Throws
    /// <see cref="PerfViolationException"/> on breach. Cost: one compare+branch in the
    /// pass case; the throwing body is in a <see cref="MethodImplOptions.NoInlining"/>
    /// helper so it never inflates the hot call site.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Ensure(long elapsedNs, long budgetNs, string? operation = null)
    {
        if (elapsedNs > budgetNs)
            ThrowViolation(operation, elapsedNs, budgetNs);
    }

    /// <summary>Same as <see cref="Ensure"/> but in tick domain — zero conversion on the pass path.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void EnsureTicks(long elapsedTicks, long budgetTicks, string? operation = null)
    {
        if (elapsedTicks > budgetTicks)
            ThrowViolation(operation, TicksToNanoseconds(elapsedTicks), TicksToNanoseconds(budgetTicks));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowViolation(string? operation, long elapsedNs, long budgetNs)
        => throw new PerfViolationException(operation, elapsedNs, budgetNs);

    /// <summary>No-inline path for allocation violations.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowAllocationViolation(string? operation, long allocatedBytes, long budgetBytes)
        => throw new PerfAllocationViolationException(operation, allocatedBytes, budgetBytes);

    /// <summary>
    /// Measures the time budget of <paramref name="action"/> and asserts it against
    /// <paramref name="budgetNs"/>. A closure over captured state allocates; prefer
    /// <see cref="PerfWatchdog"/> for allocation-free scoped measurement.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long AssertTime(Action action, long budgetNs, string? operation = null)
    {
        long start = NowTicks();
        action();
        long elapsed = ElapsedNanoseconds(start, NowTicks());
        Ensure(elapsed, budgetNs, operation);
        return elapsed;
    }

    /// <summary>
    /// Measures the allocation delta of <paramref name="action"/> and asserts it
    /// against <paramref name="budgetBytes"/>. Uses
    /// <see cref="GC.GetAllocatedBytesForCurrentThread()"/> — a zero-allocation probe.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long AssertAllocations(Action action, long budgetBytes, string? operation = null)
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        action();
        long delta = GC.GetAllocatedBytesForCurrentThread() - before;
        if (delta > budgetBytes)
            ThrowAllocationViolation(operation, delta, budgetBytes);
        return delta;
    }
}

/// <summary>
/// Thrown when an allocation budget declared by <see cref="PerfAllocationAttribute"/>
/// is exceeded. See <see cref="PerfViolationException"/> for the lazy-message design.
/// </summary>
public sealed class PerfAllocationViolationException : Exception
{
    private readonly string? _operation;
    private readonly long _allocatedBytes;
    private readonly long _budgetBytes;

    /// <summary>Creates the allocation violation with the operation label, actual bytes, and budget.</summary>
    /// <param name="operation">Optional operation name reported in the message.</param>
    /// <param name="allocatedBytes">Actual bytes allocated by the operation.</param>
    /// <param name="budgetBytes">Declared allocation budget in bytes.</param>
    public PerfAllocationViolationException(string? operation, long allocatedBytes, long budgetBytes)
        : base(null)
    {
        _operation = operation;
        _allocatedBytes = allocatedBytes;
        _budgetBytes = budgetBytes;
    }

    /// <summary>Operation that exceeded its allocation budget, if one was supplied.</summary>
    public string? Operation => _operation;

    /// <summary>Actual bytes allocated by the operation.</summary>
    public long AllocatedBytes => _allocatedBytes;

    /// <summary>Declared allocation budget in bytes.</summary>
    public long BudgetBytes => _budgetBytes;

    /// <summary>Human-readable breach description; built lazily, only when accessed.</summary>
    public override string Message
    {
        get
        {
            double pct = _budgetBytes == 0 ? 0 : 100.0 * (_allocatedBytes - _budgetBytes) / _budgetBytes;
            return string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                "[PerfCI] {0} allocated {1} B (budget {2} B, {3:+0.0;-0.0;0.0}% over).",
                _operation ?? "operation", _allocatedBytes, _budgetBytes, pct);
        }
    }
}