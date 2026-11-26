# wdbgmcp

`wdbgmcp` is the Model Context Protocol (MCP) server for the
[Windows Debug Toolkit](../../README.md). It lets a connected AI client run
diagnostic commands on the Windows machine hosting the server and inspect their
output.

> [!WARNING]
> **This MCP server provides remote code execution. Do not use it on high-value,
> production, sensitive, or otherwise critical systems.** Any client that obtains
> the bearer token can ask the server to run programs with the server process's
> permissions. This is not a sandbox: commands can read, change, or delete data,
> install software, and change system settings. Running the toolkit as administrator
> gives AI-issued commands administrator access. Authentication does not make
> untrusted AI clients or networks safe.
>
> Use only on a disposable or dedicated debugging machine that you are authorized
> to control. Prefer running as a standard user. Keep the server off public and
> untrusted networks, restrict inbound firewall access, and never publish the
> bearer token. HTTP is unencrypted; on networks you do not fully trust, do not
> connect directly—use a properly configured TLS proxy or secure tunnel.

## What it can do

The server exposes these MCP tools:

| Tool | Capability |
| --- | --- |
| `execute_command` | Starts an executable on the server machine and returns its stdout, stderr, exit code, signal, duration, and timeout/output-limit/cancellation status. |
| `configure_windows_ssh` | Inspects SSH status or, after explicit confirmation, installs/enables or disables Windows SSH remote access. |
| `ghidra_status` | Checks the Ghidra installation, backend schema, and open programs. |
| `ghidra_list_tools` | Discovers read-only backend features and their parameters. |
| `ghidra_search_strings` | Searches defined strings by regex with pagination. |
| `ghidra_list_items` | Lists methods/functions, imports, exports, classes, namespaces, segments, and data. |
| `ghidra_decompile` | Returns inferred C-like pseudocode and backend warnings for one function. |
| `ghidra_disassemble` | Reads existing assembly for one function. |
| `ghidra_xrefs` | Lists references to/from an address. |
| `ghidra_call_tool` | Calls other advertised non-destructive read-only backend tools, including function hashes, byte/instruction searches, and symbol/memory inspection. |
| `ghidra_import` | Imports a backend-local file after explicit confirmation, optionally starting analysis; never executes it. |

SSH is optional, not required for MCP or headless Ghidra. For `configure_windows_ssh`,
`action: "status"` makes no configuration changes. `action: "install_enable"`
requires `confirmed: true`, administrator privileges, and a nonempty
`allowedClients` array of explicit IP addresses/CIDRs; no network default exists.
It installs the Windows OpenSSH Server capability if sshd is missing, enables
automatic startup, creates a toolkit TCP 22 firewall rule for those clients, and
disables the known Windows default `OpenSSH-Server-In-TCP` rule. Existing SSH
authentication and unrelated firewall rules remain unchanged; review them
separately. No credentials, SSH keys, or alternate MSI are installed.

`action: "disable"` requires confirmation and administrator privileges; it stops
sshd, disables startup, and disables the toolkit firewall rule, without uninstalling
SSH. Setup failures/timeouts may leave partial changes. Check status before retrying;
Windows capability installation may require servicing sources or a manual reboot.
The toolkit MSI includes the shared PowerShell setup script. Standalone npm
installations must retain the package's `scripts` directory alongside `dist`.

For example, an AI client can run an installed diagnostic utility such as `ipconfig`,
`cdb`, `wpr`, `dumpcap`, or PowerShell, then analyze the returned text. Use the
executable and its arguments as separate fields:

```json
{
  "executable": "ipconfig.exe",
  "arguments": ["/all"],
  "timeoutMs": 30000,
  "maxOutputBytes": 262144
}
```

Arguments are passed literally; the server does not automatically invoke a shell.
For shell syntax, explicitly run PowerShell, for example:

```json
{
  "executable": "powershell.exe",
  "arguments": [
    "-NoProfile",
    "-NonInteractive",
    "-Command",
    "Get-Process | Select-Object -First 10"
  ]
}
```

You may also specify `workingDirectory` as an absolute path on the server machine.
The command runs under the account used to start `wdbgmcp`; it is non-interactive
and stdin is closed. The tool is intended for commands that finish and return
diagnostic output, not for starting background services.

### Execution limits

- Default timeout: 30 seconds; maximum: 5 minutes.
- Default combined stdout/stderr limit: 256 KiB; maximum: 1 MiB.
- Maximum four simultaneous commands.
- Timeout, cancellation, or output-limit overflow triggers process termination.
- Commands run with the server user's permissions; administrator mode raises this
  to administrator permissions.
- Command results are returned as MCP text and structured content. Failed commands
  include their output and exit status and are marked as errors.

These limits help bound individual calls; **they do not restrict what a command can
do before it exits and are not a security sandbox**.

## Connect from Visual Studio Code

Prerequisites:

- Install the Windows Debug Toolkit and Node.js 18 or later on the machine that
  will host the MCP server. The MSI bundles `wdbgmcp`; users do not need to run
  `npm install`.
- Install a VS Code version with MCP support.
- Ensure the client machine can reach the host machine. For a remote client, allow
  inbound TCP port `7890` only from trusted clients in the host firewall.

1. Start Windows Debug Toolkit on the host and select **MCP Server**.
2. Choose **Start server** (recommended: standard-user permissions). The app asks
   for confirmation because connected AI clients can execute commands.
3. If elevated permissions are truly needed, use **Start as admin** instead and
   approve the Windows UAC prompt. Stop any already-running server before switching.
   Commands from an elevated server have administrator rights.
4. In the toolkit app, copy the **VS Code `mcp.json`** connection snippet. It
   contains the host URL and a bearer token. Do not share or
   commit the token.
5. In VS Code, run **MCP: Open User Configuration** from the Command Palette, or
   add the server to the workspace's `.vscode/mcp.json`. Merge the copied `wdbgmcp`
   entry into the existing `servers` object; preserve other configured servers.
   It should have this shape:

   ```json
   {
     "servers": {
       "wdbgmcp": {
         "type": "http",
         "url": "http://<debug-machine-ip>:7890/mcp",
         "headers": {
           "Authorization": "Bearer <copy-the-current-token-from-the-toolkit>"
         }
       }
     }
   }
   ```

   Replace both placeholders with the exact values copied from the app. The URL
   must end in `/mcp`; the `Authorization` value must start with `Bearer `.
6. Save the configuration, then start or restart `wdbgmcp` in VS Code's MCP
   server controls. Inspect the available MCP tools; `execute_command` should be
   listed alongside SSH/Ghidra tools. Ghidra tools require the separate backend
   described below. Approve each command request according to your normal review process.

The URL uses the debug machine's LAN address. For VS Code on the same machine, use
the URL supplied by the app as-is; `localhost` may be used only if the client and
server are on that same machine. The app verifies its token against the local
endpoint before reporting that the server started.

### Run automatically after reboot

In the MCP Server panel, check **Run as a Windows service**, then choose **Start as
admin** and confirm. Use the MSI-installed toolkit and machine-wide Node.js at
`%ProgramFiles%\nodejs\node.exe`. The `WdbgToolkitMcp` service runs without a signed-in
user and stays running when the toolkit closes. Windows starts it automatically
after reboot and restarts it after an unexpected MCP process exit.

**Service commands run as LocalSystem with full system privileges.** They do not
run in your desktop session and cannot rely on mapped drives, per-user installations,
or your interactive credentials. Prefer standard-user mode unless this persistent
privileged access is necessary.

Service connection details use a persistent token stored in
`%ProgramData%\WdbgToolkit.McpService\settings.json`, restricted to Administrators
and SYSTEM. After reopening the toolkit, use **Manage as admin** to view it.
**Stop server** stops the service until reboot or an explicit start.
**Remove service** stops it, disables automatic startup, and deletes the token;
reinstalling creates a new token. Remove the service before uninstalling, moving,
or upgrading the toolkit or Node.js. **Refresh status** reloads its current state.
MSI uninstall and major upgrades also remove the service and token; explicitly
enable service mode again after upgrading and update the client token.
The machine's IP may change after reboot; use stable addressing if needed.
Service mode does not change firewall rules or encrypt HTTP.

### Keep the connection secure

- In app-owned mode, the token changes when the toolkit app is relaunched, and differs between
  standard-user and elevated app instances. Copy fresh details from the same app
  instance that started the server. Service mode preserves its token across reboots;
  remove and reinstall the service to rotate it.
- Treat the token like a password: anyone who has it can invoke remote commands.
  Do not paste it into chat, source control, screenshots, or logs.
- The HTTP endpoint listens on all network interfaces (`0.0.0.0`). A bearer token
  does not encrypt traffic. Use a trusted, isolated network with firewall access
  restricted to approved clients, or use a correctly configured TLS proxy/tunnel.
- Do not expose port `7890` directly to the public internet.
- Stop the server when it is not needed.

## Troubleshooting

### “Missing or invalid connection token”

Stop and restart the server from the toolkit app, copy its current VS Code snippet,
and replace the complete `wdbgmcp` entry in the active VS Code MCP configuration.
Check that the request uses the copied `headers.Authorization` value and the URL
ends in `/mcp`. Relaunching the toolkit changes the token only in app-owned mode.
For service mode, copy the existing service token from an elevated toolkit window.

### “Failed to fetch authorization server metadata” or an `/authorize` page

`wdbgmcp` uses a pre-shared bearer token, not OAuth, and does not implement
`/authorize`. Cancel the OAuth flow and use the current copied snippet, including
the authorization header. Do not remove authentication as a workaround.

### Server executable not found

For an installed app, repair or reinstall the current Windows Debug Toolkit MSI;
it includes the bundled MCP server. Node.js 18 or later is still required to launch
it. For a development checkout:

```powershell
Set-Location mcp\wdbgmcp
npm ci
npm run build
```

## Remote Ghidra prerequisites

For the Ghidra MCP-backed PE analysis workflow, install the following manually on
the **remote Windows machine running wdbgmcp**:

- Ghidra at `C:\dbg\ghidra`.
- A compatible 64-bit JDK for that Ghidra release.
- A compatible Ghidra MCP extension/backend, installed and started by the user.

The backend must be reachable from wdbgmcp on that machine; the current setup uses
`http://127.0.0.1:8089`. This is the remote machine's loopback address, not the
client's. Installation does not mean the backend is running: verify its endpoint
and supported API/schema before sending analysis requests. Keep it loopback-only
and configure backend authentication if available. Ghidra projects must not be
opened concurrently by conflicting GUI/headless processes.

Inputs must be readable and project/log/report directories writable by the
analysis account. Prefer a separate low-privilege backend over parsing untrusted
binaries as LocalSystem. Ghidra, Java, and the backend are not installed or started
automatically by wdbgmcp. A client-side Ghidra MCP bridge is not needed if wdbgmcp
calls the backend directly. SSH is optional and is not needed for loopback access.

The normal Ghidra headless analyzer does not require an MCP extension, whereas
the dedicated Ghidra tools use the HTTP backend's `/mcp/schema` contract.
The client validates tool names, parameter types, and query/body placement against
that live schema. Other backend variants may not support this contract; incompatible
schemas/endpoints fail explicitly.

### Using the Ghidra tools

Start with `ghidra_status`, then `ghidra_list_tools` to discover capabilities.
Always specify the full Ghidra program project path when the operation accepts
`program`. Same-named binaries in different folders must not be selected by basename.
For example, call `ghidra_search_strings` with:

```json
{
  "program": "/old/client.exe",
  "pattern": "error|exception",
  "min_length": 4,
  "offset": 0,
  "limit": 100
}
```

Searches apply a regex to defined/analyzed strings; they do not scan every possible
raw-file string. No encoding filter is claimed because the installed backend's
encoding field is only a label. Follow returned string addresses with
`ghidra_xrefs`, then `ghidra_decompile` or `ghidra_disassemble`.
Decompilation is inferred pseudocode, never recovered original C/C++; backend
warnings and partial results are preserved.

Use `ghidra_call_tool` with a tool name (without a leading slash) and its
`parameters` object to access additional features:

```json
{
  "tool": "get_function_hash",
  "parameters": {
    "program": "/old/client.exe",
    "offset": 0,
    "limit": 100
  }
}
```

Only backend-advertised non-destructive read-only tools are allowed through this
generic entry point; arbitrary endpoints, scripts, edits, and saves are not exposed.
`ghidra_import` is a separate opt-in mutation requiring `confirmed: true` and an
absolute `file_path` on the backend machine. Backend file-root permissions still
apply. Import responses do not guarantee analysis has finished.

| Environment setting | Default |
| --- | --- |
| `WDBGMCP_GHIDRA_HOME` | `C:\dbg\ghidra` |
| `WDBGMCP_GHIDRA_URL` | `http://127.0.0.1:8089` |
| `WDBGMCP_GHIDRA_TOKEN` | Unset; optional bearer credential if supported by the backend |

The backend URL must be a loopback HTTP(S) origin with no path, URL credentials,
query, or fragment. Redirects are refused. Protect the optional token; it is not
forwarded to commands launched by `execute_command`. Restart the server/service
after changing its environment settings.

Each backend HTTP request has a 60-second timeout and a 1 MiB response limit.
Parameters are limited to 64 KiB; supplied pagination limits must be 1-500.
Overlarge responses fail instead of appearing complete. Use offsets and smaller
function/range queries. Cancelling or timing out an HTTP request does not guarantee
backend work stops; inspect open programs/backend state before retrying imports.
Persistent/resumable jobs, content-addressed import caching, full cross-version
function matching, and downloadable report archives are not yet implemented.

## Run outside the desktop app

Node.js 18 or later is required. Build and launch:

```powershell
npm ci
npm run build
$env:WDBGMCP_TOKEN = node -e "console.log(require('node:crypto').randomBytes(32).toString('hex'))"
npm start
```

Standalone HTTP mode requires `WDBGMCP_TOKEN` to contain at least 32 characters
with no whitespace or control characters. The desktop app generates and configures
the token automatically. `WDBGMCP_PORT` changes the HTTP port (default `7890`);
`WDBGMCP_TRANSPORT=stdio` selects stdio for a trusted local MCP client that launches
and owns the server process.

Run MCP build and integration tests with:

```powershell
npm run build
npm test
```

The MSI build uses `npm ci` and `npm run build:bundle` to bundle JavaScript
dependencies into the installable MCP server; npm is not needed on the target
machine.
