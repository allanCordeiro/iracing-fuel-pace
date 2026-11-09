using System.Text;

namespace FuelPace.Replay;

public static class Program
{
    public static int Main(string[] args)
    {
        // The Windows console does not default to UTF-8; without this "✓ — ·" come out corrupted.
        Console.OutputEncoding = Encoding.UTF8;
        return ReplayCli.Run(args, Console.Out);
    }
}
