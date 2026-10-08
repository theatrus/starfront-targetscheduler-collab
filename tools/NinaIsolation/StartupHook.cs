using System.Reflection;

// Test-only .NET startup hook: points N.I.N.A.'s data folder (profiles,
// plugins, logs and Target Scheduler's database) at a throwaway directory so a
// smoke test never touches the real installation. Never package this assembly.
internal static class StartupHook
{
    public static void Initialize()
    {
        var root = Environment.GetEnvironmentVariable("STARFRONT_COLLAB_NINA_ROOT");
        var token = Environment.GetEnvironmentVariable("STARFRONT_COLLAB_NINA_TOKEN");
        if (string.IsNullOrEmpty(root) || !Path.IsPathFullyQualified(root) || !Guid.TryParseExact(token, "N", out _))
            throw new InvalidOperationException("Missing isolated N.I.N.A. test directory or token.");
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (!Path.GetFileName(root).StartsWith("nina-smoke-", StringComparison.Ordinal)
            || (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0
            || File.ReadAllText(Path.Combine(root, ".collab-test-root")) != token)
            throw new InvalidOperationException("N.I.N.A. test directory ownership check failed.");

        var field = Assembly.Load("NINA.Core").GetType("NINA.Core.Utility.CoreUtil", throwOnError: true)!
            .GetField("APPLICATIONTEMPPATH", BindingFlags.Public | BindingFlags.Static)
            ?? throw new MissingFieldException("N.I.N.A.'s data folder contract changed.");
        field.SetValue(null, root);
        File.WriteAllText(Path.Combine(root, "isolation-ready.txt"), (string)field.GetValue(null)!);
        Environment.SetEnvironmentVariable("DOTNET_STARTUP_HOOKS", null);
    }
}
