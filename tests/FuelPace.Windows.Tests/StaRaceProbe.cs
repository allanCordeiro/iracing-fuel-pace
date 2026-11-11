namespace FuelPace.Windows.Tests;

/// <summary>The start of the Windows test run, where SettingsInputTests and SettingsScreenshotTests begin together on fresh STA
/// threads, made tighter: four Settings bodies start at once in a fresh process. Without the gate in <see cref="Sta"/> it
/// failed 8 of 80 fresh processes on CI (corrupted WPF state, one native heap corruption). Runs only with FP_STA_RACE=1: the CI
/// step "Settings STA stress" starts it in many fresh processes.</summary>
public sealed class StaRaceProbe
{
    [Fact]
    public void Concurrent_first_use_of_the_Settings_views()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("FP_STA_RACE") == "1", "FP_STA_RACE=1 runs it");
        Action[] bodies =
        [
            () => new SettingsInputTests().Typing_a_new_shortcut_sends_it_to_the_Agent(),
            () => new SettingsScreenshotTests().Diagnostics_page_renders(),
            () => new SettingsInputTests().Decimals_accept_a_comma_or_a_point_and_show_a_point(),
            () => new SettingsScreenshotTests().Capture_page_matches_mock_7(),
        ];
        using var go = new Barrier(bodies.Length);
        var errors = new Exception?[bodies.Length];
        var threads = bodies.Select((b, i) => new Thread(() =>
        {
            go.SignalAndWait();
            try { b(); }
            catch (Exception e) { errors[i] = e; }
        }) { IsBackground = true }).ToArray();
        foreach (var t in threads) t.Start();
        foreach (var t in threads) t.Join();
        var failed = errors.Where(e => e is not null).ToArray();
        if (failed.Length > 0) throw new AggregateException(failed!);
    }
}
