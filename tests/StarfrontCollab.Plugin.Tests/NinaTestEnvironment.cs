using System.IO;
using System.Runtime.CompilerServices;
using NINA.Core.Utility;

namespace StarfrontCollab.Plugin.Tests;

internal static class NinaTestEnvironment
{
    // Set this before NINA's logger or plugin base can touch the real data folder.
    [ModuleInitializer]
    internal static void Initialize()
    {
        var root = Path.Combine(Path.GetTempPath(), "starfront-plugin-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        CoreUtil.APPLICATIONTEMPPATH = root;
    }
}
