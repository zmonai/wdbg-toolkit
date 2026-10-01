namespace WdbgToolkit.PackageManagement;

public sealed record ToolPackage(
    string Id,
    string Name,
    string? WinGetPackageId,
    string? ChocolateyPackageId);

public static class ToolPackageCatalog
{
    private static readonly IReadOnlyList<ToolPackage> Packages =
        Array.AsReadOnly<ToolPackage>(
        [
            new(
                "sysinternals-suite",
                "Sysinternals Suite",
                "Microsoft.Sysinternals.Suite",
                "sysinternals"),
            new(
                "windbg",
                "WinDbg",
                "Microsoft.WinDbg",
                "windbg"),
            new(
                "procdump",
                "ProcDump",
                null,
                "procdump"),
            new(
                "wireshark",
                "Wireshark",
                "WiresharkFoundation.Wireshark",
                "wireshark"),
            new(
                "npcap",
                "Npcap",
                null,
                "npcap"),
            new(
                "windows-performance-toolkit",
                "Windows Performance Toolkit (Windows ADK package)",
                "Microsoft.WindowsADK",
                "windows-adk-all"),
            new(
                "nodejs",
                "Node.js",
                "OpenJS.NodeJS",
                "nodejs")
        ]);

    public static IReadOnlyList<ToolPackage> All => Packages;

    public static ToolPackage GetById(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        return Packages.FirstOrDefault(
                   package => string.Equals(package.Id, id, StringComparison.OrdinalIgnoreCase))
               ?? throw new KeyNotFoundException($"No diagnostic tool package is registered with id '{id}'.");
    }
}
