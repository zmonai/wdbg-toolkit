import assert from "node:assert/strict";
import { test, type TestContext } from "node:test";
import { createServer, type IncomingMessage, type ServerResponse } from "node:http";
import { once } from "node:events";
import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { StreamableHTTPClientTransport } from "@modelcontextprotocol/sdk/client/streamableHttp.js";
import { GhidraClient, validateBackendUrl } from "./ghidraClient.js";
import { createHttpServer } from "./index.js";

const param = (name: string, type = "string", required = false, source = "query") => ({ name, type, source, required });
const program = param("program");
const tools = [
  { path: "/search_strings", method: "GET", read_only: true, destructive: false, description: "Regex search",
    params: [param("search_term", "string", true), param("min_length", "integer"), param("offset", "integer"), param("limit", "integer"), program] },
  { path: "/get_function_hash", method: "GET", read_only: true, description: "Hashes",
    params: [param("limit", "integer"), program] },
  { path: "/force_decompile", method: "GET", read_only: true, description: "Decompile",
    params: [param("function", "string", true), program] },
  { path: "/import_file", method: "POST", read_only: false, description: "Import", params: [
    param("file_path", "string", true, "body"), param("project_folder", "string", false, "body"), param("auto_analyze", "boolean", false, "body")] },
  { path: "/save_all_programs", method: "GET", read_only: false, description: "Save", params: [] },
  { path: "/delete_function", method: "POST", read_only: false, destructive: true, description: "Delete", params: [] },
  { path: "/read_json", method: "POST", read_only: true, description: "Example JSON parameters",
    params: [param("options", "json", true, "body"), param("value", "any", false, "body")] },
];

async function backend(t: TestContext, handler?: (req: IncomingMessage, res: ServerResponse) => void) {
  const requests: { url: string; authorization?: string; body?: unknown }[] = [];
  const server = createServer(async (req, res) => {
    if (handler) return handler(req, res);
    const chunks: Buffer[] = [];
    for await (const chunk of req) chunks.push(Buffer.from(chunk));
    const bodyText = Buffer.concat(chunks).toString();
    requests.push({ url: req.url!, authorization: req.headers.authorization,
      body: bodyText ? JSON.parse(bodyText) : undefined });
    res.setHeader("Content-Type", "application/json");
    if (req.url === "/mcp/schema") return res.end(JSON.stringify({ tools }));
    return res.end(JSON.stringify({ matches: [{ value: "Error", address: "14000e22c" }], warning: "Partial decompilation warning" }));
  });
  server.listen(0, "127.0.0.1");
  await once(server, "listening");
  const address = server.address();
  assert.ok(address && typeof address !== "string");
  t.after(async () => {
    server.closeAllConnections();
    await new Promise<void>((resolve) => server.close(() => resolve()));
  });
  return { url: `http://127.0.0.1:${address.port}`, requests };
}

test("Ghidra restricts backend addresses and rejects URL credentials and redirect paths", () => {
  for (const url of ["http://example.com:8089", "http://127.0.0.1:8089/path", "http://user:pass@localhost",
    "file:///C:/dbg/ghidra", "http://127.0.0.1?next=x", "http://127.0.0.1#x", "http://0.0.0.0:8089"]) {
    assert.throws(() => validateBackendUrl(url));
  }
  assert.equal(validateBackendUrl("http://127.0.0.1:8089").origin, "http://127.0.0.1:8089");
});

test("Ghidra encodes program paths, pagination and regex and preserves backend warnings", async (t) => {
  const api = await backend(t);
  const client = new GhidraClient({ url: api.url, token: "test-token" });
  const result = await client.call("search_strings", { program: "/old/a b.exe", search_term: "error|a&b", limit: 2, offset: 1 });
  assert.deepEqual(result, { matches: [{ value: "Error", address: "14000e22c" }], warning: "Partial decompilation warning" });
  const url = new URL(api.requests[1].url, api.url);
  assert.equal(url.searchParams.get("program"), "/old/a b.exe");
  assert.equal(url.searchParams.get("search_term"), "error|a&b");
  assert.equal(url.searchParams.get("limit"), "2");
  assert.equal(api.requests[1].authorization, "Bearer test-token");
});

test("Ghidra rejects mutations, unsupported tools, unknown parameters and invalid pagination", async (t) => {
  const api = await backend(t);
  const client = new GhidraClient({ url: api.url });
  await assert.rejects(client.call("save_all_programs", {}), /read-only/);
  await assert.rejects(client.call("delete_function", {}), /read-only/);
  await assert.rejects(client.call("../execute_script", {}), /Invalid/);
  await assert.rejects(client.call("missing", {}), /not advertised/);
  await assert.rejects(client.call("search_strings", { program: "/old/a.exe" }), /Missing required/);
  await assert.rejects(client.call("search_strings", { program: "/old/a.exe", search_term: "x", invented: "y" }), /Unknown parameter/);
  await assert.rejects(client.call("get_function_hash", { program: "/old/a.exe", limit: 0 }), /between 1 and 500/);
  await assert.rejects(client.call("search_strings", { program: "/old/a.exe", search_term: "x", offset: -1 }), /nonnegative/);
  assert.ok(api.requests.every((request) => request.url === "/mcp/schema"));
});

test("Ghidra confirmed import uses schema body parameters; generic import is rejected", async (t) => {
  const api = await backend(t);
  const client = new GhidraClient({ url: api.url });
  const parameters = { file_path: "C:\\PE\\a.exe", project_folder: "/old", auto_analyze: false };
  await assert.rejects(client.call("import_file", parameters), /read-only/);
  await client.call("import_file", parameters, undefined, true);
  assert.deepEqual(api.requests.at(-1)?.body, parameters);
  await client.call("read_json", { options: { selected: true }, value: [1, 2] });
  assert.deepEqual(api.requests.at(-1)?.body, { options: { selected: true }, value: [1, 2] });
});

test("Ghidra HTTP errors, JSON errors and unsupported schemas are explicit failures", async (t) => {
  for (const [status, body, expected] of [
    [403, '{"error":"forbidden"}', /HTTP 403/],
    [200, '{"error":"ambiguous program"}', /ambiguous program/],
    [200, '{"tools":[]', /malformed JSON/],
    [200, '{"unsupported":true}', /Unsupported Ghidra backend schema/],
  ] as const) {
    const api = await backend(t, (_, res) => {
      res.writeHead(status, { "Content-Type": "application/json" }).end(body);
    });
    await assert.rejects(new GhidraClient({ url: api.url }).catalog(), expected);
  }
});

test("Ghidra bounds responses and does not follow HTTP redirects", async (t) => {
  const large = await backend(t, (_, res) => res.end("x".repeat(2048)));
  await assert.rejects(new GhidraClient({ url: large.url, maxResponseBytes: 1024 }).catalog(), /exceeded/);
  const redirect = await backend(t, (_, res) => res.writeHead(302, { Location: "http://example.com/" }).end());
  await assert.rejects(new GhidraClient({ url: redirect.url }).catalog());
  const pending = await backend(t, () => {});
  await assert.rejects(new GhidraClient({ url: pending.url, timeoutMs: 30 }).catalog(), /timed out/);
  const controller = new AbortController();
  controller.abort();
  await assert.rejects(new GhidraClient({ url: pending.url }).catalog(controller.signal), /cancelled/);
});

test("Ghidra unavailable backend reports actionable machine-local setup guidance", async () => {
  const server = createServer();
  server.listen(0, "127.0.0.1");
  await once(server, "listening");
  const address = server.address();
  assert.ok(address && typeof address !== "string");
  await new Promise<void>((resolve) => server.close(() => resolve()));
  await assert.rejects(new GhidraClient({ url: `http://127.0.0.1:${address.port}` }).catalog(),
    /Start the user-installed backend on the machine running wdbgmcp/);
});

test("Ghidra MCP tools enforce program selection and import consent over HTTP", async (t) => {
  const api = await backend(t);
  const previous = process.env.WDBGMCP_GHIDRA_URL;
  process.env.WDBGMCP_GHIDRA_URL = api.url;
  t.after(() => {
    if (previous === undefined) delete process.env.WDBGMCP_GHIDRA_URL;
    else process.env.WDBGMCP_GHIDRA_URL = previous;
  });
  const token = "integration-test-token-".repeat(3);
  const server = createHttpServer(token);
  server.listen(0, "127.0.0.1");
  await once(server, "listening");
  const address = server.address();
  assert.ok(address && typeof address !== "string");
  const client = new Client({ name: "ghidra-integration", version: "1" });
  await client.connect(new StreamableHTTPClientTransport(new URL(`http://127.0.0.1:${address.port}/mcp`), {
    requestInit: { headers: { Authorization: `Bearer ${token}` } },
  }));
  t.after(async () => {
    await client.close();
    server.closeAllConnections();
    await new Promise<void>((resolve) => server.close(() => resolve()));
  });
  const result = await client.callTool({ name: "ghidra_search_strings",
    arguments: { program: "/old/a.exe", pattern: "error", limit: 2 } });
  assert.equal(result.isError, false);
  assert.match(JSON.stringify(result.structuredContent), /Partial decompilation warning/);
  const missingProgram = await client.callTool({ name: "ghidra_call_tool",
    arguments: { tool: "get_function_hash", parameters: { limit: 2 } } });
  assert.equal(missingProgram.isError, true);
  const importDenied = await client.callTool({ name: "ghidra_import",
    arguments: { file_path: "C:\\PE\\a.exe" } });
  assert.equal(importDenied.isError, true);
  const listed = await client.callTool({ name: "ghidra_list_tools", arguments: { filter: "search" } });
  assert.equal(listed.isError, false);
  assert.ok(!api.requests.some((request) => request.url === "/import_file"));
});
