using FuelPace.App.Host;
using FuelPace.App.Simulation;
using FuelPace.App.Tests.Fakes;
using FuelPace.Core.Display;
using FuelPace.Core.Pipeline;
using FuelPace.Core.Strategy;
using FuelPace.Tests.Support;

namespace FuelPace.App.Tests.Host;

[Collection(PerfCollection.Name)]
public sealed class RenderSchedulerTests
{
    /// <summary>Queues posts until the test runs them, like a busy UI thread.</summary>
    sealed class QueueUi : IUiDispatcher
    {
        public readonly Queue<Action> Queue = new();
        public int Posts;
        public bool Inline;

        public void Post(Action a)
        {
            Posts++;
            if (Inline) a();
            else Queue.Enqueue(a);
        }

        public void RunAll()
        {
            while (Queue.TryDequeue(out var a)) a();
        }
    }

    static void Publish(OverlayStatePublisher p, double fuel)
    {
        var s = new OverlayState { State = SignalState.Blue, FuelL = fuel };
        p.Publish(in s, GlanceViewBuilder.Build(in s, GlanceConfig.Default));
    }

    [Fact]
    public void Requests_are_spaced_100ms_apart_and_never_stacked()
    {
        var pub = new OverlayStatePublisher();
        var clock = new ManualClock(SyntheticRace.T0);
        var ui = new QueueUi();
        int renders = 0;
        var s = new RenderScheduler(pub, ui, () => renders++, clock);

        Assert.True(s.Check()); // the first check always asks for the initial render
        ui.RunAll();
        Assert.False(s.Check()); // nothing changed

        Publish(pub, 10);
        Assert.False(s.Check()); // < 100 ms since the last request
        clock.Advance(TimeSpan.FromMilliseconds(99));
        Assert.False(s.Check());
        clock.Advance(TimeSpan.FromMilliseconds(1));
        Assert.True(s.Check());

        Publish(pub, 11);
        clock.Advance(TimeSpan.FromMilliseconds(500));
        Assert.False(s.Check()); // the previous request has not run yet: at most one pending
        ui.RunAll();
        Assert.True(s.Check());
        ui.RunAll();

        clock.Advance(TimeSpan.FromMilliseconds(200));
        Assert.False(s.Check());
        s.InvalidateOptions(); // view options changed
        Assert.True(s.Check());
        ui.RunAll();
        Assert.Equal(4, renders);
        Assert.Equal(4, ui.Posts);
    }

    [Fact]
    public void Check_allocates_nothing()
    {
        var pub = new OverlayStatePublisher();
        var clock = new ManualClock(SyntheticRace.T0);
        var ui = new QueueUi { Inline = true };
        int renders = 0;
        var s = new RenderScheduler(pub, ui, () => renders++, clock);
        var step = TimeSpan.FromMilliseconds(150);
        for (int i = 0; i < 20; i++)
        {
            Publish(pub, i);
            clock.Advance(step);
            s.Check();
            s.Check();
        }

        var m = AllocationMeasure.Start();
        for (int i = 0; i < 200; i++)
        {
            Publish(pub, 100 + i);
            clock.Advance(step);
            s.Check();
            s.Check();
        }
        m.AssertNone();
        Assert.Equal(220, renders);
    }
}

[Collection(PerfCollection.Name)]
public sealed class AgentHostHotPathTests
{
    [Fact]
    public void Frame_tap_allocates_nothing_per_frame()
    {
        using var rig = new HostRig(debug: true); // with the recorder in the tap
        rig.Start();
        rig.WaitUntil(() => rig.Host.RecorderRunning, "recorder started");
        var tap = rig.Host.FrameTap;
        var frames = SyntheticRace.Frames(1, false).Take(300).ToArray();
        foreach (var f in frames.AsSpan(0, 100)) tap.OnFrame(in f);

        var m = AllocationMeasure.Start();
        foreach (var f in frames.AsSpan(100)) tap.OnFrame(in f);
        m.AssertNone();
    }
}
