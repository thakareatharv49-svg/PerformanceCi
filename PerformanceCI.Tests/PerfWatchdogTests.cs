using PerformanceCI;
using Xunit;

namespace PerformanceCI.Tests;

/// <summary>Verifies the sample struct's payload and violation predicate.</summary>
public sealed class PerfSampleTests
{
    [Fact]
    public void Ctor_Stores_Fields()
    {
        var sample = new PerfSample(elapsedNs: 100, budgetNs: 200, "op");
        Assert.Equal(100L, sample.ElapsedNanoseconds);
        Assert.Equal(200L, sample.BudgetNanoseconds);
        Assert.Equal("op", sample.Operation);
    }

    [Theory]
    [InlineData(99, 100, false)]
    [InlineData(100, 100, false)]
    [InlineData(101, 100, true)]
    public void Violated_Is_Strict_Elapsed_Over_Budget(long elapsed, long budget, bool expected)
    {
        var sample = new PerfSample(elapsed, budget, null);
        Assert.Equal(expected, sample.Violated);
    }
}

/// <summary>
/// Verifies the process-wide heavy-event bus: subscribe, raise with payload, unsubscribe,
/// and a no-op when nobody is listening.
/// </summary>
public sealed class PerfHeavyTests
{
    [Fact]
    public void Notify_With_No_Subscribers_Is_Noop()
    {
        PerfHeavy.Clear();
        PerfHeavy.NotifyViolation(100, 50); // must not throw
    }

    [Fact]
    public void Subscriber_Receives_Sample_In_Order()
    {
        PerfHeavy.Clear();
        long? seenNs = null;
        long? seenBudget = null;
        string? seenOp = null;
        bool? seenViolation = null;

        PerfHeavyHandler handler = (in PerfSample s) =>
        {
            seenNs = s.ElapsedNanoseconds;
            seenBudget = s.BudgetNanoseconds;
            seenOp = s.Operation;
            seenViolation = s.Violated;
        };
        PerfHeavy.Violation += handler;
        try
        {
            PerfHeavy.NotifyViolation(150, 100, "heavy-op");
        }
        finally
        {
            PerfHeavy.Violation -= handler;
        }

        Assert.Equal(150L, seenNs);
        Assert.Equal(100L, seenBudget);
        Assert.Equal("heavy-op", seenOp);
        Assert.True(seenViolation);
    }

    [Fact]
    public void Unsubscribed_Handler_Is_Not_Called()
    {
        PerfHeavy.Clear();
        int calls = 0;
        PerfHeavyHandler handler = (in PerfSample s) => calls++;
        PerfHeavy.Violation += handler;
        PerfHeavy.Violation -= handler;
        PerfHeavy.NotifyViolation(10, 5);
        Assert.Equal(0, calls);
    }

    [Fact]
    public void Clear_Removes_All_Handlers()
    {
        int calls = 0;
        PerfHeavyHandler handler = (in PerfSample s) => calls++;
        PerfHeavy.Violation += handler;
        PerfHeavy.Clear();
        PerfHeavy.NotifyViolation(10, 5);
        Assert.Equal(0, calls);
    }
}

/// <summary>
/// Verifies the scoped watchdog: the pass fast-path raises nothing, the breach path
/// notifies the heavy bus, and the throw variant raises PerfViolationException.
/// Uses <c>using var</c> so Dispose runs at scope end.
/// </summary>
public sealed class PerfWatchdogTests
{
    [Fact]
    public void Start_Within_Budget_Passes_Silently()
    {
        PerfHeavy.Clear();
        using var watchdog = PerfWatchdog.Start(60_000_000_000, "fast");
        // no breach expected
    }

    [Fact]
    public void Start_Over_Budget_Notifies_Heavy_Bus()
    {
        PerfHeavy.Clear();
        long? notifiedNs = null;
        PerfHeavyHandler handler = (in PerfSample s) => notifiedNs = s.ElapsedNanoseconds;
        PerfHeavy.Violation += handler;
        try
        {
            using (var watchdog = PerfWatchdog.Start(1, "slow"))
            {
                Thread.SpinWait(200_000);
            }

            Assert.NotNull(notifiedNs);
            Assert.True(notifiedNs > 1);
        }
        finally
        {
            PerfHeavy.Violation -= handler;
            PerfHeavy.Clear();
        }
    }

    [Fact]
    public void StartAndThrow_Over_Budget_Throws()
    {
        PerfHeavy.Clear();
        var ex = Assert.Throws<PerfViolationException>(() =>
        {
            using var watchdog = PerfWatchdog.StartAndThrow(1, "throw-op");
            Thread.SpinWait(100_000);
        });

        Assert.Equal("throw-op", ex.Operation);
        Assert.Equal(1L, ex.BudgetNanoseconds);
        Assert.True(ex.ElapsedNanoseconds > 1);
    }

    [Fact]
    public void StartAndThrow_Within_Budget_Does_Not_Throw()
    {
        using var watchdog = PerfWatchdog.StartAndThrow(60_000_000_000, "fast-safe");
        // must not throw
    }

    [Fact]
    public void Dry_Run_Start_Still_Notifies_But_Does_Not_Throw()
    {
        PerfHeavy.Clear();
        int notified = 0;
        PerfHeavyHandler handler = (in PerfSample s) => notified++;
        PerfHeavy.Violation += handler;
        try
        {
            using (var watchdog = PerfWatchdog.Start(1, "dry"))
            {
                Thread.SpinWait(200_000);
            }

            Assert.Equal(1, notified);
        }
        finally
        {
            PerfHeavy.Violation -= handler;
        }
    }

    [Fact]
    public void ElapsedNanoseconds_Is_NonNegative()
    {
        var watchdog = PerfWatchdog.Start(60_000_000_000, "elapsed");
        Assert.True(watchdog.ElapsedNanoseconds >= 0);
        watchdog.Dispose();
    }
}

/// <summary>
/// Verifies the unsafe native scope: measure-only windows, null callback is allowed,
/// and a breach invokes the raw function pointer with the measured values.
/// </summary>
public sealed unsafe class PerfWatchdogNativeTests
{
    private static int s_calls;

    private static void OnViolation(long elapsedNs, long budgetNs)
    {
        Assert.True(elapsedNs > 0);
        Assert.Equal(1L, budgetNs);
        s_calls++;
    }

    [Fact]
    public void Native_Start_Within_Budget_Calls_Nothing()
    {
        s_calls = 0;
        using var scope = PerfWatchdogNative.Start(60_000_000_000, &OnViolation, "native-fast");
        Assert.Equal(0, s_calls);
    }

    [Fact]
    public void Native_Start_Over_Budget_Invokes_Function_Pointer()
    {
        s_calls = 0;
        using (var scope = PerfWatchdogNative.Start(1, &OnViolation, "native-slow"))
        {
            Thread.SpinWait(200_000);
        }

        Assert.Equal(1, s_calls);
    }

    [Fact]
    public void Native_Start_Null_Callback_Over_Budget_Is_Measure_Only()
    {
        PerfHeavy.Clear();
        int notified = 0;
        PerfHeavyHandler handler = (in PerfSample s) => notified++;
        PerfHeavy.Violation += handler;
        try
        {
            using (var scope = PerfWatchdogNative.Start(1, (delegate*<long, long, void>)null, "null-cb"))
            {
                Thread.SpinWait(200_000);
            }

            Assert.Equal(1, notified); // bus still notified; raw pointer skipped
        }
        finally
        {
            PerfHeavy.Violation -= handler;
        }
    }

    [Fact]
    public void Native_ElapsedNanoseconds_Is_NonNegative()
    {
        var scope = PerfWatchdogNative.Start(60_000_000_000, &OnViolation, "native-elapsed");
        Assert.True(scope.ElapsedNanoseconds >= 0);
        scope.Dispose();
    }
}