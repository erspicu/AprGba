namespace AprCpu.Tests;

/// <summary>
/// Locates the repo's spec/ directory from the test binary location.
/// Tests are run from src/AprCpu.Tests/bin/Debug/net10.0/, so the spec
/// folder sits four levels up.
/// </summary>
internal static class TestPaths
{
    public static string RepoRoot { get; } = ResolveRepoRoot();
    public static string SpecRoot     => Path.Combine(RepoRoot, "spec");
    public static string TempRoot     => Path.Combine(RepoRoot, "temp");
    public static string TestRomsRoot => Path.Combine(RepoRoot, "test-roms");

    private static string ResolveRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8 && dir is not null; i++)
        {
            if (File.Exists(Path.Combine(dir, "AprGba.slnx"))) return dir;
            dir = Path.GetDirectoryName(dir);
        }
        throw new InvalidOperationException(
            $"Could not locate repo root (looking for AprGba.slnx) starting at {AppContext.BaseDirectory}");
    }
}
