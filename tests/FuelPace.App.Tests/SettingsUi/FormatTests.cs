using FuelPace.App.SettingsUi;
using FuelPace.Core.Laps;

namespace FuelPace.App.Tests.SettingsUi;

public class FormatTests
{
    [Theory]
    [InlineData(80.897, "1:20.897")]
    [InlineData(93.175, "1:33.175")]
    [InlineData(59.9996, "1:00.000")]
    [InlineData(5.5, "0:05.500")]
    [InlineData(3725.25, "62:05.250")]
    public void Lap_time_is_m_ss_mmm(double s, string text) => Assert.Equal(text, LapTimeFormat.Format(s));

    [Theory]
    [InlineData(null)]
    [InlineData(double.NaN)]
    [InlineData(-1.0)]
    public void Missing_time_is_a_dash(double? s) => Assert.Equal("—", LapTimeFormat.Format(s));

    [Fact]
    public void Reasons_are_joined_in_the_mock_order()
    {
        Assert.Equal("", ReasonLabels.Format(LapReasons.None));
        Assert.Equal("pit, incidente", ReasonLabels.Format(LapReasons.Pit | LapReasons.Incident));
        Assert.Equal("fora da pista, incidente", ReasonLabels.Format(LapReasons.Incident | LapReasons.OffTrack));
        Assert.Equal("pit, fora da pista, incidente, bandeira amarela, reset/reboque, reabastecimento, incompleta, lenta, excluída",
            ReasonLabels.Format((LapReasons)511));
    }
}
