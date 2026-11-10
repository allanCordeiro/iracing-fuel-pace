namespace FuelPace.App.Tests;

/// <summary>
/// Tests asserting an exact allocation count live here: xUnit runs this collection alone (see the Core tests' PerfCollection),
/// so no other test thread allocates while the window is measured.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PerfCollection
{
    public const string Name = "zero-allocation";
}
