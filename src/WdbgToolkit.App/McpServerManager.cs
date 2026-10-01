using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace WdbgToolkit.App;

/// <summary>
/// Launches and tracks the local <c>wdbgmcp</c> Node.js MCP server process, and builds
/// the copyable connection details an MCP client (e.g. an LLM assistant) needs to reach
/// its HTTP (Streamable HTTP) endpoint over the network.
/// </summary>
public sealed class McpServerManager : IDisposable
{
    /// <summary>
    /// Environment variable the launched server reads to locate its workflow-run root.
    /// Matches <c>mcp/wdbgmcp/src/workflowStore.ts</c>'s <c>WDBGMCP_ROOT</c> override.
    /// </summary>
    public const string RunRootEnvironmentVariable = "WDBGMCP_ROOT";

    /// <summary>
    /// Environment variable the launched server reads for its listen port.
    /// Matches <c>mcp/wdbgmcp/src/index.ts</c>'s <c>WDBGMCP_PORT</c> override.
    /// </summary>
    public const string PortEnvironmentVariable = "WDBGMCP_PORT";

    /// <summary>
    /// Environment variable selecting the server's transport ("http" or "stdio").
    /// Matches <c>mcp/wdbgmcp/src/index.ts</c>'s <c>WDBGMCP_TRANSPORT</c> override.
    /// </summary>
    public const string TransportEnvironmentVariable = "WDBGMCP_TRANSPORT";

    /// <summary>
    /// Default TCP port the server listens on. Must match the Node side's default
    /// (<c>DEFAULT_PORT</c> in <c>mcp/wdbgmcp/src/index.ts</c>).
    /// </summary>
    public const int DefaultPort = 7890;

    /// <summary>HTTP path MCP clients should POST/GET/DELETE against.</summary>
    public const string McpPath = "/mcp";

    /// <summary>
    /// How long to wait after launching before checking whether the process exited
    /// immediately (e.g. because Node.js is missing or the server failed to start).
    /// </summary>
    private static readonly TimeSpan StartupCheckDelay = TimeSpan.FromMilliseconds(600);

    private readonly object _stderrLock = new();
    private readonly StringBuilder _recentStderr = new();
    private Process? _process;

    public bool IsRunning => _process is { HasExited: false };

    public int? ProcessId => IsRunning ? _process!.Id : null;

    public int Port { get; private set; } = DefaultPort;

    public event EventHandler? Exited;

    /// <summary>
    /// Finds <c>mcp/wdbgmcp/dist/index.js</c> by walking up from the app's base directory
    /// (the dev layout: repo-root/src/WdbgToolkit.App/bin/...), then falling back to a
    /// copy placed alongside the app itself (an installed layout can bundle it there).
    /// Overridable via the <c>WDBGTOOLKIT_MCP_ENTRY_POINT</c> environment variable.
    /// </summary>
    public static string? ResolveServerEntryPoint()
    {
        var overridePath = Environment.GetEnvironmentVariable("WDBGTOOLKIT_MCP_ENTRY_POINT");
        if (!string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath))
        {
            return Path.GetFullPath(overridePath);
        }

        const string relativeEntryPoint = "mcp\\wdbgmcp\\dist\\index.js";

        var bundled = Path.Combine(AppContext.BaseDirectory, relativeEntryPoint);
        if (File.Exists(bundled))
        {
            return Path.GetFullPath(bundled);
        }

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && directory is not null; i++, directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, relativeEntryPoint);
            if (File.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        return null;
    }

    public static string DefaultRunRootDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "WdbgToolkit");

    public async Task StartAsync(string entryPointPath) => await StartAsync(entryPointPath, DefaultPort).ConfigureAwait(true);

    /// <summary>
    /// Starts the server on a background thread (launching a process, and especially the
    /// first launch of an unfamiliar executable, can block for seconds on antivirus/
    /// SmartScreen scanning, so this must never run on the UI thread). After launch, waits
    /// briefly to detect an immediate failure (e.g. Node.js missing, or a module error) and
    /// throws with the captured stderr output so the caller can show a clear reason instead
    /// of a silent "not running" state.
    /// </summary>
    public async Task StartAsync(string entryPointPath, int port)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entryPointPath);

        if (IsRunning)
        {
            return;
        }

        lock (_stderrLock)
        {
            _recentStderr.Clear();
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = "node",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add(entryPointPath);
        startInfo.Environment[RunRootEnvironmentVariable] = DefaultRunRootDirectory;
        startInfo.Environment[TransportEnvironmentVariable] = "http";
        startInfo.Environment[PortEnvironmentVariable] = port.ToString();

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.Exited += OnProcessExited;
        process.ErrorDataReceived += OnErrorDataReceived;

        try
        {
            await Task.Run(() =>
            {
                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
            }).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            process.Exited -= OnProcessExited;
            process.ErrorDataReceived -= OnErrorDataReceived;
            process.Dispose();
            throw new InvalidOperationException(
                $"Could not launch node. Make sure Node.js is installed and on PATH. ({exception.Message})",
                exception);
        }

        _process = process;
        Port = port;

        // Give the process a moment to fail fast (missing module, bad entry point, etc.)
        // before reporting success, so the UI doesn't claim "running" for a process that
        // is already gone.
        await Task.Delay(StartupCheckDelay).ConfigureAwait(true);

        if (process.HasExited)
        {
            var exitCode = process.ExitCode;
            var errorOutput = GetRecentStderr();
            _process = null;
            process.Exited -= OnProcessExited;
            process.ErrorDataReceived -= OnErrorDataReceived;
            process.Dispose();

            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(errorOutput)
                    ? $"wdbgmcp exited immediately (exit code {exitCode})."
                    : $"wdbgmcp exited immediately (exit code {exitCode}): {errorOutput}");
        }
    }

    public async Task StopAsync()
    {
        var process = _process;
        if (process is null)
        {
            return;
        }

        try
        {
            await Task.Run(() =>
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(5000);
                }
            }).ConfigureAwait(true);
        }
        catch (InvalidOperationException)
        {
            // Process already exited between the HasExited check and Kill.
        }
        finally
        {
            process.Exited -= OnProcessExited;
            process.ErrorDataReceived -= OnErrorDataReceived;
            process.Dispose();
            _process = null;
        }
    }

    /// <summary>
    /// Substrings in an adapter's description/name that indicate a virtual adapter
    /// (hypervisor host-only networks, VPN clients, etc.) that's usually not reachable
    /// from other machines on the LAN, so real physical adapters are preferred over them.
    /// </summary>
    private static readonly string[] VirtualAdapterMarkers =
    [
        "virtual", "vmware", "virtualbox", "hyper-v", "vethernet", "loopback", "tap-", "tunnel", "wsl",
    ];

    /// <summary>
    /// Finds a non-loopback IPv4 address for this machine so remote MCP clients (e.g. VS
    /// Code or an LLM tool running on another machine) can reach the HTTP endpoint by IP.
    /// Prefers physical adapters (Ethernet/Wi-Fi) over virtual ones (VirtualBox/VMware/
    /// Hyper-V host-only networks), since those are rarely reachable from other machines.
    /// Falls back to "localhost" if no suitable network address can be found (e.g. no
    /// active network adapters), which still works for same-machine clients.
    /// </summary>
    public static string ResolveMachineAddress()
    {
        try
        {
            string? fallbackAddress = null;

            foreach (var networkInterface in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (networkInterface.OperationalStatus != OperationalStatus.Up ||
                    networkInterface.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                {
                    continue;
                }

                var isVirtual = VirtualAdapterMarkers.Any(marker =>
                    networkInterface.Description.Contains(marker, StringComparison.OrdinalIgnoreCase) ||
                    networkInterface.Name.Contains(marker, StringComparison.OrdinalIgnoreCase));

                foreach (var unicast in networkInterface.GetIPProperties().UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily != AddressFamily.InterNetwork ||
                        IPAddress.IsLoopback(unicast.Address))
                    {
                        continue;
                    }

                    if (!isVirtual)
                    {
                        // Physical adapter: use it immediately.
                        return unicast.Address.ToString();
                    }

                    // Virtual adapter: remember it in case no physical adapter is found.
                    fallbackAddress ??= unicast.Address.ToString();
                }
            }

            if (fallbackAddress is not null)
            {
                return fallbackAddress;
            }
        }
        catch (NetworkInformationException)
        {
            // Fall through to localhost below.
        }

        return "localhost";
    }

    /// <summary>
    /// Builds the copyable config snippets an MCP client needs to reach this machine's
    /// <c>wdbgmcp</c> HTTP endpoint over the network. VS Code's <c>mcp.json</c> schema
    /// (top-level <c>servers</c> key, with a required <c>type</c> field) differs from the
    /// <c>mcpServers</c> schema used by Claude Desktop, Cursor, and most other MCP
    /// clients, so both are included, each ready to paste as-is.
    /// </summary>
    public static string BuildConnectionDetails(int port)
    {
        var url = $"http://{ResolveMachineAddress()}:{port}{McpPath}";

        return $$"""
            VS Code (and other editors using the MCP "mcp.json" schema)
            Add to your user settings "mcp.json" (Ctrl+Shift+P -> "MCP: Open User Configuration"),
            or to .vscode/mcp.json in this workspace:

            {
              "servers": {
                "wdbgmcp": {
                  "type": "http",
                  "url": "{{url}}"
                }
              }
            }

            Claude Desktop, Cursor, and other clients using "mcpServers"
            Add to claude_desktop_config.json / mcp.json:

            {
              "mcpServers": {
                "wdbgmcp": {
                  "type": "http",
                  "url": "{{url}}"
                }
              }
            }

            Note: the Windows Debug Toolkit app must be running with the MCP server started
            (see the buttons above) for this URL to be reachable. The machine's firewall must
            allow inbound connections on port {{port}} for a remote client to connect.
            """;
    }

    private string GetRecentStderr()
    {
        lock (_stderrLock)
        {
            return _recentStderr.ToString().Trim();
        }
    }

    private void OnErrorDataReceived(object sender, DataReceivedEventArgs e)
    {
        if (string.IsNullOrEmpty(e.Data))
        {
            return;
        }

        lock (_stderrLock)
        {
            // Cap the buffer so a chatty server can't grow this unbounded.
            if (_recentStderr.Length > 4000)
            {
                _recentStderr.Remove(0, _recentStderr.Length - 4000);
            }

            _recentStderr.AppendLine(e.Data);
        }
    }

    private void OnProcessExited(object? sender, EventArgs e) => Exited?.Invoke(this, EventArgs.Empty);

    public void Dispose()
    {
        var process = _process;
        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // Process already exited.
        }
        finally
        {
            process.Exited -= OnProcessExited;
            process.ErrorDataReceived -= OnErrorDataReceived;
            process.Dispose();
            _process = null;
        }
    }
}
