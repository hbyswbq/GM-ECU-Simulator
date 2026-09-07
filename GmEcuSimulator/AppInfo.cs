using System.Reflection;

namespace GmEcuSimulator;

// Single source of truth for the app's identity strings shown in the UI
// (About menu / About dialog). The version is NOT hard-coded here: it is read
// from the AssemblyInformationalVersion attribute, which GmEcuSimulator.csproj
// stamps at build time from `git describe --tags` (the StampVersionFromGit
// target). Because every GitHub release is a git tag (v0.4.2, ...), the value
// surfaced here is always the release the binary was built from:
//   - clean checkout on a tag -> "0.4.2"
//   - N commits past the tag  -> "0.4.2-3-gabc1234"  (a dev build)
//   - working tree dirty      -> "...-dirty"
// A release bundle produced by Publish-Release.ps1 is stamped with exactly its
// tag, so a downloaded release reports e.g. "0.4.2" with no suffix.
internal static class AppInfo
{
    public const string ProductName = "GM ECU 模拟器";

    // GitHub project + releases page. Used by the About dialog's "View
    // releases" action so the user can jump straight to the page the version
    // string is keyed to.
    public const string RepositoryUrl   = "https://github.com/hjtrbo/GM-ECU-Simulator";
    public const string ReleasesUrl     = "https://github.com/hjtrbo/GM-ECU-Simulator/releases";

    // The build-stamped version, e.g. "0.4.2" or "0.4.2-3-gabc1234".
    public static string Version { get; } = ResolveVersion();

    // "Version 0.4.2" - the label shown inline on the About menu.
    public static string VersionDisplay => $"版本 {Version}";

    private static string ResolveVersion()
    {
        var asm = Assembly.GetExecutingAssembly();

        // InformationalVersion carries the full git-describe string. Prefer it
        // over AssemblyVersion (which is numeric-only and drops the ahead-count
        // / hash suffix).
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(info))
        {
            // The .NET SDK can append "+<commit-sha>" to InformationalVersion
            // when source-revision embedding is on. The csproj disables that,
            // but strip it defensively so the UI never shows a 40-char hash.
            int plus = info.IndexOf('+');
            if (plus >= 0) info = info[..plus];
            return info.Trim();
        }

        // Fallback: numeric AssemblyVersion (git-less build with no attribute).
        return asm.GetName().Version?.ToString() ?? "unknown";
    }
}
