namespace FuelPace.Core.Display;

/// <summary>PRD v3 §6: the full panel (decision block), the compact one (no baseline, no target) or the dimmed inactive one.</summary>
public enum GlanceMode : byte { Full, Compact, Inactive }

/// <summary>PRD v3 §5.1: the decision word of the F1 block (its colour follows the hysteresis-held state).</summary>
public enum DecisionKind : byte { None, Economize, Ok, Pit }

/// <summary>PRD v3 §5.4: how the per-lap target of the F4 row is labelled.</summary>
public enum TargetLabel : byte { None, Alvo, Ate, PitMin }

/// <summary>PRD v3 §4.3: the status pills under the panel.</summary>
[Flags]
public enum StatusPills : byte { None = 0, Rec = 1, BopMismatch = 2, Caution = 4 }

/// <summary>
/// PRD v3 §8.1: everything the v3 overlay shows, already rounded as displayed, no strings. Value equality (record struct;
/// NaN equals NaN) is the redraw dirty flag (§8.4): two views that compare equal draw the same picture.
/// Conventions: a NaN float is shown as "—"; a short of -1 is shown as "—"; <see cref="BarMarkerQ"/> and
/// <see cref="BarGhostQ"/> use <see cref="BarAbsent"/> for "not drawn".
/// </summary>
public record struct GlanceView
{
    /// <summary>A bar marker or ghost that is not drawn.</summary>
    public const sbyte BarAbsent = sbyte.MinValue;

    public GlanceMode Mode;

    /// <summary>Selo and colour of the F1 block (hysteresis-held state). None outside <see cref="GlanceMode.Full"/>.</summary>
    public DecisionKind Decision;

    /// <summary>Economize: L/lap, 2 decimals. Ok (folga) and Pit (faltam): liters, 1 decimal. NaN = "—".</summary>
    public float DecisionValue;

    /// <summary>Economize only: the percent as displayed, negative (−3 → "−3%"); 0 = no complement.</summary>
    public sbyte ComplementPct;

    /// <summary>Economize with the seconds complement (CFG-V3-03) and a fuel-vs-time fit: s/lap, 1 decimal ("+0.9 s").
    /// NaN = use <see cref="ComplementPct"/>.</summary>
    public float ComplementSec;

    /// <summary>The F2 delta bar is drawn (Blue/Red and CFG-V3-01 on).</summary>
    public bool ShowBar;

    /// <summary>Q = steps per side of the bar (round(S / 0.01), 20 with S = 0.20).</summary>
    public byte BarSteps;

    /// <summary>Quantized positions −Q..+Q (0 = on the target); ±Q = saturated (drawn as a triangle). BarAbsent = not drawn.</summary>
    public sbyte BarMarkerQ, BarGhostQ;

    /// <summary>VOLTA (projection), ÚLT, MÉD: L/lap, 2 decimals; NaN = "—".</summary>
    public float LapProj, LapLast, LapAvg;
    public bool LapLastInvalid, LapAvgFromBaseline;

    /// <summary>CFG-V3-04: MÉD is shown in the F3 row.</summary>
    public bool ShowAvg;

    /// <summary>COMB: liters, 1 decimal; NaN = "—".</summary>
    public float Fuel;

    /// <summary>"dá N v": floor((C − res) / MÉD); -1 = "—".</summary>
    public short LapsPossible;

    /// <summary>"faltam N": ceil(L_rest), or the stint laps left when <see cref="IsStint"/>; -1 = "—".</summary>
    public short LapsRemaining;

    public bool IsStint;

    /// <summary>The target rests on a frozen leader pace (caution): drawn with a "~" prefix.</summary>
    public bool IsEstimate;

    public TargetLabel TargetLabel;

    /// <summary>ALVO / ATÉ / alvo (PIT): L/lap, 2 decimals; NaN = "—".</summary>
    public float Target;

    /// <summary>MÍN (PIT only): the feasible limit, L/lap, 2 decimals; NaN otherwise.</summary>
    public float FeasibleMin;

    /// <summary>Compact mode: minutes of the laps possible ("· 45 min"); -1 = "—".</summary>
    public short MinutesPossible;

    public StatusPills Pills;
    public short RecValid, RecTotal;

    /// <summary>Optional pace line (§5.5): seconds, 2 decimals; NaN = "—".</summary>
    public float LastLapTimeS, MeanLapTimeS, BaselineLapTimeS;
}
