using PerformanceCI;
using Xunit;

namespace PerformanceCI.Tests;

/// <summary>
/// Verifies the reflection side of the library: how method/class attributes combine,
/// how comparison gates rewrite budgets, and the end-to-end console gate.
/// </summary>
public sealed class PerfEvaluatorTests
{
    // ---- Fixtures (attributes describe budgets; measured means come from dictionaries) ----

    private sealed class DefaultFixture
    {
        [PerfCritical(500)]
        [PerfAllocation(64)]
        [PerfBaseline("grp1")]
        public void Budgeted() { }

        [PerfCritical(501)]
        public void TimedOnly() { }

        [PerfIgnore]
        public void ReportOnly() { }
    }

    private sealed class ClassBudgetFixture
    {
        [PerfCritical(2000)]
        [PerfAllocation(512)]
        public void InheritsClassBudgets() { }

        [PerfCritical(100)]
        [PerfAllocation(128)]
        public void OverridesClassBudgets() { }
    }

    private sealed class ClassIgnoredFixture
    {
        [PerfIgnore]
        public string ReferenceOnly() => "";
    }

    private sealed class ComparisonFixture
    {
        [PerfBaseline("groupA")]
        public void BaselineA() { }

        [PerfComparison(maxRatio: 2.0, group: "groupA")]
        public void ChallengerA2x() { }

        [PerfComparison(maxRatio: 3.0, group: "groupA")]
        public void ChallengerA3x() { }
    }

    private sealed class ComparisonGroupNamedBaselineFixture
    {
        // Baseline method whose Name equals the group key: exercises the explicit
        // name-match path in FindBaseline (rather than "first baseline" fallback).
        [PerfBaseline("groupNameMatch")]
        public void groupNameMatch() { }

        [PerfComparison(maxRatio: 2.0, group: "groupNameMatch")]
        public void Challenger() { }
    }

    private sealed class ComparisonNoBaselineFixture
    {
        [PerfComparison(maxRatio: 2.0, group: "missing")]
        public void Orphan() { }
    }

    private sealed class ComparisonBaselineStillSnanFixture
    {
        [PerfBaseline("grp")]
        public void Base() { }

        [PerfComparison(maxRatio: 2.0, group: "grp")]
        public void Challenger() { }
    }

    // ---- Helpers ----

    private static Dictionary<string, double> Map(params (string name, double value)[] rows)
    {
        var d = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var (name, value) in rows)
            d[name] = value;
        return d;
    }

    private static PerfMeasurement[] Build(Type type, Dictionary<string, double> ns, Dictionary<string, double> bytes)
        => PerfEvaluator.BuildMeasurements(type, ns, bytes);

    private static PerfMeasurement ByName(PerfMeasurement[] ms, string name)
    {
        var found = ms.FirstOrDefault(m => m.Name == name);
        Assert.NotNull(found);
        Assert.Equal(name, found.Name);
        return found;
    }

    // ---- Budget resolution ----

    [Fact]
    public void Method_Attributes_Resolve_Budgets_And_Baseline()
    {
        var ns = Map(("Budgeted", 42.0));
        var bytes = Map(("Budgeted", 8.0));
        var ms = Build(typeof(DefaultFixture), ns, bytes);

        var m = ByName(ms, "Budgeted");
        Assert.Equal(500L, m.BudgetNanoseconds);
        Assert.Equal(64L, m.BudgetBytes);
        Assert.True(m.IsBaseline);
        Assert.False(m.Ignored);
        Assert.Equal(42.0, m.MeanNanoseconds);
        Assert.Equal(8.0, m.MeanAllocatedBytes);
    }

    [Fact]
    public void Method_Without_Allocation_Attribute_Keeps_No_Byte_Budget()
    {
        var ns = Map(("TimedOnly", 42.0));
        var ms = Build(typeof(DefaultFixture), ns, new Dictionary<string, double>());

        var m = ByName(ms, "TimedOnly");
        Assert.Equal(501L, m.BudgetNanoseconds);
        Assert.Equal(-1L, m.BudgetBytes); // no budget declared
        Assert.False(m.Ignored);
    }

    [Fact]
    public void Class_Budgets_Apply_When_Method_Has_None()
    {
        var ns = Map(("InheritsClassBudgets", 10.0), ("OverridesClassBudgets", 20.0));
        var ms = Build(typeof(ClassBudgetFixture), ns, new Dictionary<string, double>());

        Assert.Equal(2000L, ByName(ms, "InheritsClassBudgets").BudgetNanoseconds);
        Assert.Equal(512L, ByName(ms, "InheritsClassBudgets").BudgetBytes);
        Assert.Equal(100L, ByName(ms, "OverridesClassBudgets").BudgetNanoseconds);
        Assert.Equal(128L, ByName(ms, "OverridesClassBudgets").BudgetBytes);
    }

    [Fact]
    public void Class_Ignore_Propagates_To_All_Methods()
    {
        var ns = Map(("ReferenceOnly", 123.0));
        var ms = Build(typeof(ClassIgnoredFixture), ns, new Dictionary<string, double>());

        var m = ByName(ms, "ReferenceOnly");
        Assert.True(m.Ignored);
    }

    [Fact]
    public void Missing_Measurement_Becomes_NaN_And_Does_Not_Violate_Time()
    {
        var ms = Build(typeof(DefaultFixture), new Dictionary<string, double>(), new Dictionary<string, double>());
        var m = ByName(ms, "Budgeted");
        Assert.True(double.IsNaN(m.MeanNanoseconds));
        Assert.False(m.TimeViolated); // NaN > budget is false
    }

    [Fact]
    public void Baseline_Flag_Defaults_To_Class_Level()
    {
        var ms = Build(typeof(ClassBudgetFixture), new Dictionary<string, double>(), new Dictionary<string, double>());
        // No class-level PerfBaseline; both methods must NOT be baselines.
        Assert.All(ms, m => Assert.False(m.IsBaseline));
    }

    // ---- Comparison gates ----

    [Fact]
    public void Comparison_Challenger_Passing_Budget()
    {
        var ns = Map(("BaselineA", 100.0), ("ChallengerA2x", 150.0));
        var ms = Build(typeof(ComparisonFixture), ns, new Dictionary<string, double>());
        var m = ByName(ms, "ChallengerA2x");
        // budget = ceil(100 * 2.0) = 200; 150 <= 200 => pass
        Assert.Equal(200L, m.BudgetNanoseconds);
        Assert.False(m.TimeViolated);
    }

    [Fact]
    public void Comparison_Challenger_Breaching_Budget()
    {
        var ns = Map(("BaselineA", 100.0), ("ChallengerA2x", 250.0));
        var ms = Build(typeof(ComparisonFixture), ns, new Dictionary<string, double>());
        var m = ByName(ms, "ChallengerA2x");
        // budget = 200; 250 > 200 => violation
        Assert.Equal(200L, m.BudgetNanoseconds);
        Assert.True(m.TimeViolated);
    }

    [Fact]
    public void Comparison_Budget_Uses_Ceiling()
    {
        var ns = Map(("BaselineA", 101.0), ("ChallengerA2x", 202.0));
        var ms = Build(typeof(ComparisonFixture), ns, new Dictionary<string, double>());
        // ceil(101 * 2.0) = ceil(202.0) = 202; exactly at budget => pass
        Assert.Equal(202L, ByName(ms, "ChallengerA2x").BudgetNanoseconds);
        Assert.False(ByName(ms, "ChallengerA2x").TimeViolated);
    }

    [Fact]
    public void Comparison_Ratio_Budget_Depends_On_Its_Own_Ratio()
    {
        var ns = Map(("BaselineA", 100.0), ("ChallengerA3x", 250.0));
        var ms = Build(typeof(ComparisonFixture), ns, new Dictionary<string, double>());
        // ceil(100 * 3.0) = 300; 250 <= 300 => pass
        Assert.Equal(300L, ByName(ms, "ChallengerA3x").BudgetNanoseconds);
        Assert.False(ByName(ms, "ChallengerA3x").TimeViolated);
    }

    [Fact]
    public void Comparison_Without_Baseline_Leaves_Challenger_Unbudgeted()
    {
        var ms = Build(typeof(ComparisonNoBaselineFixture), new Dictionary<string, double>(), new Dictionary<string, double>());
        var m = ByName(ms, "Orphan");
        Assert.Equal(-1L, m.BudgetNanoseconds); // no baseline found => skip rewrite
    }

    [Fact]
    public void Comparison_With_NaN_Baseline_Leaves_Challenger_Unbudgeted()
    {
        // BaselineA measured as NaN => baseline mean is NaN => skip
        var ns = Map(("Challenger", 50.0)); // baseline not measured
        var ms = Build(typeof(ComparisonBaselineStillSnanFixture), ns, new Dictionary<string, double>());
        var m = ByName(ms, "Challenger");
        Assert.Equal(-1L, m.BudgetNanoseconds);
    }

    [Fact]
    public void Comparison_Baseline_Itself_Keeps_No_Budget()
    {
        var ns = Map(("BaselineA", 100.0));
        var ms = Build(typeof(ComparisonFixture), ns, new Dictionary<string, double>());
        Assert.Equal(-1L, ByName(ms, "BaselineA").BudgetNanoseconds);
        Assert.True(ByName(ms, "BaselineA").IsBaseline);
    }

    [Fact]
    public void Comparison_Group_Name_Matches_Baseline_Method_Name()
    {
        // Explicit group match: baseline method's Name equals the comparison group key.
        var ns = Map(("groupNameMatch", 100.0), ("Challenger", 150.0));
        var ms = Build(typeof(ComparisonGroupNamedBaselineFixture), ns, new Dictionary<string, double>());
        Assert.Equal(200L, ByName(ms, "Challenger").BudgetNanoseconds); // ceil(100 * 2.0)
        Assert.False(ByName(ms, "Challenger").TimeViolated);
    }

    // ---- Evaluate / verdict ----

    [Fact]
    public void PerfVerdict_Passes_When_All_Within_Budget()
    {
        var ns = Map(("Budgeted", 42.0));
        var ms = Build(typeof(DefaultFixture), ns, new Dictionary<string, double>());
        bool pass = PerfVerdict.Pass(ms, out int t, out int a);
        Assert.True(pass);
        Assert.Equal(0, t);
        Assert.Equal(0, a);
    }

    [Fact]
    public void PerfVerdict_Fails_On_Time_Or_Allocation_Breach()
    {
        var ns = Map(("Budgeted", 900.0)); // budget 500 => breach
        var ms = Build(typeof(DefaultFixture), ns, new Dictionary<string, double>());
        bool pass = PerfVerdict.Pass(ms, out int t, out int a);
        Assert.False(pass);
        Assert.Equal(1, t);
    }

    [Fact]
    public void PerfVerdict_Ignores_Report_Only_Methods()
    {
        var ns = Map(("ReportOnly", 999999.0));
        var ms = Build(typeof(DefaultFixture), ns, new Dictionary<string, double>());
        // report-only has no budget => no violation even though budget-less
        bool pass = PerfVerdict.Pass(ms, out int t, out int a);
        Assert.True(pass);
    }

    // ---- Console gate ----

    [Fact]
    public void RunAndReport_Writes_Report_File_And_Returns_Zero_On_Pass()
    {
        string tmp = Path.Combine(Path.GetTempPath(), $"perfci-{Guid.NewGuid():N}.txt");
        try
        {
            var ns = Map(("Budgeted", 42.0));
            using (var console = new StringWriter())
            {
                Console.SetOut(console);
                int code = PerfConsoleGate.RunAndReport(typeof(DefaultFixture), ns, new Dictionary<string, double>(), tmp);
                Assert.Equal(0, code);
                Assert.Contains("[PerfCI] time violations: 0, allocation violations: 0 -> PASS", console.ToString());
            }

            string report = File.ReadAllText(tmp);
            Assert.Contains("# PerformanceCI Report", report);
            Assert.Contains("Budgeted", report);
        }
        finally
        {
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
            if (File.Exists(tmp))
                File.Delete(tmp);
        }
    }

    [Fact]
    public void RunAndReport_Returns_One_On_Violation_And_Prints_To_Console()
    {
        var ns = Map(("Budgeted", 900.0)); // exceeds 500 ns budget
        using (var console = new StringWriter())
        {
            Console.SetOut(console);
            int code = PerfConsoleGate.RunAndReport(typeof(DefaultFixture), ns, new Dictionary<string, double>(), null);
            Assert.Equal(1, code);
            string output = console.ToString();
            Assert.Contains("[PerfCI] time violations: 1", output);
            Assert.Contains("VIOLATION", output);
        }
        Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
    }
}