using System;
using System.Runtime.CompilerServices;

namespace PerformanceCI;

/// <summary>
/// Immutable sample of a measured operation. Value type — passed by wide reference,
/// never boxed on the publish path, so raising a heavy event allocates nothing.
/// </summary>
public readonly struct PerfSample
{
    /// <summary>Measured elapsed time in nanoseconds.</summary>
    public readonly long ElapsedNanoseconds;

    /// <summary>Declared budget in nanoseconds.</summary>
    public readonly long BudgetNanoseconds;

    /// <summary>Operation label, when one was provided.</summary>
    public readonly string? Operation;

    internal PerfSample(long elapsedNs, long budgetNs, string? operation)
    {
        ElapsedNanoseconds = elapsedNs;
        BudgetNanoseconds = budgetNs;
        Operation = operation;
    }

    /// <summary>True when the sample exceeded its budget.</summary>
    public bool Violated => ElapsedNanoseconds > BudgetNanoseconds;
}

/// <summary>
/// Signature for heavy-event subscribers. The sample is passed <see langword="in"/>
/// (by-ref) so a notification costs one static event-field read + one indirect call,
/// with zero heap traffic on the measured path.
/// </summary>
public delegate void PerfHeavyHandler(in PerfSample sample);

/// <summary>
/// Process-wide event bus that fires when a measured operation exceeds its budget.
/// The event field is <see langword="null"/> (a single branch) until subscribed,
/// so an unobserved run pays no price.
/// </summary>
public static class PerfHeavy
{
    /// <summary>Raised whenever a watchdog or gate observes a budget breach.</summary>
    public static event PerfHeavyHandler? Violation;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void NotifyViolation(long elapsedNs, long budgetNs, string? operation = null)
    {
        PerfHeavyHandler? handler = System.Threading.Volatile.Read(ref Violation);
        if (handler is not null)
        {
            var sample = new PerfSample(elapsedNs, budgetNs, operation);
            handler.Invoke(in sample);
        }
    }

    /// <summary>Unsubscribes every handler. Useful to fully silence a noisy section.</summary>
    public static void Clear() => System.Threading.Volatile.Write(ref Violation, null);
}

/// <summary>
/// Scoped, allocation-free watchdog. Mark the start with <see cref="Start(long,string?)"/>,
/// let the returned ref-struct fall out of scope, and its <see cref="Dispose"/> measures
/// the elapsed window against the budget. Because it is a <see langword="ref struct"/>,
/// the <see langword="using"/>/scope pattern never boxes it — the whole lifecycle is
/// stack-only. Internally it reads <c>Stopwatch.GetTimestamp()</c> (the OS QPC), which
/// is the cheapest high-resolution clock available.
/// </summary>
/// <remarks>
/// Three termination modes:
/// <list type="bullet">
///   <item><description><see cref="Start(long,string?)"/> — measures; on breach it raises
///   <see cref="PerfHeavy.Violation"/> only.</description></item>
///   <item><description><see cref="StartAndThrow(long,string?)"/> — additionally throws
///   <see cref="PerfViolationException"/> (assert-equivalent).</description></item>
///   <item><description>Unsafe <see cref="PerfWatchdogNative.Start"/> — invokes a raw
///   managed function pointer on breach; zero delegate indirection.</description></item>
/// </list>
/// The excessive branch (Dispose) is <see cref="MethodImplOptions.AggressiveOptimization"/>-implicit
/// and runs only the compare; the invariant fast exit never touches the heap.
/// </remarks>
public readonly ref struct PerfWatchdog
{
    private readonly long _startTicks;
    private readonly long _budgetTicks;
    private readonly long _budgetNs;
    private readonly bool _throwOnViolation;
    private readonly string? _operation;

    private PerfWatchdog(long budgetNs, string? operation, bool throwOnViolation)
    {
        _startTicks = Timestamp();
        _budgetTicks = PerfGate.NanosecondsToTicks(budgetNs);
        _budgetNs = budgetNs;
        _operation = operation;
        _throwOnViolation = throwOnViolation;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long Timestamp() => System.Diagnostics.Stopwatch.GetTimestamp();

    /// <summary>Starts a measuring window; raises <see cref="PerfHeavy.Violation"/> on breach.</summary>
    public static PerfWatchdog Start(long budgetNs, string? operation = null)
        => new(budgetNs, operation, throwOnViolation: false);

    /// <summary>Starts a measuring window that throws <see cref="PerfViolationException"/> on breach.</summary>
    public static PerfWatchdog StartAndThrow(long budgetNs, string? operation = null)
        => new(budgetNs, operation, throwOnViolation: true);

    /// <summary>Elapsed nanoseconds within this still-open window.</summary>
    public long ElapsedNanoseconds => PerfGate.ElapsedNanoseconds(_startTicks, Timestamp());

    /// <summary>
    /// Ends the window. Measures elapsed vs budget; on breach notifies the heavy bus
    /// and, when started with <see cref="StartAndThrow"/>, throws.
    ///
    /// Hot path: budget was converted to ticks once at construction, so the pass case is
    /// one QPC read + one integer subtract + one integer compare — no multiply, no branch
    /// beyond the compare, nothing on the heap.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public void Dispose()
    {
        long elapsedTicks = ElapsedTicks();
        if (elapsedTicks <= _budgetTicks)
            return;

        long elapsedNs = PerfGate.ElapsedNanoseconds(_startTicks, _startTicks + elapsedTicks);
        PerfHeavy.NotifyViolation(elapsedNs, _budgetNs, _operation);
        if (_throwOnViolation)
            throw new PerfViolationException(_operation, elapsedNs, _budgetNs);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private long ElapsedTicks() => Timestamp() - _startTicks;
}

/// <summary>
/// Unsafe, dependent-token-free watchdog entry points. See <see cref="PerfWatchdog"/>.
/// The breach callback is a raw managed function pointer (no delegate object, no thunk),
/// the lowest-ceremony notification this library offers.
/// </summary>
public static unsafe class PerfWatchdogNative
{
    /// <summary>Starts a window whose breach handler is <paramref name="onViolation"/> (elapsedNs, budgetNs).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static PerfWatchdogNativeScope Start(long budgetNs, delegate*<long, long, void> onViolation, string? operation = null)
        => new(budgetNs, onViolation, operation);
}

/// <summary>Stack-only scope returned by <see cref="PerfWatchdogNative.Start"/></summary>
public readonly unsafe ref struct PerfWatchdogNativeScope
{
    private readonly long _startTicks;
    private readonly long _budgetTicks;
    private readonly long _budgetNs;
    private readonly string? _operation;
    private readonly delegate*<long, long, void> _onViolation;

    internal PerfWatchdogNativeScope(long budgetNs, delegate*<long, long, void> onViolation, string? operation)
    {
        _startTicks = System.Diagnostics.Stopwatch.GetTimestamp();
        _budgetTicks = PerfGate.NanosecondsToTicks(budgetNs);
        _budgetNs = budgetNs;
        _onViolation = onViolation;
        _operation = operation;
    }

    /// <summary>Elapsed nanoseconds within this still-open native window.</summary>
    public long ElapsedNanoseconds => PerfGate.ElapsedNanoseconds(_startTicks, System.Diagnostics.Stopwatch.GetTimestamp());

    /// <summary>
    /// Ends the window. On breach, invokes the raw function pointer (zero allocation),
    /// then notifies the heavy bus. The function pointer may be <see langword="null"/>
    /// for measure-only. Pass path: one QPC read + one integer compare.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public void Dispose()
    {
        long elapsedTicks = System.Diagnostics.Stopwatch.GetTimestamp() - _startTicks;
        if (elapsedTicks <= _budgetTicks)
            return;

        long elapsedNs = PerfGate.ElapsedNanoseconds(_startTicks, _startTicks + elapsedTicks);
        PerfHeavy.NotifyViolation(elapsedNs, _budgetNs, _operation);
        if (_onViolation != null)
            _onViolation(elapsedNs, _budgetNs);
    }
}