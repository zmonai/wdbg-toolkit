# Windows Debug Toolkit

A Windows desktop foundation for guided crash, performance, and networking diagnostics.

> **Current status:** The app can detect WinGet or Chocolatey and offers scenario-specific prerequisite installation. Crash setup includes registering ProcDump for full-memory postmortem dumps after explicit confirmation. Each scenario also exposes runnable workflow actions (crash dump listing/analysis, performance tracing, packet capture/summary) that write artifacts and a manifest under `%ProgramData%\WdbgToolkit`. A WiX project builds an MSI for the desktop app. A separate TypeScript MCP server (`wdbgmcp`) exposes those workflow runs and artifacts to an LLM client, read-only.

## Projects

- `src/WdbgToolkit.App` — WPF desktop application.
- `src/WdbgToolkit.App/Assets/Wdt.ico` — multi-resolution WDT icon used by the executable, window/taskbar, and MSI shortcut.
- `src/WdbgToolkit.Core` — UI-independent diagnostic scenario contracts and catalog.
- `src/WdbgToolkit.PackageManagement` — package-manager detection and explicit tool installation via WinGet or Chocolatey.
- `src/WdbgToolkit.Workflows` — UI-independent per-scenario diagnostic actions (crash, performance, networking) with a shared run-manifest convention, reusable by the app and a future MCP server.
- `installer/WdbgToolkit.Installer` — WiX Toolset 6 MSI project for the desktop app.
- `tests/WdbgToolkit.Core.Tests` — core, package-management, and workflow-action tests.
- `mcp/wdbgmcp` — separate TypeScript MCP server exposing workflow run manifests and artifacts to an LLM client. See [`mcp/wdbgmcp/README.md`](mcp/wdbgmcp/README.md).

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

The current app displays planned scenarios and host OS/.NET information, and can install the listed prerequisites through the package-management library after explicit confirmation. It does not collect diagnostic data or analyze files.

The MSI is generated at `installer\WdbgToolkit.Installer\bin\Release\WdbgToolkit.Installer.msi`. It installs the desktop app under Program Files and creates an all-users Start Menu shortcut. The app is currently framework-dependent, so installing or running it requires the matching .NET 10 Windows Desktop Runtime. The MSI does not install .NET, WinGet, Chocolatey, or any diagnostic tools. Update the MSI `Package` version when releasing a new version; the stable upgrade code enables major upgrades.

## Package management library

`WdbgToolkit.PackageManagement` does not install or bootstrap WinGet or Chocolatey. It assumes WinGet is provided by Windows and checks for both managers by running their version commands. If a manager is unavailable, installation is reported as unavailable; the library never tries to install it.

The library catalog currently maps:

| Tool | WinGet package ID | Chocolatey package ID |
| --- | --- | --- |
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

For scenarios with defined prerequisites, the app checks for both package managers, lets the user select one, then asks for confirmation before installing that scenario's tools. Npcap currently has no direct WinGet package, so when WinGet is selected for networking, the app explicitly routes only Npcap through Chocolatey and shows the per-tool source in the confirmation dialog. If Chocolatey is unavailable, it blocks the install and explains that Chocolatey must be installed separately. The library starts package-manager processes without a shell, passes package IDs as separate arguments, accepts the WinGet package/source agreements for the already-confirmed install, and returns command output and failures to the caller. Chocolatey is not installed by the library, and administrator elevation is not requested automatically. If a selected manager requires elevation, run the toolkit with appropriate privileges; the app reports failures rather than silently retrying or elevating. Npcap installs a network capture driver and may require administrator privileges and acceptance of its license.

For crash analysis, ProcDump is installed through Chocolatey because there is no direct WinGet package in the current catalog. After all crash prerequisites install successfully, the app accepts ProcDump's Sysinternals license and registers it as the system postmortem debugger by running `procdump -accepteula -i <dump-directory> -ma`. This replaces any existing postmortem debugger registration. Dumps are written to `%ProgramData%\WdbgToolkit\CrashDumps`. This is a system-wide AeDebug change and requires an elevated toolkit process. Full dumps can be large and contain sensitive process memory; protect and regularly clean up the dump directory. ProcDump's `-u` option removes its postmortem registration if it needs to be undone.

## Workflow actions

`WdbgToolkit.Workflows` defines reusable, UI-independent actions per scenario. Both the WPF app and the future MCP server drive actions through `WorkflowActionCatalog`/`WorkflowRunner`, so behavior is never duplicated between the two.

| Scenario | Action ID | What it does |
| --- | --- | --- |
| Crash | `crash.list-dumps` | Lists crash dump files in `%ProgramData%\WdbgToolkit\CrashDumps`. |
| Crash | `crash.analyze-dump` | Runs `cdb -z <dump> -c "!analyze -v;q"` against the most recent (or a specified) dump. |
| Performance | `performance.start-trace` | Starts a WPR trace (`wpr -start <profile>`, default `GeneralProfile.Light`). |
| Performance | `performance.stop-trace` | Stops the active WPR trace and saves an `.etl` file (`wpr -stop`). |
| Networking | `networking.capture-packets` | Captures packets for a bounded duration with `dumpcap -i <interface> -a duration:<n> -w <file>.pcapng`. |
| Networking | `networking.summarize-capture` | Summarizes a `.pcapng` capture with `tshark -r <file> -q -z io,phs`. |

Each run creates `%ProgramData%\WdbgToolkit\<scenario>\<timestamp>\manifest.json` recording the action, its result, and any artifact paths, giving the app and the MCP server an identical, inspectable record of what ran. In the app, select a scenario and use **Workflow actions**; actions with a required input (e.g. a capture path) show a text box before the **Run** button. `wpr` and `dumpcap` may require administrator privileges to run successfully.

### MCP Server scenario

Selecting the **MCP Server** scenario shows an **MCP Server** panel with **Start server** / **Stop server** buttons and a read-only, copyable **connection details** box. The app resolves the built `mcp/wdbgmcp/dist/index.js` entry point (built with `npm run build` in `mcp/wdbgmcp`) relative to the running app, and **Start server** launches it with `node`, listening over HTTP (the MCP Streamable HTTP transport) bound to all network interfaces on port 7890 by default. The connection details box shows ready-to-paste config snippets (for VS Code's `mcp.json` and for the `mcpServers` schema used by Claude Desktop, Cursor, and most other clients) pointing at `http://<this-machine's-LAN-IP>:7890/mcp`, so any MCP client — on this machine or another one on the network — can connect directly, without needing to spawn its own copy of the process. The machine's firewall must allow inbound connections on that port for a remote client to connect. This entry-point lookup currently only works for a local/build-from-source layout; an installed MSI build does not yet bundle `mcp/wdbgmcp`.

## Product direction

- Provision diagnostic tools through explicit, user-confirmed WinGet or Chocolatey operations.
- Keep collection and analysis behind scenario-specific adapters in the core orchestration layer.
- Run maintained PowerShell scenarios with validated parameters, consent, timeouts, cancellation, and captured output.
- Keep raw artifacts, normalized findings, and session metadata traceable and exportable.
- The MCP server (`mcp/wdbgmcp`) is a separate TypeScript project with read-only access to collected results in its first release.
- Treat dumps, packet captures, and logs as sensitive diagnostic data.
