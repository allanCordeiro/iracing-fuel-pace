using FuelPace.Core.Display;

namespace FuelPace.Tests.Display;

public class NumberFormatTests
{
    static string F(float v, int d) { Span<char> b = stackalloc char[16]; return new string(b[..NumberFormat.Fixed(v, d, b)]); }
    static string C(int v) { Span<char> b = stackalloc char[16]; return new string(b[..NumberFormat.Count(v, b)]); }
    static string P(int v) { Span<char> b = stackalloc char[16]; return new string(b[..NumberFormat.Percent(v, b)]); }
    static string S(float v) { Span<char> b = stackalloc char[16]; return new string(b[..NumberFormat.SignedSeconds(v, b)]); }

    [Fact]
    public void Nan_is_em_dash()
    {
        Assert.Equal("—", F(float.NaN, 2));
        Assert.Equal("—", S(float.NaN));
    }

    [Fact]
    public void Negatives_use_U2212()
    {
        Assert.Equal("−0.23", F(-0.23f, 2));
        Assert.Equal("−5", F(-5f, 0));
        Assert.Equal("−3%", P(-3));
        Assert.Equal("−0.4 s", S(-0.4f));
    }

    [Fact]
    public void Negative_that_rounds_to_zero_has_no_sign()
    {
        Assert.Equal("0.00", F(-0.001f, 2));
        Assert.Equal("0", F(-0.4f, 0));
        Assert.Equal("+0.0 s", S(-0.04f));
    }

    [Fact]
    public void Decimals_and_invariant_culture()
    {
        Assert.Equal("2.39", F(2.39f, 2));
        Assert.Equal("74.0", F(74f, 1));
        Assert.Equal("2.300", F(2.3f, 3));
        Assert.Equal("108.46", F(108.46f, 2));
    }

    [Fact]
    public void Count_negative_is_dash()
    {
        Assert.Equal("—", C(-1));
        Assert.Equal("0", C(0));
        Assert.Equal("999", C(999));
    }

    [Fact] public void Percent_positive() => Assert.Equal("12%", P(12));

    [Fact] public void SignedSeconds_positive() => Assert.Equal("+0.9 s", S(0.9f));

    [Fact]
    public void Too_small_destination_returns_zero()
    {
        Span<char> b = stackalloc char[2];
        Assert.Equal(0, NumberFormat.Fixed(12.34f, 2, b));
        Assert.Equal(0, NumberFormat.Percent(-30, b));
        Assert.Equal(0, NumberFormat.SignedSeconds(0.9f, b));
        Assert.Equal(0, NumberFormat.Count(1234, b));
        Assert.Equal(0, NumberFormat.Fixed(1f, 1, Span<char>.Empty));
    }
}
