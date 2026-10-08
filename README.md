# Windows Debug Toolkit

A Windows desktop toolkit for guided crash, performance, and networking diagnostics.

> **Current status:** The app can detect WinGet or Chocolatey and offers scenario-specific prerequisite installation. Crash setup includes registering ProcDump for full-memory postmortem dumps after explicit confirmation. A WiX project builds an MSI for the desktop app. A separate TypeScript MCP server (`wdbgmcp`) lets authenticated AI clients execute commands on the debugging machine and analyze their output.

> [!WARNING]
> **The MCP server enables remote code execution. Do not use it on high-value,
> production, sensitive, or critical systems.** Any client with the bearer token
> can run commands with the server process's permissions; this is not a sandbox.
> Run it only on a dedicated debugging machine you are authorized to control,
> preferably as a standard user. Restrict network access and never expose its
> unencrypted HTTP endpoint directly to the public internet. See the
> [MCP security and usage guide](mcp/wdbgmcp/README.md) before enabling it.

## Projects

- `src/WdbgToolkit.App` — WPF desktop application.
- `src/WdbgToolkit.App/Assets/Wdt.ico` — multi-resolution WDT icon used by the executable, window/taskbar, and MSI shortcut.
- `src/WdbgToolkit.Core` — UI-independent diagnostic scenario contracts and catalog.
- `src/WdbgToolkit.PackageManagement` — package-manager detection and explicit tool installation via WinGet or Chocolatey.
- `installer/WdbgToolkit.Installer` — WiX Toolset 6 MSI project for the desktop app.
- `tests/WdbgToolkit.Core.Tests` — core and package-management tests.
- `mcp/wdbgmcp` — separate TypeScript MCP server for AI-driven remote command execution. See [`mcp/wdbgmcp/README.md`](mcp/wdbgmcp/README.md).

## Requirements

- Windows with the .NET 10 SDK to build and run the WPF application.
- A supported Windows client version with the matching .NET 10 Desktop Runtime to run a framework-dependent build.

The supported Windows-version matrix and tool compatibility still need to be established before diagnostic workflows are added.

## Build and run

```powershell
dotnet test WdbgToolkit.sln
dotnet run --project src\WdbgToolkit.App\WdbgToolkit.App.csproj
dotnet build installer\WdbgToolkit.Installer\WdbgToolkit.Installer.wixproj -c Release
```

The app displays diagnostic scenarios and host OS/.NET information and installs listed prerequisites after explicit confirmation.

The MSI is generated at `installer\WdbgToolkit.Installer\bin\Release\WdbgToolkit.Installer.msi`. It installs the desktop app under Program Files and creates an all-users Start Menu shortcut. It also ships `mcp\wdbgmcp\dist\index.js`, bundled with its JavaScript dependencies, and its package metadata. No npm install is needed on the target machine. Building the MSI requires Node.js/npm and downloads locked build dependencies using `npm ci`. The app is currently framework-dependent, so installing or running it requires the matching .NET 10 Windows Desktop Runtime. Node.js is still required to run the MCP server and can be installed through the MCP Server prerequisites or Debug Machine Setup. The MSI does not install .NET, Node.js, WinGet, Chocolatey, or any diagnostic tools. Update the MSI `Package` version when releasing a new version; the stable upgrade code enables major upgrades.

## Package management library

The left-hand **Package Manager Setup** section installs Chocolatey through WinGet
using the exact `Chocolatey.Chocolatey` package from the `winget` source. Run the
toolkit as administrator; installation requires explicit confirmation and accepts
the package/source agreements. The section reports installation failures and
refreshes package-manager detection afterward. It is disabled when Chocolatey is
already available or WinGet is missing. WinGet itself must be installed through
Windows (App Installer). Chocolatey's installation directory is checked directly
so it can be used without waiting for the running app's PATH to update.

The library catalog currently maps:

| Tool | WinGet package ID | Chocolatey package ID |
| --- | --- | --- |
| Chocolatey (separate package-manager setup) | `Chocolatey.Chocolatey` | — |
| Sysinternals Suite | `Microsoft.Sysinternals.Suite` | `sysinternals` |
| WinDbg | `Microsoft.WinDbg` | `windbg` |
| ProcDump | Not available | `procdump` |
| Wireshark | `WiresharkFoundation.Wireshark` | `wireshark` |
| Npcap | Not available | `npcap` |
| Windows Performance Toolkit (Windows ADK package) | `Microsoft.WindowsADK` | `windows-adk-all` |

The app's **Debug Machine Setup** button installs the union of all prerequisites defined for crash, performance, networking, and MCP Server scenarios. It asks for confirmation and lists which package manager will install each tool. It also registers ProcDump for full-memory postmortem dumps.

The scenario-specific **Install prerequisites** control follows the selected diagnostic scenario:

| Scenario | Prerequisites |
| --- | --- |
| Crash analysis | WinDbg, Sysinternals Suite, ProcDump (registered as postmortem debugger with `-ma`) |
| Performance | Windows Performance Toolkit, Sysinternals Suite |
| Networking | Wireshark, Npcap, Sysinternals Suite |
| MCP Server | Node.js (required to run `wdbgmcp`) |

For scenarios with defined prerequisites, the app checks for both package managers, lets the user select one, then asks for confirmation before installing that scenario's tools. Npcap currently has no direct WinGet package, so when WinGet is selected for networking, the app explicitly routes only Npcap through Chocolatey and shows the per-tool source in the confirmation dialog. If Chocolatey is unavailable, it blocks the install and directs you to Package Manager Setup on the left. The library starts package-manager processes without a shell, passes package IDs as separate arguments, accepts the WinGet package/source agreements for the already-confirmed install, and returns command output and failures to the caller. Chocolatey setup is separate from scenario installation; administrator elevation is not requested automatically. If a selected manager requires elevation, run the toolkit with appropriate privileges; the app reports failures rather than silently retrying or elevating. Npcap installs a network capture driver and may require administrator privileges and acceptance of its license.

For crash analysis, ProcDump is installed through Chocolatey because there is no direct WinGet package in the current catalog. After all crash prerequisites install successfully, the app accepts ProcDump's Sysinternals license and registers it as the system postmortem debugger by running `procdump -accepteula -i <dump-directory> -ma`. This replaces any existing postmortem debugger registration. Dumps are written to `%ProgramData%\WdbgToolkit\CrashDumps`. This is a system-wide AeDebug change and requires an elevated toolkit process. Full dumps can be large and contain sensitive process memory; protect and regularly clean up the dump directory. ProcDump's `-u` option removes its postmortem registration if it needs to be undone.

### MCP server and VS Code

Selecting the **MCP Server** scenario shows an **MCP Server** panel with **Start server** / **Stop server** buttons and a read-only, copyable **connection details** box. The app resolves `mcp/wdbgmcp/dist/index.js` alongside the installed app, or from the repository in a development build. **Start server** launches it with `node`, listening over HTTP (the MCP Streamable HTTP transport) bound to all network interfaces on port 7890 by default. The connection details box shows ready-to-paste config snippets pointing at `http://<this-machine's-LAN-IP>:7890/mcp` with the required authentication header. The machine's firewall must allow inbound connections on that port for a remote client to connect.

The server exposes one MCP tool, `execute_command`, which runs a requested program
and returns its output and exit status. Starting it requires confirmation. All
HTTP requests require the bearer token included in the connection snippets; copy
new details after relaunching the app and keep the token private.

To connect VS Code, start the server, copy the **VS Code `mcp.json`** snippet from
the app, then merge its `wdbgmcp` entry into the `servers` object in User MCP
Configuration or `.vscode/mcp.json`. Preserve the supplied URL and authorization
header; do not commit the token. See the complete
[MCP capabilities, VS Code setup, limits, and security guide](mcp/wdbgmcp/README.md).
Use a trusted network with restricted firewall access or a correctly configured
TLS proxy/tunnel; the server's HTTP traffic is unencrypted.

**Start as admin** requests UAC approval and relaunches the toolkit elevated,
selects MCP Server, and starts it after the command-execution confirmation. The
original window closes once the elevated process launches. Copy fresh connection
details from the elevated window because its token is different. Stop any running
server before changing permissions. Cancelling UAC leaves the original app open.

## Product direction

- Provision diagnostic tools through explicit, user-confirmed WinGet or Chocolatey operations.
- Run maintained PowerShell scenarios with validated parameters, consent, timeouts, cancellation, and captured output.
- Keep raw artifacts, normalized findings, and session metadata traceable and exportable.
- The MCP server (`mcp/wdbgmcp`) is a separate TypeScript project with authenticated access to collected results and bounded command execution for AI-driven diagnostics.
- Treat dumps, packet captures, and logs as sensitive diagnostic data.
