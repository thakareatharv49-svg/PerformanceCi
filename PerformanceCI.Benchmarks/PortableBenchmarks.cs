using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using PerformanceCI;

namespace PerformanceCI.Benchmarks;

/// <summary>Measures portable prediction and text normalization operations.</summary>
[MemoryDiagnoser]
[SimpleJob(launchCount: 1, warmupCount: 1, iterationCount: 3)]
// Class-level budgets apply to any method that does not declare its own.
[PerfCritical(2000)]
[PerfAllocation(512)]
public class PortableBenchmarks
{
    private readonly Dictionary<string, List<string>> _predictionCache = new();

    /// <summary>Measures prediction cache insertion and lookup.</summary>
    [Benchmark]
    [PerfCritical(1000)]
    public void CachePrediction()
    {
        _predictionCache["i need"] = ["water", "help", "a break"];
        _predictionCache.TryGetValue("i need", out _);
    }

    /// <summary>Measures invariant phrase normalization.</summary>
    [Benchmark]
    // No method-level budget: inherits the class 2000 ns / 512 B gates.
    public string NormalizePhrase()
    {
        return "I Need A Break".ToLowerInvariant();
    }

    /// <summary>Baseline for comparison gating.</summary>
    [Benchmark]
    [PerfCritical(500)]
    [PerfBaseline("phrase")]
    public string BaselinePhrase()
    {
        return "hello";
    }

    /// <summary>Must stay within 2x of the baseline mean.</summary>
    [Benchmark]
    [PerfComparison(maxRatio: 2.0, group: "phrase")]
    public string ChallengerPhrase()
    {
        return "hello" + " world";
    }

    /// <summary>Report-only; never gates.</summary>
    [Benchmark]
    [PerfIgnore]
    public string ReferenceOnly()
    {
        return "always shown, never failed";
    }
}