using PerformanceCI;
using Xunit;

namespace PerformanceCI.Tests;

/// <summary>
/// Verifies the streaming report writer, the single-string convenience wrapper, the
/// batch verdict, and the status classification. Measurements are built via the
/// internal ctor (InternalsVisibleTo).
/// </summary>
public sealed class PerfReportTests
{
    private static PerfMeasurement Ms(string name, double meanNs, double meanBytes = double.NaN,
        long budgetNs = -1, long budgetBytes = -1, bool isBaseline = false, bool ignored = false)
        => new(name, meanNs, meanBytes, budgetNs, budgetBytes, isBaseline, ignored);

    [Fact]
    public void WriteText_Empty_Batch_Prints_Header_And_No_Measurements()
    {
        using var writer = new StringWriter();
        PerfReport.WriteText(writer, ReadOnlySpan<PerfMeasurement>.Empty);
        string text = writer.ToString();

        Assert.Contains("# PerformanceCI Report", text);
        Assert.Contains("(no measurements)", text);
    }

    [Fact]
    public void WriteText_Simple_Measurement_Contains_Name_Ns_And_Info_Status()
    {
        var ms = new[] { Ms("FooBar", 123.4) };
        using var writer = new StringWriter();
        PerfReport.WriteText(writer, ms);
        string text = writer.ToString();

        Assert.Contains("FooBar", text);
        Assert.Contains("123.4", text);
        Assert.Contains(" ns", text);
        Assert.Contains("\tinfo\r\n", text);
    }

    [Fact]
    public void WriteText_With_Time_Budget_Prints_Budget_Line()
    {
        var ms = new[] { Ms("Timed", meanNs: 100, budgetNs: 200) };
        using var writer = new StringWriter();
        PerfReport.WriteText(writer, ms);
        string text = writer.ToString();

        Assert.Contains("/ budget 200 ns", text);
        Assert.Contains("\tpass\r\n", text);
    }

    [Fact]
    public void WriteText_With_Allocation_Prints_Bytes()
    {
        var ms = new[] { Ms("Alloc", meanNs: 10, meanBytes: 512, budgetBytes: 1024) };
        using var writer = new StringWriter();
        PerfReport.WriteText(writer, ms);
        string text = writer.ToString();

        Assert.Contains("512.0 B", text);
        Assert.Contains("/ budget 1024 B", text);
        Assert.Contains("\tpass\r\n", text);
    }

    [Fact]
    public void WriteText_TimeViolation_Flags_VIOLATION()
    {
        var ms = new[] { Ms("Slow", meanNs: 300, budgetNs: 100) };
        using var writer = new StringWriter();
        PerfReport.WriteText(writer, ms);
        Assert.Contains("\tVIOLATION\r\n", writer.ToString());
    }

    [Fact]
    public void WriteText_AllocationViolation_Flags_VIOLATION()
    {
        var ms = new[] { Ms("Alloc", meanNs: 10, meanBytes: 2048, budgetBytes: 100) };
        using var writer = new StringWriter();
        PerfReport.WriteText(writer, ms);
        Assert.Contains("\tVIOLATION\r\n", writer.ToString());
    }

    [Fact]
    public void WriteText_Ignored_Measurement_Is_Flagged_Ignored()
    {
        var ms = new[] { Ms("IgnoredOne", meanNs: 999_999, budgetNs: 1, ignored: true) };
        using var writer = new StringWriter();
        PerfReport.WriteText(writer, ms);
        Assert.Contains("\tignored\r\n", writer.ToString());
    }

    [Fact]
    public void WriteText_Fallback_Path_Is_Used_For_Oversize_Values()
    {
        // A double that overflows the 32-char stack buffer (e.g. huge values) exercises
        // the ToString fallback in WriteNs/WriteLong without crashing.
        var ms = new[] { Ms("Huge", meanNs: double.MaxValue, meanBytes: double.MaxValue, budgetNs: long.MaxValue, budgetBytes: long.MaxValue) };
        using var writer = new StringWriter();
        PerfReport.WriteText(writer, ms);
        Assert.Contains("Huge", writer.ToString());
    }

    [Fact]
    public void ToText_Returns_Same_Content_As_WriteText()
    {
        var ms = new[] { Ms("A", 1), Ms("B", 2, meanBytes: 3, budgetNs: 5, budgetBytes: 6, isBaseline: true) };
        using var writer = new StringWriter();
        PerfReport.WriteText(writer, ms);
        Assert.Equal(writer.ToString().Replace("\r\n", "\n"), PerfReport.ToText(ms).Replace("\r\n", "\n"));
    }

    [Fact]
    public void Evaluate_Empty_Batch_Passes()
    {
        Assert.True(PerfReport.Evaluate(ReadOnlySpan<PerfMeasurement>.Empty, out int t, out int a));
        Assert.Equal(0, t);
        Assert.Equal(0, a);
    }

    [Fact]
    public void Evaluate_Counts_Time_And_Allocation_Violations()
    {
        var ms = new[]
        {
            Ms("Fast-Alloc", meanNs: 1, meanBytes: 2000, budgetNs: 100, budgetBytes: 100), // allocation breach
            Ms("Slow-NoAlloc", meanNs: 500, budgetNs: 100), // time breach
        };
        Assert.False(PerfReport.Evaluate(ms, out int t, out int a));
        Assert.Equal(1, t);
        Assert.Equal(1, a);
    }

    [Fact]
    public void Evaluate_Ignores_Report_Only_Entries()
    {
        var ms = new[]
        {
            Ms("Ignored", meanNs: 999, meanBytes: 999, budgetNs: 1, budgetBytes: 1, ignored: true),
            Ms("Clean", meanNs: 1, budgetNs: 100),
        };
        Assert.True(PerfReport.Evaluate(ms, out int t, out int a));
        Assert.Equal(0, t);
        Assert.Equal(0, a);
    }

    [Fact]
    public void TimeViolated_Is_False_Without_Time_Budget()
    {
        var m = Ms("NoBudget", meanNs: 1_000);
        Assert.False(m.TimeViolated);
    }

    [Fact]
    public void AllocationViolated_Is_False_When_NaNs()
    {
        var m = Ms("NoBytes", meanNs: 1, meanBytes: double.NaN, budgetBytes: 100);
        Assert.False(m.AllocationViolated);
    }
}