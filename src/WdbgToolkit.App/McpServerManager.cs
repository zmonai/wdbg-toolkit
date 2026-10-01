using System.Diagnostics;
using System.IO;
using System.Text;

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

    /// <summary>
    /// Starts the server on a background thread (launching a process, and especially the
    /// first launch of an unfamiliar executable, can block for seconds on antivirus/
    /// SmartScreen scanning, so this must never run on the UI thread). After launch, waits
    /// briefly to detect an immediate failure (e.g. Node.js missing, or a module error) and
    /// throws with the captured stderr output so the caller can show a clear reason instead
    /// of a silent "not running" state.
    /// </summary>
    public async Task StartAsync(string entryPointPath)
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
    /// Builds the copyable config snippets an MCP client needs to launch its own
    /// <c>wdbgmcp</c> instance over stdio. VS Code's <c>mcp.json</c> schema (top-level
    /// <c>servers</c> key, with a required <c>type</c> field) differs from the
    /// <c>mcpServers</c> schema used by Claude Desktop, Cursor, and most other MCP
    /// clients, so both are included, each ready to paste as-is.
    /// </summary>
    public static string BuildConnectionDetails(string entryPointPath)
    {
        var escapedEntryPoint = entryPointPath.Replace("\\", "\\\\");
        var escapedRunRoot = DefaultRunRootDirectory.Replace("\\", "\\\\");

        return $$"""
            VS Code (and other editors using the MCP "mcp.json" schema)
            Add to your user settings "mcp.json" (Ctrl+Shift+P -> "MCP: Open User Configuration"),
            or to .vscode/mcp.json in this workspace:

            {
              "servers": {
                "wdbgmcp": {
                  "type": "stdio",
                  "command": "node",
                  "args": ["{{escapedEntryPoint}}"],
                  "env": {
                    "{{RunRootEnvironmentVariable}}": "{{escapedRunRoot}}"
                  }
                }
              }
            }

            Claude Desktop, Cursor, and other clients using "mcpServers"
            Add to claude_desktop_config.json / mcp.json:

            {
              "mcpServers": {
                "wdbgmcp": {
                  "command": "node",
                  "args": ["{{escapedEntryPoint}}"],
                  "env": {
                    "{{RunRootEnvironmentVariable}}": "{{escapedRunRoot}}"
                  }
                }
              }
            }
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
