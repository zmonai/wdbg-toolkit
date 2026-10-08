import assert from "node:assert/strict";
import { test } from "node:test";
import { once } from "node:events";
import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { StreamableHTTPClientTransport } from "@modelcontextprotocol/sdk/client/streamableHttp.js";
import { StdioClientTransport } from "@modelcontextprotocol/sdk/client/stdio.js";
import { fileURLToPath } from "node:url";
import { z } from "zod";
import { createHttpServer } from "./index.js";

const commandResultSchema = z.object({
  exitCode: z.number().nullable(),
  stdout: z.string(),
  stderr: z.string(),
  timedOut: z.boolean(),
});

test("HTTP refuses to start without a strong token", () => {
  assert.throws(() => createHttpServer(""), /WDBGMCP_TOKEN/);
  assert.throws(() => createHttpServer("a".repeat(32) + "\n"), /WDBGMCP_TOKEN/);
});

test("HTTP authenticates requests and serves AI command output to multiple clients", async () => {
  const token = "test-only-token-".repeat(4);
  const server = createHttpServer(token);
  server.listen(0, "127.0.0.1");
  await once(server, "listening");
  const address = server.address();
  assert.ok(address && typeof address !== "string");
  const url = new URL(`http://127.0.0.1:${address.port}/mcp`);
  const headers = { Authorization: `Bearer ${token}` };
  const clients: Client[] = [];
  try {
    for (const authorization of [undefined, "Bearer wrong"]) {
      const response = await fetch(url, {
        method: "POST",
        headers: authorization ? { Authorization: authorization } : {},
      });
      assert.equal(response.status, 403);
      assert.equal(response.headers.get("WWW-Authenticate"), null);
      assert.match(await response.text(), /Copy the current connection details/);
    }
    assert.equal((await fetch(url, { method: "POST", headers: { ...headers, Origin: "https://example.com" } })).status, 403);
    assert.equal((await fetch(url, { headers })).status, 405);
    const unauthenticatedClient = new Client({ name: "oauth-capable-client", version: "1" });
    clients.push(unauthenticatedClient);
    const requestedUrls: string[] = [];
    const unexpectedOAuth = () => { throw new Error("OAuth discovery must not run for a static token error."); };
    await assert.rejects(unauthenticatedClient.connect(new StreamableHTTPClientTransport(url, {
      authProvider: {
        redirectUrl: "http://localhost/callback",
        clientMetadata: { redirect_uris: ["http://localhost/callback"] },
        clientInformation: unexpectedOAuth,
        tokens: () => undefined,
        saveTokens: unexpectedOAuth,
        redirectToAuthorization: unexpectedOAuth,
        saveCodeVerifier: unexpectedOAuth,
        codeVerifier: unexpectedOAuth,
      },
      fetch: async (input, init) => {
        requestedUrls.push(String(input));
        return fetch(input, init);
      },
    })), /Copy the current connection details/);
    assert.ok(requestedUrls.length > 0);
    assert.ok(requestedUrls.every((requested) => requested === url.href), "Client must not request OAuth metadata");
    for (let i = 0; i < 2; i++) {
      const client = new Client({ name: "integration-test", version: "1" });
      clients.push(client);
      await client.connect(new StreamableHTTPClientTransport(url, { requestInit: { headers } }));
      assert.deepEqual((await client.listTools()).tools.map((tool) => tool.name), ["execute_command"]);
      const result = await client.callTool({
        name: "execute_command",
        arguments: { executable: process.execPath, arguments: ["-e", "console.log('AI can analyse this output');"] },
      });

      assert.equal(result.isError, false);
      const output = commandResultSchema.parse(result.structuredContent);
      assert.equal(output.exitCode, 0);
      assert.equal(output.stdout, "AI can analyse this output\n");
      const failure = await client.callTool({
        name: "execute_command",
        arguments: { executable: process.execPath, arguments: ["-e", "console.error('failure'); process.exit(3);"] },
      });
      assert.equal(failure.isError, true);
      const failureOutput = commandResultSchema.parse(failure.structuredContent);
      assert.equal(failureOutput.exitCode, 3);
      assert.equal(failureOutput.stderr, "failure\n");
      const timeout = await client.callTool({
        name: "execute_command",
        arguments: { executable: process.execPath, arguments: ["-e", "setInterval(()=>{},1000);"], timeoutMs: 200 },
      });
      assert.equal(timeout.isError, true);
      assert.equal(commandResultSchema.parse(timeout.structuredContent).timedOut, true);
    }
  } finally {
    await Promise.all(clients.map((client) => client.close()));
    server.closeAllConnections();
    await new Promise<void>((resolve, reject) => server.close((error) => error ? reject(error) : resolve()));
  }
});

test("stdio also exposes command execution without HTTP credentials", async () => {
  const client = new Client({ name: "stdio-test", version: "1" });
  const transport = new StdioClientTransport({
    command: process.execPath,
    args: [process.env.WDBGMCP_TEST_ENTRY_POINT ?? fileURLToPath(new URL("./index.js", import.meta.url))],
    env: { ...process.env, WDBGMCP_TRANSPORT: "stdio" },
  });
  try {
    await client.connect(transport);
    const result = await client.callTool({
      name: "execute_command",
      arguments: { executable: process.execPath, arguments: ["-e", "console.log('stdio output');"] },
    });
    assert.equal(result.isError, false);
    assert.equal(commandResultSchema.parse(result.structuredContent).stdout, "stdio output\n");
  } finally {
    await client.close();
  }
});
