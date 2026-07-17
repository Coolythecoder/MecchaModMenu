using System.Text;

namespace MecchaCamouflage.Core;

public sealed class AppPaths
{
    public AppPaths(string version)
    {
        Version = SanitizeVersion(version);
        // Prefer the process environment so isolated test and portable launch
        // contexts can redirect app data without touching the signed-in user's
        // real profile. Normal Windows launches resolve this to the same folder.
        var local = Environment.GetEnvironmentVariable("LOCALAPPDATA");
        if (string.IsNullOrWhiteSpace(local))
            local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(local))
            local = Path.GetTempPath();
        RootDirectory = Path.Combine(local, "MecchaCamouflage");
        VersionsDirectory = Path.Combine(RootDirectory, "versions");
        VersionRoot = Path.Combine(VersionsDirectory, Version);
        ConfigDirectory = Path.Combine(VersionRoot, "config");
        ConfigPath = Path.Combine(ConfigDirectory, "config.json");
        LogDirectory = Path.Combine(VersionRoot, "logs");
        RuntimeDirectory = Path.Combine(VersionRoot, "runtime");
        ModuleHostDirectory = Path.Combine(RuntimeDirectory, "module-hosts");
        ModulesDirectory = Path.Combine(RootDirectory, "modules");
        ModuleDataDirectory = Path.Combine(RootDirectory, "module-data");
        BridgeInstancesDirectory = Path.Combine(RootDirectory, "bridge-instances");
        BridgeStateDirectory = Path.Combine(RootDirectory, "bridge-state");
        BridgeProgressDirectory = Path.Combine(BridgeStateDirectory, "progress");
        DebugDirectory = Path.Combine(VersionRoot, "debug");
        DiagnosticsDirectory = Path.Combine(VersionRoot, "diagnostics");
    }

    public string Version { get; }
    public string RootDirectory { get; }
    public string VersionsDirectory { get; }
    public string VersionRoot { get; }
    public string ConfigDirectory { get; }
    public string ConfigPath { get; }
    public string LogDirectory { get; }
    public string RuntimeDirectory { get; }
    public string ModuleHostDirectory { get; }
    public string ModulesDirectory { get; }
    public string ModuleDataDirectory { get; }
    public string BridgeInstancesDirectory { get; }
    public string BridgeStateDirectory { get; }
    public string BridgeProgressDirectory { get; }
    public string DebugDirectory { get; }
    public string DiagnosticsDirectory { get; }

    public void EnsureBaseDirectories()
    {
        Directory.CreateDirectory(ConfigDirectory);
        Directory.CreateDirectory(LogDirectory);
        Directory.CreateDirectory(ModuleHostDirectory);
        Directory.CreateDirectory(ModulesDirectory);
        Directory.CreateDirectory(ModuleDataDirectory);
        Directory.CreateDirectory(BridgeInstancesDirectory);
        Directory.CreateDirectory(BridgeProgressDirectory);
        Directory.CreateDirectory(DiagnosticsDirectory);
    }

    public static string SanitizeVersion(string? version)
    {
        var input = string.IsNullOrWhiteSpace(version) ? "unversioned" : version.Trim();
        var builder = new StringBuilder(input.Length);
        foreach (var ch in input)
        {
            builder.Append(char.IsLetterOrDigit(ch) || ch is '.' or '_' or '-' ? ch : '_');
        }
        return builder.Length == 0 ? "unversioned" : builder.ToString();
    }

}
