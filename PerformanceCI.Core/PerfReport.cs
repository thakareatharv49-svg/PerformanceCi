using System;
using System.IO;
using System.Runtime.CompilerServices;

namespace PerformanceCI;

/// <summary>
/// A single measured entry destined for a report. Value type; stored in caller-provided
/// buffers so the aggregation phase allocates nothing beyond the buffer itself.
/// </summary>
public readonly struct PerfMeasurement
{
    /// <summary>Simple method or operation name.</summary>
    public readonly string Name;

    /// <summary>Mean elapsed time, nanoseconds.</summary>
    public readonly double MeanNanoseconds;

    /// <summary>Mean allocated bytes (NaN when not measured).</summary>
    public readonly double MeanAllocatedBytes;

    /// <summary>Declared time budget in ns, or -1 when unbudgeted.</summary>
    public readonly long BudgetNanoseconds;

    /// <summary>Declared allocation budget in bytes, or -1 when unbudgeted.</summary>
    public readonly long BudgetBytes;

    /// <summary>True when this entry is a comparison baseline.</summary>
    public readonly bool IsBaseline;

    /// <summary>True when the entry carries <see cref="PerfIgnoreAttribute"/> (report only, never gates).</summary>
    public readonly bool Ignored;

    internal PerfMeasurement(string name, double meanNs, double meanBytes, long budgetNs, long budgetBytes, bool isBaseline, bool ignored)
    {
        Name = name;
        MeanNanoseconds = meanNs;
        MeanAllocatedBytes = meanBytes;
        BudgetNanoseconds = budgetNs;
        BudgetBytes = budgetBytes;
        IsBaseline = isBaseline;
        Ignored = ignored;
    }

    /// <summary>Time-gate result: true when the entry exceeded a declared time budget.</summary>
    public bool TimeViolated => BudgetNanoseconds >= 0 && MeanNanoseconds > BudgetNanoseconds;

    /// <summary>Allocation-gate result: true when the entry exceeded a declared byte budget.</summary>
    public bool AllocationViolated => BudgetBytes >= 0 && !double.IsNaN(MeanAllocatedBytes) && MeanAllocatedBytes > BudgetBytes;
}

/// <summary>
/// Streaming, allocation-light report writer. Output goes straight to a caller-provided
/// <see cref="TextWriter"/> (file or stdout) — no intermediate report string is built on
/// this path. Numbers are formatted via <see cref="double.TryFormat(Span{char},out int,ReadOnlySpan{char},IFormatProvider)"/>
/// into a single reused <see langword="stackalloc"/> buffer, so no per-row number strings
/// are allocated. <see cref="ToText"/> is the convenience wrapper for callers that prefer
/// a returned <see cref="string"/> and accept one allocation.
/// </summary>
public static class PerfReport
{
    private const int NumBuf = 32;

    /// <summary>
    /// Writes a plain-text report of every measurement to <paramref name="writer"/>.
    /// One <c>WriteLine</c> per row; violations are flagged inline. O(1) per row,
    /// 0 allocations per row (names write as-is; numbers go through the stack buffer).
    /// </summary>
    public static void WriteText(TextWriter writer, ReadOnlySpan<PerfMeasurement> measurements)
    {
        writer.WriteLine("# PerformanceCI Report");
        writer.WriteLine();

        if (measurements.IsEmpty)
        {
            writer.WriteLine("(no measurements)");
            return;
        }

        foreach (ref readonly var m in measurements)
        {
            writer.Write(m.Name);
            writer.Write(":\t");
            WriteNs(writer, m.MeanNanoseconds);
            writer.Write(" ns");
            if (m.BudgetNanoseconds >= 0)
            {
                writer.Write(" / budget ");
                WriteLong(writer, m.BudgetNanoseconds);
                writer.Write(" ns");
            }

            if (!double.IsNaN(m.MeanAllocatedBytes))
            {
                writer.Write("\t");
                WriteNs(writer, m.MeanAllocatedBytes);
                writer.Write(" B");
                if (m.BudgetBytes >= 0)
                {
                    writer.Write(" / budget ");
                    WriteLong(writer, m.BudgetBytes);
                    writer.Write(" B");
                }
            }

            writer.Write("\t");
            writer.WriteLine(StatusOf(m));
        }
    }

    private static void WriteNs(TextWriter writer, double value)
    {
        Span<char> buffer = stackalloc char[NumBuf];
        if (value.TryFormat(buffer, out int written, "F1", System.Globalization.CultureInfo.InvariantCulture))
            writer.Write(buffer[..written]);
        else
            writer.Write(value.ToString("F1", System.Globalization.CultureInfo.InvariantCulture));
    }

    private static void WriteLong(TextWriter writer, long value)
    {
        Span<char> buffer = stackalloc char[NumBuf];
        if (value.TryFormat(buffer, out int written, ReadOnlySpan<char>.Empty, System.Globalization.CultureInfo.InvariantCulture))
            writer.Write(buffer[..written]);
        else
            writer.Write(value.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>Plain-text report as a single string. Allocates the one result buffer.</summary>
    public static string ToText(ReadOnlySpan<PerfMeasurement> measurements)
    {
        using var writer = new System.IO.StringWriter(System.Globalization.CultureInfo.InvariantCulture);
        WriteText(writer, measurements);
        return writer.ToString();
    }

    /// <summary>
    /// Verdict over a batch, ignoring report-only entries. <see langword="true"/> = green.
    /// </summary>
    public static bool Evaluate(ReadOnlySpan<PerfMeasurement> measurements, out int timeViolations, out int allocationViolations)
    {
        timeViolations = 0;
        allocationViolations = 0;
        foreach (ref readonly var m in measurements)
        {
            if (m.Ignored)
                continue;
            if (m.TimeViolated)
                timeViolations++;
            if (m.AllocationViolated)
                allocationViolations++;
        }
        return timeViolations == 0 && allocationViolations == 0;
    }

    private static string StatusOf(in PerfMeasurement m)
    {
        if (m.Ignored)
            return "ignored";
        if (m.TimeViolated || m.AllocationViolated)
            return "VIOLATION";
        return m.BudgetNanoseconds >= 0 || m.BudgetBytes >= 0 ? "pass" : "info";
    }
}