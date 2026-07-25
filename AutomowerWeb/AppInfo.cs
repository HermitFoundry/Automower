using System.Reflection;

namespace AutomowerWeb;

// Build-time facts baked into the assembly - MinVer's computed version
// (AutomowerWeb.csproj's MinVerTagPrefix + the 'v0.9.0' git tag) and the
// UTC build date (AutomowerWeb.csproj's BuildDateUtc property). Read back
// via reflection rather than passed some other way so there's exactly one
// source of truth (the compiled assembly itself) for "what is this
// deployment, and when was it built" - useful for confirming what's
// actually running on the container versus what's running locally.
public static class AppInfo
{
    public static string Version { get; } = ComputeVersion();

    public static string BuildDate { get; } = ComputeBuildDate();

    // Sourced from AutomowerWeb.csproj's <Copyright> property (MSBuild
    // writes it into AssemblyCopyrightAttribute) - one source of truth
    // instead of the holder's name being duplicated in the .csproj and
    // hardcoded again in a Razor component.
    public static string Copyright { get; } = Assembly.GetExecutingAssembly()
        .GetCustomAttribute<AssemblyCopyrightAttribute>()?.Copyright ?? "";

    private static string ComputeVersion()
    {
        var informational = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrEmpty(informational)) return "0.0.0";

        // Drop MinVer's '+<sha>' build-metadata suffix for a clean display
        // version - the full value (with sha) is still in the assembly
        // metadata if it's ever needed for exact-build diagnosis.
        var plusIndex = informational.IndexOf('+');
        return plusIndex >= 0 ? informational[..plusIndex] : informational;
    }

    private static string ComputeBuildDate()
    {
        var metadata = Assembly.GetExecutingAssembly().GetCustomAttributes<AssemblyMetadataAttribute>();
        return metadata.FirstOrDefault(m => m.Key == "BuildDate")?.Value ?? "unknown";
    }
}
