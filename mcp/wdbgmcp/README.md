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

The server exposes exactly one MCP tool:

| Tool | Capability |
| --- | --- |
| `execute_command` | Starts an executable on the server machine and returns its stdout, stderr, exit code, signal, duration, and timeout/output-limit/cancellation status. |

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
   contains the host URL and a per-app-instance bearer token. Do not share or
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
   listed. Approve each command request according to your normal review process.

The URL uses the debug machine's LAN address. For VS Code on the same machine, use
the URL supplied by the app as-is; `localhost` may be used only if the client and
server are on that same machine. The app verifies its token against the local
endpoint before reporting that the server started.

### Keep the connection secure

- The token changes when the toolkit app is relaunched, and differs between
  standard-user and elevated app instances. Copy fresh details from the same app
  instance that started the server.
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
ends in `/mcp`. Relaunching the toolkit changes the token.

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
