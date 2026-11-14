namespace FuelPace.Core.Strategy;

/// <summary>The v2.1 insight (§7.5), declared in priority order after the overriding ones. The v3 overlay has no insight line;
/// the engine still computes it for the <c>InsightChanged</c> diagnostic.</summary>
public enum InsightKind : byte { None, ElectricUnsupported, StopRequired, BopMismatch, Save, Slack, OnTarget, NoBaseline }

/// <summary>
/// An insight and its arguments: StopRequired A = liters missing; Save A = L/lap, B = whole percent, C = s/lap or NaN;
/// Slack and OnTarget A = liters.
/// </summary>
public readonly record struct Insight(InsightKind Kind, double A = double.NaN, double B = double.NaN, double C = double.NaN);
