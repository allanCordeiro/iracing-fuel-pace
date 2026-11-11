using FuelPace.App.SettingsUi;

namespace FuelPace.App.Tests.SettingsUi;

/// <summary>Ruling A25: the Settings fields show numbers with '.', and accept ',' or '.' typed by a pt-BR pilot.</summary>
public class NumberInputTests
{
    [Theory]
    [InlineData(0.5, "0.5")]
    [InlineData(6.0, "6")]
    [InlineData(0.0, "0")]
    [InlineData(12.25, "12.25")]
    public void Shows_numbers_with_a_point(double v, string text) => Assert.Equal(text, NumberInput.Format(v));

    [Theory]
    [InlineData("0,5", 0.5)]
    [InlineData("0.5", 0.5)]
    [InlineData(" 6 ", 6.0)]
    [InlineData("1,25", 1.25)]
    [InlineData("-1", -1.0)]
    public void Accepts_a_comma_or_a_point(string text, double expected)
    {
        Assert.True(NumberInput.TryParseDouble(text, out double v));
        Assert.Equal(expected, v);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("1,000.5")]
    [InlineData("1.2.3")]
    [InlineData("NaN")]
    [InlineData("∞")]
    public void Rejects_what_is_not_one_number(string? text) => Assert.False(NumberInput.TryParseDouble(text, out _));

    [Theory]
    [InlineData("5", 5)]
    [InlineData(" 1840 ", 1840)]
    [InlineData("-40", -40)]
    public void Integers_parse(string text, int expected)
    {
        Assert.True(NumberInput.TryParseInt(text, out int v));
        Assert.Equal(expected, v);
    }

    [Theory]
    [InlineData("5,5")]
    [InlineData("5.5")]
    [InlineData("x")]
    [InlineData("1 000")]
    public void Integers_reject_fractions_and_text(string text) => Assert.False(NumberInput.TryParseInt(text, out _));
}
