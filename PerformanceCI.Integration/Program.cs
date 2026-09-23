using System.Collections.Generic;
using System.Collections.Immutable;
using BenchmarkDotNet.Running;
using PerformanceCI;

namespace PerformanceCI.Integration;

/// <summary>Runs the SQLite integration suite and returns the PerfCI gate exit code.</summary>
public static class Program
{
    /// <summary>Runs the integration suite and returns the PerfCI gate exit code.</summary>
    /// <param name="args">Ignored command-line arguments.</param>
    /// <returns>0 when every perf gate passed, 1 otherwise.</returns>
    public static int Main(string[] args)
    {
        var summary = BenchmarkRunner.Run<SqliteVocabularyBenchmarks>();

        var meanNs = new System.Collections.Generic.Dictionary<string, double>(summary.Reports.Length, System.StringComparer.Ordinal);
        var meanBytes = new System.Collections.Generic.Dictionary<string, double>(summary.Reports.Length, System.StringComparer.Ordinal);

        foreach (var report in summary.Reports)
        {
            var name = report.BenchmarkCase.Descriptor.WorkloadMethod.Name;
            meanNs[name] = report.ResultStatistics?.Mean ?? double.NaN;
            if (report.Metrics is not null && report.Metrics.TryGetValue("Allocated", out var metric) && metric.Value is double b)
                meanBytes[name] = b;
        }

        return PerfConsoleGate.RunAndReport(typeof(SqliteVocabularyBenchmarks), meanNs, meanBytes,
            reportPath: System.IO.Path.Combine(System.AppContext.BaseDirectory, "perf-report.txt"));
    }
}