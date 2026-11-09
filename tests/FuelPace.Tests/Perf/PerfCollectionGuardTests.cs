using System.Text.RegularExpressions;
using FuelPace.Tests.Support;

namespace FuelPace.Tests.Perf;

/// <summary>Exact allocation / GC-count assertions are only deterministic when nothing else runs; keep them all in <see cref="PerfCollection"/>.</summary>
public class PerfCollectionGuardTests
{
    static readonly string[] Exempt = ["AllocationMeasure.cs", "PerfCollectionGuardTests.cs"];

    static IEnumerable<string> TestSources() =>
        Directory.EnumerateFiles(Path.Combine(Fixtures.RepoRoot, "tests", "FuelPace.Tests"), "*.cs", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                     && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                     && !Exempt.Contains(Path.GetFileName(p)));

    [Fact]
    public void Tests_measuring_allocations_use_the_shared_helper()
    {
        var raw = new Regex(@"GC\.(GetAllocatedBytesForCurrentThread|GetTotalAllocatedBytes|CollectionCount)");
        var offenders = TestSources().Where(p => raw.IsMatch(File.ReadAllText(p))).Select(Path.GetFileName).ToArray();
        Assert.True(offenders.Length == 0, "use AllocationMeasure instead of raw GC calls in: " + string.Join(", ", offenders));
    }

    [Fact]
    public void Every_test_class_using_AllocationMeasure_is_in_the_PerfCollection()
    {
        var users = TestSources().Select(p => (Path: p, Text: File.ReadAllText(p)))
            .Where(x => x.Text.Contains("AllocationMeasure.")).ToArray();
        Assert.NotEmpty(users); // the guard itself must see the known users
        var bad = new List<string>();
        foreach (var (path, text) in users)
        {
            int classes = Regex.Count(text, @"^public (sealed )?class \w+", RegexOptions.Multiline);
            int inCollection = Regex.Count(text,
                @"^\[Collection\([^\]\r\n]*PerfCollection[^\]\r\n]*\]\s*public (sealed )?class \w+", RegexOptions.Multiline);
            if (classes != inCollection) bad.Add($"{Path.GetFileName(path)} ({inCollection}/{classes} public classes in the collection)");
        }
        Assert.True(bad.Count == 0, "missing [Collection(PerfCollection.Name)]: " + string.Join(", ", bad));
    }
}
