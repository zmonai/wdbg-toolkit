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

`wdbgmcp` speaks MCP over stdio, so it's meant to be launched by an MCP client (for
example, as a configured server in an AI assistant), not run interactively on its own.

## Configuration

| Environment variable | Purpose |
| --- | --- |
| `WDBGMCP_ROOT` | Overrides the workflow-run root directory. Defaults to `%ProgramData%\WdbgToolkit`. Useful for testing or reading runs collected on another machine. |

## Project layout

- `src/workflowStore.ts` — filesystem access: lists scenarios/runs and reads
  manifests/artifacts, with path checks to keep reads confined to the workflow root.
- `src/index.ts` — MCP server entry point; registers the tools above over stdio.

## Status

Read-only, first-release scope. Running actions remotely (e.g. triggering a capture or
script from the LLM side) is not implemented yet — see the toolkit root README's
"Product direction" section.
