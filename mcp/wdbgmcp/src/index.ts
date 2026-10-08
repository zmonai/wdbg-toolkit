#!/usr/bin/env node
import { createServer, type IncomingMessage, type ServerResponse } from "node:http";
import { createHash, timingSafeEqual } from "node:crypto";
import { pathToFileURL } from "node:url";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { StdioServerTransport } from "@modelcontextprotocol/sdk/server/stdio.js";
import { StreamableHTTPServerTransport } from "@modelcontextprotocol/sdk/server/streamableHttp.js";
import { commandInput, executeCommand } from "./commandRunner.js";

/** Default TCP port for the HTTP (Streamable HTTP) transport. Override with WDBGMCP_PORT. */
const DEFAULT_PORT = 7890;

/** HTTP request path MCP clients POST/GET/DELETE against. */
const MCP_PATH = "/mcp";

/**
 * Builds a fully configured MCP server. Stateless HTTP mode creates a fresh server and
 * transport per request, because a stateless transport can only serve a single request.
 */
export function createMcpServer(): McpServer {
  const server = new McpServer({
    name: "wdbgmcp",
    version: "0.1.0",
  });

  server.registerTool(
    "execute_command",
    {
      title: "Execute a command on the debugging machine",
      description: "Runs an executable with arguments on the server machine and returns stdout, stderr, exitCode, durationMs, and termination flags for analysis. Commands run with the server account's permissions and can modify the machine. Use only with the machine owner's approval. Shell syntax requires explicitly invoking powershell.exe or cmd.exe.",
      inputSchema: commandInput,
      annotations: { readOnlyHint: false, destructiveHint: true, idempotentHint: false, openWorldHint: true },
    },
    async (input, extra) => {
      try {
        const result = await executeCommand(input, extra.signal);
        return {
          isError: result.exitCode !== 0 || result.timedOut || result.outputLimitExceeded || result.cancelled || !!result.error,
          content: [{ type: "text", text: JSON.stringify(result, null, 2) }],
          structuredContent: { ...result },
        };
      } catch (error) {
        return { isError: true, content: [{ type: "text", text: error instanceof Error ? error.message : String(error) }] };
      }
    },
  );

  return server;
}

/**
 * Runs the server over the Streamable HTTP transport, bound to all network interfaces
 * so remote MCP clients (e.g. VS Code or an LLM tool running on another machine) can
 * reach it at http://<this-machine-ip>:<port>/mcp. This is the default transport since
 * wdbgmcp is normally started from the Windows Debug Toolkit app and consumed remotely.
 */
async function runHttp(): Promise<void> {
  const port = Number(process.env.WDBGMCP_PORT ?? DEFAULT_PORT);
  const httpServer = createHttpServer(process.env.WDBGMCP_TOKEN ?? "");

  await new Promise<void>((resolve, reject) => {
    httpServer.once("error", reject);
    httpServer.listen(port, "0.0.0.0", () => {
      httpServer.removeListener("error", reject);
      resolve();
    });
  });

  console.error(`wdbgmcp listening on http://0.0.0.0:${port}${MCP_PATH}`);
}

export function createHttpServer(token: string) {
  if (token.length < 32 || /[\s\x00-\x1f\x7f]/.test(token)) {
    throw new Error("HTTP requires WDBGMCP_TOKEN: at least 32 characters without whitespace or control characters.");
  }
  const digest = (value: string) => createHash("sha256").update(value).digest();
  const expected = digest(`Bearer ${token}`);
  return createServer((req: IncomingMessage, res: ServerResponse) => {
    if (!timingSafeEqual(expected, digest(req.headers.authorization ?? ""))) {
      // This is a pre-shared credential, not an OAuth authorization server.
      // MCP clients interpret 401 as a request to start OAuth discovery.
      res.writeHead(403, { "Content-Type": "application/json" }).end(JSON.stringify({
        jsonrpc: "2.0",
        error: {
          code: -32000,
          message: "Missing or invalid connection token. Copy the current connection details from Windows Debug Toolkit into your MCP configuration, including headers.Authorization. wdbgmcp does not support OAuth sign-in.",
        },
        id: null,
      }));
      return;
    }
    // Browsers are not supported command clients. Prevent cross-origin requests.
    if (req.headers.origin) {
      res.writeHead(403, { "Content-Type": "text/plain" }).end("Browser origins are not allowed");
      return;
    }
    const url = new URL(req.url ?? "/", `http://${req.headers.host ?? "localhost"}`);
    if (url.pathname !== MCP_PATH) {
      res.writeHead(404, { "Content-Type": "text/plain" }).end("Not found");
      return;
    }

    // Stateless mode has no server-initiated SSE stream or session to terminate.
    if (req.method !== "POST") {
      res
        .writeHead(405, { "Content-Type": "application/json", Allow: "POST" })
        .end(JSON.stringify({ jsonrpc: "2.0", error: { code: -32000, message: "Method not allowed." }, id: null }));
      return;
    }

    void handleStatelessRequest(req, res);
  });
}

/** Serves one POST with its own server/transport pair, as required by stateless Streamable HTTP. */
async function handleStatelessRequest(req: IncomingMessage, res: ServerResponse): Promise<void> {
  const server = createMcpServer();
  const transport = new StreamableHTTPServerTransport({ sessionIdGenerator: undefined });
  res.on("close", () => {
    void transport.close();
    void server.close();
  });

  try {
    await server.connect(transport);
    await transport.handleRequest(req, res);
  } catch (error) {
    console.error("wdbgmcp: error handling request:", error);
    if (!res.headersSent) {
      res
        .writeHead(500, { "Content-Type": "application/json" })
        .end(JSON.stringify({ jsonrpc: "2.0", error: { code: -32603, message: "Internal server error" }, id: null }));
    }
  }
}

/** Runs the server over stdio, for MCP clients that spawn and own the child process directly. */
async function runStdio(): Promise<void> {
  const transport = new StdioServerTransport();
  await createMcpServer().connect(transport);
}

async function main() {
  const transportMode = (process.env.WDBGMCP_TRANSPORT ?? "http").trim().toLowerCase();
  if (transportMode === "stdio") {
    await runStdio();
  } else {
    await runHttp();
  }
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  main().catch((error) => {
    console.error("wdbgmcp failed to start:", error);
    process.exit(1);
  });
}
