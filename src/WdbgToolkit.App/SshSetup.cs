using System.IO;
using System.Text;
using System.Text.Json;
using WdbgToolkit.PackageManagement;

namespace WdbgToolkit.App;

public static class SshSetup
{
    public static Task<CommandResult> RunAsync(
        ICommandRunner runner, string action, string[] allowedClients, bool confirmed,
        CancellationToken cancellationToken = default)
    {
        var script = Path.Combine(AppContext.BaseDirectory, "mcp", "wdbgmcp", "scripts", "Configure-Ssh.ps1");
        if (!File.Exists(script))
        {
            throw new InvalidOperationException("SSH setup script was not found. Repair the toolkit installation.");
        }
        var configuration = Convert.ToBase64String(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(new { action, allowedClients, confirmed })));
        return runner.RunAsync(
            Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
            ["-NoProfile", "-NonInteractive", "-File", script, "-ConfigBase64", configuration],
            cancellationToken);
    }
}
