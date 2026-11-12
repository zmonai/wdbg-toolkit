using System.Diagnostics;
using System.IO;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text.Json;

namespace WdbgToolkit.App;

public sealed record McpServiceSettings(string EntryPoint, string NodePath, int Port, string Token)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(EntryPoint) || !Path.IsPathFullyQualified(EntryPoint) ||
            string.IsNullOrWhiteSpace(NodePath) || !Path.IsPathFullyQualified(NodePath) ||
            Port is < 1 or > 65535 || string.IsNullOrEmpty(Token) ||
            Token.Length < 32 || Token.Any(character => char.IsWhiteSpace(character) || char.IsControl(character)))
        {
            throw new InvalidOperationException("The MCP service configuration is invalid. Remove and reinstall the service.");
        }
    }
}

public static class McpServiceManager
{
    public const string ServiceName = "WdbgToolkitMcp";
    public static string SettingsDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "WdbgToolkit.McpService");
    private static string SettingsPath => Path.Combine(SettingsDirectory, "settings.json");

    public static bool IsAdministrator
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    public static ServiceControllerStatus? GetStatus()
    {
        var services = ServiceController.GetServices();
        try
        {
            return services.FirstOrDefault(item => item.ServiceName == ServiceName)?.Status;
        }
        finally
        {
            foreach (var service in services)
            {
                service.Dispose();
            }
        }
    }

    public static McpServiceSettings LoadSettings()
    {
        var settings = JsonSerializer.Deserialize<McpServiceSettings>(File.ReadAllText(SettingsPath)) ??
            throw new InvalidOperationException("The MCP service configuration is empty. Remove and reinstall the service.");
        settings.Validate();
        return settings;
    }

    public static async Task InstallAndStartAsync(string? entryPoint)
    {
        RequireAdministrator();
        if (GetStatus() is not null)
        {
            await StartAsync();
            return;
        }

        if (entryPoint is null)
        {
            throw new InvalidOperationException("The bundled MCP server was not found. Repair the toolkit MSI.");
        }
        var executable = Environment.ProcessPath ??
            throw new InvalidOperationException("Could not locate the toolkit executable.");
        var node = FindMachineNode();
        // LocalSystem must never execute a user-writable development build or per-user Node installation.
        RequireProgramFilesPath(executable);
        RequireProgramFilesPath(entryPoint);
        RequireProgramFilesPath(node);
        if (!string.Equals(Path.GetFileName(executable), "WdbgToolkit.App.exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Install the toolkit MSI before enabling the MCP service.");
        }

        var security = new DirectorySecurity();
        security.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
        {
            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(sid, null), FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None, AccessControlType.Allow));
        }
        if (Directory.Exists(SettingsDirectory))
        {
            throw new InvalidOperationException(
                $"Service data already exists at {SettingsDirectory}. Remove the old service data as administrator before installing.");
        }
        FileSystemAclExtensions.CreateDirectory(security, SettingsDirectory);
        var settings = new McpServiceSettings(Path.GetFullPath(entryPoint), node,
            McpServerManager.DefaultPort, Convert.ToHexString(RandomNumberGenerator.GetBytes(32)));
        var created = false;
        try
        {
            await File.WriteAllTextAsync(SettingsPath, JsonSerializer.Serialize(settings));
            await RunScAsync("create", ServiceName, "binPath=", BuildServiceCommand(executable),
                "start=", "auto", "obj=", "LocalSystem", "DisplayName=", "Windows Debug Toolkit MCP");
            created = true;
            await RunScAsync("description", ServiceName,
                "Authenticated MCP command execution as LocalSystem. Starts automatically after reboot.");
            await RunScAsync("failure", ServiceName, "reset=", "86400", "actions=", "restart/5000/restart/15000/restart/60000");
        }
        catch
        {
            if (!created)
            {
                File.Delete(SettingsPath);
                Directory.Delete(SettingsDirectory);
            }
            throw;
        }

        await StartAsync();
    }

    public static string BuildServiceCommand(string executable)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        if (!Path.IsPathFullyQualified(executable) || executable.Contains('"') ||
            executable.Any(char.IsControl))
        {
            throw new ArgumentException("The service executable must be an absolute path without quotes or control characters.", nameof(executable));
        }
        return $"\"{executable}\" --mcp-service";
    }

    private static void RequireProgramFilesPath(string path)
    {
        var root = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)) +
            Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(path).StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
        {
            throw new InvalidOperationException(
                "Service mode requires the toolkit, bundled MCP server, and Node.js installed machine-wide under Program Files. Install the toolkit MSI and machine-wide Node.js first.");
        }
    }

    private static string FindMachineNode()
    {
        var node = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs", "node.exe");
        if (!File.Exists(node))
        {
            throw new InvalidOperationException("Install machine-wide Node.js under Program Files\\nodejs before enabling the service.");
        }
        return node;
    }

    public static async Task StartAsync()
    {
        RequireAdministrator();
        await Task.Run(() =>
        {
            using var service = new ServiceController(ServiceName);
            if (service.Status == ServiceControllerStatus.Stopped)
            {
                service.Start();
            }
            service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
        });
        var settings = LoadSettings();
        await McpServerManager.VerifyEndpointAsync(settings.Port, settings.Token);
    }

    public static async Task StopAsync()
    {
        RequireAdministrator();
        await Task.Run(() =>
        {
            using var service = new ServiceController(ServiceName);
            if (service.Status != ServiceControllerStatus.Stopped)
            {
                if (service.Status != ServiceControllerStatus.StopPending)
                {
                    service.Stop();
                }
                service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
            }
        });
    }

    public static async Task RemoveAsync()
    {
        RequireAdministrator();
        await StopAsync();
        await RunScAsync("delete", ServiceName);
        File.Delete(SettingsPath);
        if (Directory.Exists(SettingsDirectory))
        {
            Directory.Delete(SettingsDirectory);
        }
    }

    private static void RequireAdministrator()
    {
        if (!IsAdministrator)
        {
            throw new InvalidOperationException("Run the toolkit as administrator to manage the MCP service.");
        }
    }

    private static async Task RunScAsync(params string[] args)
    {
        var info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "sc.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var arg in args)
        {
            info.ArgumentList.Add(arg);
        }
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Could not launch the service controller.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var output = await stdout;
        var error = await stderr;
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"Service operation failed ({process.ExitCode}): {output} {error}");
        }
    }
}
