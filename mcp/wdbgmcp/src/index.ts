#!/usr/bin/env node
import { createServer, type IncomingMessage, type ServerResponse } from "node:http";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { StdioServerTransport } from "@modelcontextprotocol/sdk/server/stdio.js";
import { StreamableHTTPServerTransport } from "@modelcontextprotocol/sdk/server/streamableHttp.js";
import { z } from "zod";
import { getRootDirectory, getRun, listRuns, listScenarios, readArtifact } from "./workflowStore.js";

/** Default TCP port for the HTTP (Streamable HTTP) transport. Override with WDBGMCP_PORT. */
const DEFAULT_PORT = 7890;

/** HTTP request path MCP clients POST/GET/DELETE against. */
const MCP_PATH = "/mcp";

const server = new McpServer({
  name: "wdbgmcp",
  version: "0.1.0",
});

server.registerTool(
  "list_scenarios",
  {
    title: "List diagnostic scenarios",
    description:
      "Lists diagnostic scenario ids (e.g. crash, performance, networking, custom-logs) that have at least one recorded Windows Debug Toolkit workflow run.",
    inputSchema: {},
  },
  async () => {
    const scenarios = await listScenarios();
    return {
      content: [{ type: "text", text: JSON.stringify({ root: getRootDirectory(), scenarios }, null, 2) }],
    };
  },
);

server.registerTool(
  "list_workflow_runs",
  {
    title: "List workflow runs",
    description:
      "Lists Windows Debug Toolkit workflow runs (most recent first), optionally filtered to one scenario. Each run includes its manifest: action id, success, summary, structured data, and artifact paths.",
    inputSchema: {
      scenarioId: z
        .string()
        .optional()
        .describe("Scenario id to filter by (e.g. 'crash', 'performance', 'networking', 'custom-logs'). Omit to list runs across all scenarios."),
    },
  },
  async ({ scenarioId }) => {
    const runs = await listRuns(scenarioId);
    return {
      content: [{ type: "text", text: JSON.stringify(runs, null, 2) }],
    };
  },
);

server.registerTool(
  "get_workflow_run",
  {
    title: "Get a workflow run's manifest",
    description: "Reads a single workflow run's manifest.json by scenario id and run id (the run's timestamp folder name).",
    inputSchema: {
      scenarioId: z.string().describe("Scenario id, e.g. 'crash'."),
      runId: z.string().describe("Run id (timestamp folder name), e.g. '20260101-120000'."),
    },
  },
  async ({ scenarioId, runId }) => {
    const run = await getRun(scenarioId, runId);
    if (!run) {
      return {
        isError: true,
        content: [{ type: "text", text: `No run found for scenario '${scenarioId}' and run id '${runId}'.` }],
      };
    }

    return {
      content: [{ type: "text", text: JSON.stringify(run, null, 2) }],
    };
  },
);

server.registerTool(
  "read_artifact",
  {
    title: "Read a workflow artifact file",
    description:
      "Reads the text contents of an artifact file produced by a workflow run (e.g. a !analyze report or a tshark summary). The path must be one of the 'Artifacts' entries from a run's manifest.",
    inputSchema: {
      artifactPath: z.string().describe("Absolute path to the artifact file, as returned in a run manifest's Artifacts array."),
    },
  },
  async ({ artifactPath }) => {
    try {
      const contents = await readArtifact(artifactPath);
      return { content: [{ type: "text", text: contents }] };
    } catch (error) {
      return {
        isError: true,
        content: [{ type: "text", text: error instanceof Error ? error.message : String(error) }],
      };
    }
  },
);

/**
 * Runs the server over the Streamable HTTP transport, bound to all network interfaces
 * so remote MCP clients (e.g. VS Code or an LLM tool running on another machine) can
 * reach it at http://<this-machine-ip>:<port>/mcp. This is the default transport since
 * wdbgmcp is normally started from the Windows Debug Toolkit app and consumed remotely.
 */
async function runHttp(): Promise<void> {
  const port = Number(process.env.WDBGMCP_PORT ?? DEFAULT_PORT);
  // Stateless mode: no sessionIdGenerator, so every request is independent and the
  // server doesn't need to track per-client session state across requests.
  const transport = new StreamableHTTPServerTransport({ sessionIdGenerator: undefined });
  await server.connect(transport);

  const httpServer = createServer((req: IncomingMessage, res: ServerResponse) => {
    const url = new URL(req.url ?? "/", `http://${req.headers.host ?? "localhost"}`);
    if (url.pathname !== MCP_PATH) {
      res.writeHead(404, { "Content-Type": "text/plain" }).end("Not found");
      return;
    }

    transport.handleRequest(req, res).catch((error) => {
      console.error("wdbgmcp: error handling request:", error);
      if (!res.headersSent) {
        res.writeHead(500, { "Content-Type": "text/plain" }).end("Internal server error");
      }
    });
  });

  await new Promise<void>((resolve, reject) => {
    httpServer.once("error", reject);
    httpServer.listen(port, "0.0.0.0", () => {
      httpServer.removeListener("error", reject);
      resolve();
    });
  });

  console.error(`wdbgmcp listening on http://0.0.0.0:${port}${MCP_PATH}`);
}

/** Runs the server over stdio, for MCP clients that spawn and own the child process directly. */
async function runStdio(): Promise<void> {
  const transport = new StdioServerTransport();
  await server.connect(transport);
}

async function main() {
  const transportMode = (process.env.WDBGMCP_TRANSPORT ?? "http").trim().toLowerCase();
  if (transportMode === "stdio") {
    await runStdio();
  } else {
    await runHttp();
  }
}

main().catch((error) => {
  console.error("wdbgmcp failed to start:", error);
  process.exit(1);
});
