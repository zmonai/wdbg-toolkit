# wdbgmcp

A Model Context Protocol (MCP) server that gives an LLM read access to the diagnostic
data collected by the [Windows Debug Toolkit](../../README.md) (`WdbgToolkit.App`).

It does not install tools or run diagnostic actions itself. The desktop app's
`WdbgToolkit.Workflows` library is the thing that actually runs `cdb`, `wpr`,
`dumpcap`/`tshark`, and custom PowerShell scripts; each run writes a `manifest.json`
plus any artifact files under:

```
%ProgramData%\WdbgToolkit\<scenarioId>\<runId>\manifest.json
```

`wdbgmcp` scans that same directory tree and exposes it to an MCP client (e.g. an LLM
chat client) as tools, matching the toolkit's "read-only access to collected results in
its first release" product direction.

## Tools

| Tool | Description |
| --- | --- |
| `list_scenarios` | Lists scenario ids that have at least one recorded workflow run. |
| `list_workflow_runs` | Lists runs (most recent first), optionally filtered to one scenario, including each run's manifest. |
| `get_workflow_run` | Reads a single run's `manifest.json` by scenario id and run id. |
| `read_artifact` | Reads the text contents of an artifact file referenced in a run's manifest. |

## Requirements

- Node.js 18+.
- Run on (or with access to) the same machine/share where `WdbgToolkit.App` wrote its
  `%ProgramData%\WdbgToolkit` runs, or set `WDBGMCP_ROOT` to point at a copy of that
  directory.

## Build and run

```powershell
npm install
npm run build
npm start
```

By default `wdbgmcp` speaks MCP over the **Streamable HTTP** transport, listening on all
network interfaces (`0.0.0.0`) on port `7890` at the `/mcp` path — e.g.
`http://<this-machine's-IP>:7890/mcp`. This lets any MCP client (VS Code, an LLM
assistant, etc.), on this machine or another one on the network, connect directly by
URL instead of needing to spawn its own copy of the process. The Windows Debug Toolkit
app's **MCP Server** scenario starts/stops this same server and shows ready-to-paste
VS Code (`mcp.json`) and `mcpServers`-style config snippets with the resolved URL.

Set `WDBGMCP_TRANSPORT=stdio` to run over stdio instead, for MCP clients that spawn and
own the child process directly rather than connecting over the network.

## Configuration

| Environment variable | Purpose |
| --- | --- |
| `WDBGMCP_ROOT` | Overrides the workflow-run root directory. Defaults to `%ProgramData%\WdbgToolkit`. Useful for testing or reading runs collected on another machine. |
| `WDBGMCP_TRANSPORT` | `http` (default) or `stdio`. Selects the MCP transport. |
| `WDBGMCP_PORT` | TCP port for the HTTP transport. Defaults to `7890`. Ignored in `stdio` mode. |

## Project layout

- `src/workflowStore.ts` — filesystem access: lists scenarios/runs and reads
  manifests/artifacts, with path checks to keep reads confined to the workflow root.
- `src/index.ts` — MCP server entry point; registers the tools above over HTTP
  (default) or stdio (`WDBGMCP_TRANSPORT=stdio`).

## Status

Read-only, first-release scope. Running actions remotely (e.g. triggering a capture or
script from the LLM side) is not implemented yet — see the toolkit root README's
"Product direction" section.
