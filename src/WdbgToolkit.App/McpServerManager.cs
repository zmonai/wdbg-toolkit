using System.Diagnostics;
using System.IO;

namespace WdbgToolkit.App;

/// <summary>
/// Launches and tracks the local <c>wdbgmcp</c> Node.js MCP server process, and builds
/// the copyable connection details an MCP client (e.g. an LLM assistant) needs to spawn
/// its own instance over stdio.
/// </summary>
public sealed class McpServerManager : IDisposable
{
    /// <summary>
    /// Environment variable the launched server reads to locate its workflow-run root.
    /// Matches <c>mcp/wdbgmcp/src/workflowStore.ts</c>'s <c>WDBGMCP_ROOT</c> override.
    /// </summary>
    public const string RunRootEnvironmentVariable = "WDBGMCP_ROOT";

    private Process? _process;

    public bool IsRunning => _process is { HasExited: false };

    public int? ProcessId => IsRunning ? _process!.Id : null;

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

    public void Start(string entryPointPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entryPointPath);

        if (IsRunning)
        {
            return;
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

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.Exited += OnProcessExited;
        process.Start();
        _process = process;
    }

    public void Stop()
    {
        if (_process is null)
        {
            return;
        }

        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(5000);
            }
        }
        catch (InvalidOperationException)
        {
            // Process already exited between the HasExited check and Kill.
        }
        finally
        {
            _process.Exited -= OnProcessExited;
            _process.Dispose();
            _process = null;
        }
    }

    /// <summary>
    /// Builds the connection details an MCP client config (e.g. Claude Desktop's
    /// <c>claude_desktop_config.json</c> or VS Code's <c>mcp.json</c>) needs to launch
    /// its own <c>wdbgmcp</c> instance over stdio.
    /// </summary>
    public static string BuildConnectionDetails(string entryPointPath) =>
        $$"""
        {
          "mcpServers": {
            "wdbgmcp": {
              "command": "node",
              "args": ["{{entryPointPath.Replace("\\", "\\\\")}}"],
              "env": {
                "{{RunRootEnvironmentVariable}}": "{{DefaultRunRootDirectory.Replace("\\", "\\\\")}}"
              }
            }
          }
        }
        """;

    private void OnProcessExited(object? sender, EventArgs e) => Exited?.Invoke(this, EventArgs.Empty);

    public void Dispose() => Stop();
}
