using PerformanceCI;
using Xunit;

namespace PerformanceCI.Tests;

/// <summary>
/// Verifies the gate primitives: tick conversion, budget comparisons, and the
/// assert-equivalent failure semantics. These tests avoid asserting on absolute
/// timing, only on ordering and thresholds.
/// </summary>
public sealed class PerfGateTests
{
    [Fact]
    public void NowTicks_Is_Monotonic_Increasing()
    {
        long first = PerfGate.NowTicks();
        long second = PerfGate.NowTicks();
        Assert.True(second >= first);
    }

    [Fact]
    public void ElapsedTicks_Is_Difference_Of_Timestamps()
    {
        long start = PerfGate.NowTicks();
        // A guaranteed small positive delta regardless of clock granularity.
        long end = start + 1000;
        Assert.Equal(1000L, PerfGate.ElapsedTicks(start, end));
        Assert.Equal(-1000L, PerfGate.ElapsedTicks(end, start));
    }

    [Fact]
    public void ElapsedNanoseconds_Is_Positive_For_Later_Timestamp()
    {
        long start = PerfGate.NowTicks();
        long end = start + 1000;
        Assert.True(PerfGate.ElapsedNanoseconds(start, end) > 0);
    }

    [Fact]
    public void ElapsedNanoseconds_Same_Timestamp_Is_Zero()
    {
        long now = PerfGate.NowTicks();
        Assert.Equal(0L, PerfGate.ElapsedNanoseconds(now, now));
    }

    [Fact]
    public void NanosecondsToTicks_Is_Roughly_Second_Per_GHz()
    {
        // 1 second = 1_000_000_000 ns. Stopwatch.Frequency = ticks/second.
        // So we expect ~= Frequency ticks.
        long ticksForSecond = PerfGate.NanosecondsToTicks(1_000_000_000L);
        long frequency = System.Diagnostics.Stopwatch.Frequency;
        long tolerance = frequency / 1_000; // 0.1% tolerance
        Assert.InRange(ticksForSecond, frequency - tolerance, frequency + tolerance);
    }

    [Fact]
    public void NanosecondsToTicks_Zero_Is_Zero()
    {
        Assert.Equal(0L, PerfGate.NanosecondsToTicks(0));
    }

    [Theory]
    [InlineData(100, 100, false)] // equal => not exceeded
    [InlineData(100, 99, true)]
    [InlineData(99, 100, false)]
    [InlineData(-5, 0, false)] // negative elapsed never exceeds
    public void Exceeded_Compares_Strictly(long elapsed, long budget, bool expected)
    {
        Assert.Equal(expected, PerfGate.Exceeded(elapsed, budget));
    }

    [Fact]
    public void Ensure_Does_Not_Throw_When_Within_Budget()
    {
        PerfGate.Ensure(elapsedNs: 50, budgetNs: 100);
        PerfGate.Ensure(elapsedNs: 100, budgetNs: 100); // boundary is pass
    }

    [Fact]
    public void Ensure_Throws_On_Breach()
    {
        var ex = Assert.Throws<PerfViolationException>(() => PerfGate.Ensure(elapsedNs: 101, budgetNs: 100));
        Assert.Equal(101L, ex.ElapsedNanoseconds);
        Assert.Equal(100L, ex.BudgetNanoseconds);
    }

    [Fact]
    public void Ensure_Records_Operation_Label()
    {
        var ex = Assert.Throws<PerfViolationException>(() => PerfGate.Ensure(101, 100, "op-name"));
        Assert.Equal("op-name", ex.Operation);
    }

    [Fact]
    public void EnsureTicks_Throws_On_Breach_In_Tick_Domain()
    {
        long budgetNs = 1000;
        long budgetTicks = PerfGate.NanosecondsToTicks(budgetNs);
        long overBudgetTicks = budgetTicks + 100;
        var ex = Assert.Throws<PerfViolationException>(() =>
            PerfGate.EnsureTicks(overBudgetTicks, budgetTicks, "ticks-op"));

        Assert.Equal("ticks-op", ex.Operation);
        Assert.Equal(budgetNs, ex.BudgetNanoseconds);
        Assert.True(ex.ElapsedNanoseconds > budgetNs);
    }

    [Fact]
    public void EnsureTicks_Does_Not_Throw_Within_Budget()
    {
        long budgetTicks = PerfGate.NanosecondsToTicks(1_000_000L);
        PerfGate.EnsureTicks(budgetTicks - 1, budgetTicks);
    }

    [Fact]
    public void AssertTime_Returns_Positive_Elapsed_On_Pass()
    {
        long elapsed = PerfGate.AssertTime(() => { /* no-op */ }, budgetNs: 60_000_000, "fast");
        Assert.True(elapsed >= 0);
    }

    [Fact]
    public void AssertTime_Throws_When_Action_Exceeds_Tiny_Budget()
    {
        var ex = Assert.Throws<PerfViolationException>(() =>
            PerfGate.AssertTime(() => Thread.SpinWait(100_000), budgetNs: 1, "spin"));
        Assert.Equal("spin", ex.Operation);
        Assert.True(ex.ElapsedNanoseconds > 1);
    }

    [Fact]
    public void AssertAllocations_Returns_Delta_On_Pass()
    {
        long delta = PerfGate.AssertAllocations(() => { /* no-op */ }, budgetBytes: 1_000_000, "noalloc");
        Assert.True(delta >= 0);
    }

    [Fact]
    public void AssertAllocations_Throws_When_Action_Allocates_Over_Budget()
    {
        var ex = Assert.Throws<PerfAllocationViolationException>(() =>
            PerfGate.AssertAllocations(() => { var _ = new byte[4096]; }, budgetBytes: 16, "alloc"));
        Assert.Equal("alloc", ex.Operation);
        Assert.True(ex.AllocatedBytes > 16);
        Assert.Equal(16L, ex.BudgetBytes);
    }
}

/// <summary>Verifies the lazy message building and data-carrying behavior of both exception types.</summary>
public sealed class PerfViolationExceptionTests
{
    [Fact]
    public void Message_Includes_Operation_Elapsed_Budget_And_Percentage()
    {
        var ex = new PerfViolationException("my-op", 150, 100);
        string msg = ex.Message;
        Assert.Contains("[PerfCI]", msg);
        Assert.Contains("my-op", msg);
        Assert.Contains("150", msg);
        Assert.Contains("100", msg);
        Assert.Contains("50.0%", msg); // (150-100)/100 = 50% over
    }

    [Fact]
    public void Message_Uses_Fallback_Operation_When_Null()
    {
        var ex = new PerfViolationException(null, 42, 21);
        Assert.Contains("operation", ex.Message);
    }

    [Fact]
    public void Message_Handles_Zero_Budget_Without_Divide_By_Zero()
    {
        var ex = new PerfViolationException("op", 99, 0);
        Assert.Contains("0.0%", ex.Message);
    }

    [Fact]
    public void Data_Properties_Return_Constructor_Values()
    {
        var ex = new PerfViolationException("op", 1, 2);
        Assert.Equal("op", ex.Operation);
        Assert.Equal(1L, ex.ElapsedNanoseconds);
        Assert.Equal(2L, ex.BudgetNanoseconds);
    }
}

/// <summary>Verifies the allocation violation exception's lazy message and payload.</summary>
public sealed class PerfAllocationViolationExceptionTests
{
    [Fact]
    public void Message_Includes_Operation_Bytes_And_Percentage()
    {
        var ex = new PerfAllocationViolationException("alloc-op", 300, 100);
        string msg = ex.Message;
        Assert.Contains("[PerfCI]", msg);
        Assert.Contains("alloc-op", msg);
        Assert.Contains("300", msg);
        Assert.Contains("100", msg);
        Assert.Contains("200.0%", msg); // (300-100)/100
    }

    [Fact]
    public void Message_Uses_Fallback_Operation_When_Null()
    {
        var ex = new PerfAllocationViolationException(null, 300, 100);
        Assert.Contains("operation", ex.Message);
    }

    [Fact]
    public void Message_Handles_Zero_Budget_Without_Divide_By_Zero()
    {
        var ex = new PerfAllocationViolationException("op", 5, 0);
        Assert.Contains("0.0%", ex.Message);
    }

    [Fact]
    public void Data_Properties_Return_Constructor_Values()
    {
        var ex = new PerfAllocationViolationException("op", 10, 20);
        Assert.Equal("op", ex.Operation);
        Assert.Equal(10L, ex.AllocatedBytes);
        Assert.Equal(20L, ex.BudgetBytes);
    }
}