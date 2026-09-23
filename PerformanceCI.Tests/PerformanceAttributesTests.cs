using PerformanceCI;
using Xunit;

namespace PerformanceCI.Tests;

/// <summary>Verifies attribute metadata semantics (validation, defaults, usage) without running any measured code.</summary>
public sealed class PerfCriticalAttributeTests
{
    [Fact]
    public void Valid_Budget_IsStored()
    {
        var attr = new PerfCriticalAttribute(1234);
        Assert.Equal(1234, attr.ThresholdNanoseconds);
    }

    [Fact]
    public void Budget_Zero_Throws_ArgumentOutOfRange()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => new PerfCriticalAttribute(0));
        Assert.Contains("thresholdNanoseconds", ex.ParamName);
    }

    [Fact]
    public void Budget_Negative_Throws_ArgumentOutOfRange()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => new PerfCriticalAttribute(-5));
        Assert.Contains("thresholdNanoseconds", ex.ParamName);
    }

    [Fact]
    public void AppliedTo_Method_And_Class_Only()
    {
        var usages = Attribute.GetCustomAttribute(typeof(PerfCriticalAttribute), typeof(AttributeUsageAttribute))
            as AttributeUsageAttribute;
        Assert.NotNull(usages);
        Assert.True(usages.ValidOn.HasFlag(AttributeTargets.Method));
        Assert.True(usages.ValidOn.HasFlag(AttributeTargets.Class));
        Assert.False(usages.AllowMultiple);
        Assert.True(usages.Inherited);
    }
}

/// <summary>PerfIgnore is a marker attribute: constructible, no behavior, valid on method/class.</summary>
public sealed class PerfIgnoreAttributeTests
{
    [Fact]
    public void Default_Instance_Is_Valid_Marker()
    {
        var attr = new PerfIgnoreAttribute();
        Assert.NotNull(attr);
        var usages = Attribute.GetCustomAttribute(typeof(PerfIgnoreAttribute), typeof(AttributeUsageAttribute))
            as AttributeUsageAttribute;
        Assert.NotNull(usages);
        Assert.True(usages.ValidOn.HasFlag(AttributeTargets.Method));
        Assert.True(usages.ValidOn.HasFlag(AttributeTargets.Class));
    }
}

/// <summary>Verifies baseline defaults and null-group normalization.</summary>
public sealed class PerfBaselineAttributeTests
{
    [Fact]
    public void Parameterless_Ctor_Defaults_To_Empty_Group()
    {
        var attr = new PerfBaselineAttribute();
        Assert.Equal(string.Empty, attr.Group);
    }

    [Fact]
    public void Explicit_Group_Is_Stored()
    {
        var attr = new PerfBaselineAttribute("phrase");
        Assert.Equal("phrase", attr.Group);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Null_Group_Is_Normalized_To_Empty(string? group)
    {
        var attr = new PerfBaselineAttribute(group!);
        Assert.Equal(string.Empty, attr.Group);
    }
}

/// <summary>Verifies allocation attribute validation and storage.</summary>
public sealed class PerfAllocationAttributeTests
{
    [Fact]
    public void Valid_MaxBytes_Is_Stored()
    {
        var attr = new PerfAllocationAttribute(4096);
        Assert.Equal(4096, attr.MaxBytes);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Non_Positive_MaxBytes_Throws(int maxBytes)
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => new PerfAllocationAttribute(maxBytes));
        Assert.Contains("maxBytes", ex.ParamName);
    }

    [Fact]
    public void AppliedTo_Method_And_Class_Only()
    {
        var usages = Attribute.GetCustomAttribute(typeof(PerfAllocationAttribute), typeof(AttributeUsageAttribute))
            as AttributeUsageAttribute;
        Assert.NotNull(usages);
        Assert.True(usages.ValidOn.HasFlag(AttributeTargets.Method));
        Assert.True(usages.ValidOn.HasFlag(AttributeTargets.Class));
    }
}

/// <summary>Verifies comparison attribute ratio validation and group handling.</summary>
public sealed class PerfComparisonAttributeTests
{
    [Fact]
    public void Valid_Ratio_And_Group_Are_Stored()
    {
        var attr = new PerfComparisonAttribute(maxRatio: 1.5, group: "phrase");
        Assert.Equal(1.5, attr.MaxRatio);
        Assert.Equal("phrase", attr.Group);
    }

    [Fact]
    public void Default_Group_Is_Empty()
    {
        var attr = new PerfComparisonAttribute(2.0);
        Assert.Equal(string.Empty, attr.Group);
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(0.5)]
    [InlineData(double.NaN)]
    public void Ratio_Not_Strictly_Greater_Than_One_Throws(double ratio)
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => new PerfComparisonAttribute(ratio));
        Assert.Contains("maxRatio", ex.ParamName);
    }

    [Fact]
    public void Infinity_Ratio_Is_Allowed_As_Unlimited()
    {
        // +Inf > 1.0, so it is a valid (effectively unlimited) ratio.
        var attr = new PerfComparisonAttribute(double.PositiveInfinity);
        Assert.True(double.IsInfinity(attr.MaxRatio));
    }

    [Fact]
    public void Null_Group_Is_Normalized_To_Empty()
    {
        var attr = new PerfComparisonAttribute(2.0, null!);
        Assert.Equal(string.Empty, attr.Group);
    }
}