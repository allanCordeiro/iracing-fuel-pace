using System.IO.Compression;
using FuelPace.App.Diagnostics;
using FuelPace.App.Ipc;
using FuelPace.App.Simulation;
using FuelPace.App.Tests.Fakes;
using FuelPace.Core.Capture;
using FuelPace.Core.Storage;
using FuelPace.Core.Telemetry;

namespace FuelPace.App.Tests.Host;

/// <summary>Swaps the process-wide identity <see cref="Privacy"/> scrubs: nothing else may run meanwhile.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PrivacyCollection
{
    public const string Name = "privacy identity";
}

[Collection(PrivacyCollection.Name)]
public sealed class AgentHostPrivacyTests
{
    [Fact] // carry-over (b)
    public async Task Bundle_carries_no_user_profile_path_or_user_name()
    {
        using var rig = new HostRig { ErrorsExpected = true }; // the failed save below is logged as an error
        string profile = Path.Combine(rig.Root, "Users", "fpuser");
        rig.Options = rig.Options with { AppDataRoot = Path.Combine(profile, "AppData", "Roaming", "FuelPace") };
        Directory.CreateDirectory(rig.AppData);
        using var identity = Privacy.OverrideForTests(profile, "fpuser");
        rig.Sys.Info["owner"] = "fpuser";

        var info = SessionInfoParser.Parse(SyntheticRace.Yaml);
        var store = new ProfileStore(rig.AppData);
        var doc = SyntheticRace.Profile(info);
        var rec = new CaptureRecord
        {
            Id = "2026-11-09T12:00:00Z", SessionType = "Practice",
            Conditions = new CaptureConditions(30, 20, 0, 0, 0, info.TankCapacityL),
        };
        for (int lap = 1; lap <= 4; lap++) rec.Laps.Add(new CapturedLap { Lap = lap, TimeS = 80, FuelL = 2.4, FullThrottlePct = 60 });
        ProfileOperations.AddCapture(doc, rec, SyntheticRace.T0);
        store.Save(doc);
        rig.Start();
        var probe = await rig.ConnectAsync();

        // A save that works, then one that fails with an OS error naming the full path.
        await probe.Client.SendAsync(new ProfileCommand(info.ProfileKey, "setCaptureActive", rec.Id, null, false));
        await probe.WaitFor<Profile>();
        Directory.CreateDirectory(store.PathFor(info.CarPath, info.TrackName) + ".tmp");
        await probe.Client.SendAsync(new ProfileCommand(info.ProfileKey, "setCaptureActive", rec.Id, null, true));
        await probe.WaitFor<Error>();

        await probe.Client.SendAsync(new CreateBundle(false));
        var created = await probe.WaitFor<BundleCreated>();
        using var zip = ZipFile.OpenRead(created.Path);
        string logs = "";
        foreach (var entry in zip.Entries)
        {
            using var r = new StreamReader(entry.Open());
            string text = r.ReadToEnd();
            Assert.DoesNotContain(profile, text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(profile.Replace("\\", "\\\\"), text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("fpuser", text, StringComparison.OrdinalIgnoreCase);
            if (entry.FullName.StartsWith("logs/", StringComparison.Ordinal)) logs += text;
        }
        Assert.Contains("save failed", logs);       // the failure is still explained...
        Assert.Contains("%USERPROFILE%", logs);     // ...with the path made anonymous
        Assert.Contains("owner: %USERNAME%", ReadEntry(zip, "system-info.txt"));
    }

    [Fact]
    public void Scrub_replaces_the_profile_path_in_any_spelling_and_the_name_as_a_word()
    {
        using var identity = Privacy.OverrideForTests(@"C:\Users\Ana Silva", "Ana Silva");
        Assert.Equal(@"%USERPROFILE%\AppData\x.json", Privacy.Scrub(@"c:\users\ana silva\AppData\x.json"));
        Assert.Equal("%USERPROFILE%/AppData", Privacy.Scrub("C:/Users/Ana Silva/AppData"));
        Assert.Equal(@"""p"":""%USERPROFILE%\\AppData""", Privacy.Scrub(@"""p"":""C:\\Users\\Ana Silva\\AppData"""));
        Assert.Equal("user %USERNAME% logged; Ana Silvana stays", Privacy.Scrub("user ana silva logged; Ana Silvana stays"));
    }

    static string ReadEntry(ZipArchive zip, string name)
    {
        using var r = new StreamReader(zip.GetEntry(name)!.Open());
        return r.ReadToEnd();
    }
}
