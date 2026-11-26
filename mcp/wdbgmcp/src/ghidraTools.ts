import { access } from "node:fs/promises";
import { isAbsolute, join } from "node:path";
import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { GhidraClient } from "./ghidraClient.js";

const program = z.string().min(1).max(4096)
  .describe("Explicit Ghidra project program path, e.g. /old/client.exe. Required to avoid ambiguous active-program selection.");
const reference = z.string().min(1).max(4096).describe("Function/address reference, including address-space prefix when needed.");
const pagination = { offset: z.number().int().min(0).default(0), limit: z.number().int().min(1).max(500).default(100) };
const readOnly = { readOnlyHint: true, destructiveHint: false, idempotentHint: true, openWorldHint: false };

async function respond(work: () => Promise<unknown>) {
  try {
    const result = await work();
    const structuredContent = { result };
    return { isError: false, content: [{ type: "text" as const, text: JSON.stringify(structuredContent, null, 2) }], structuredContent };
  } catch (error) {
    return { isError: true, content: [{ type: "text" as const, text: error instanceof Error ? error.message : String(error) }] };
  }
}

export function registerGhidraTools(server: McpServer): void {
  // Construct lazily so a bad optional backend setting does not disable unrelated MCP tools.
  const client = () => new GhidraClient();
  server.registerTool("ghidra_status", {
    description: "Verify the user-installed local Ghidra backend and list open programs. Does not install/start Ghidra. Requires compatible Ghidra/JDK/backend; default home C:\\dbg\\ghidra and loopback backend port 8089.",
    inputSchema: {}, annotations: readOnly,
  }, (_, extra) => respond(async () => {
    const home = process.env.WDBGMCP_GHIDRA_HOME ?? "C:\\dbg\\ghidra";
    if (!isAbsolute(home)) throw new Error("WDBGMCP_GHIDRA_HOME must be an absolute path.");
    await access(join(home, "support", "analyzeHeadless.bat"));
    const backend = client();
    const tools = await backend.catalog(extra.signal);
    const programs = await backend.call("list_open_programs", {}, extra.signal);
    return { home, backend: backend.baseUrl.origin, advertisedToolCount: tools.length, programs,
      notice: "Backend reachable; this does not verify JDK compatibility, analysis completeness, or reboot persistence." };
  }));
  server.registerTool("ghidra_list_tools", {
    description: "Discover the installed backend's read-only capabilities and exact parameter schemas. Only non-destructive read-only tools are exposed by the generic call tool.",
    inputSchema: { filter: z.string().max(256).default(""), ...pagination }, annotations: readOnly,
  }, (input, extra) => respond(async () => {
    const tools = (await client().catalog(extra.signal)).filter((tool) =>
      tool.read_only && !tool.destructive &&
      `${tool.path} ${tool.description} ${tool.category ?? ""}`.toLowerCase().includes(input.filter.toLowerCase()));
    return { total: tools.length, offset: input.offset, limit: input.limit, tools: tools.slice(input.offset, input.offset + input.limit) };
  }));
  server.registerTool("ghidra_call_tool", {
    description: "Call another non-destructive read-only feature advertised by ghidra_list_tools (function hashes, byte/instruction searches, symbols, memory reads, etc.). Parameters are validated against the live schema; no arbitrary endpoints/scripts/mutations. Specify program explicitly for any tool accepting it. Responses capped at 1 MiB; use pagination. Request cancellation/timeouts do not guarantee backend cancellation.",
    inputSchema: {
      tool: z.string().regex(/^[A-Za-z_][A-Za-z0-9_]*$/).max(128),
      parameters: z.record(z.unknown()).default({}),
    }, annotations: readOnly,
  }, (input, extra) => respond(() => client().call(input.tool, input.parameters, extra.signal)));
  server.registerTool("ghidra_search_strings", {
    description: "Search defined Ghidra strings using a regex. Returns the backend's matches/warnings and pagination metadata. This searches analyzed strings, not all possible raw-file strings; the backend's encoding parameter is not a filter.",
    inputSchema: { program, pattern: z.string().min(1).max(1024), min_length: z.number().int().min(1).max(4096).default(4), ...pagination },
    annotations: readOnly,
  }, (input, extra) => respond(() => client().call("search_strings", {
    program: input.program, search_term: input.pattern, min_length: input.min_length, offset: input.offset, limit: input.limit,
  }, extra.signal)));
  server.registerTool("ghidra_list_items", {
    description: "Page through program imports, exports, methods/functions, classes, namespaces, segments, data items, or external locations.",
    inputSchema: { program, kind: z.enum(["classes", "methods", "namespaces", "imports", "exports", "segments", "data_items", "external_locations"]), ...pagination },
    annotations: readOnly,
  }, (input, extra) => respond(() => client().call("list_program_items", input, extra.signal)));
  server.registerTool("ghidra_decompile", {
    description: "Get decompiler output for one function. Preserve backend warnings; output is inferred C-like pseudocode, not recovered original C/C++ source. Backend analysis must already be available.",
    inputSchema: { program, function: reference }, annotations: readOnly,
  }, (input, extra) => respond(() => client().call("force_decompile", input, extra.signal)));
  server.registerTool("ghidra_disassemble", {
    description: "Read existing assembly instructions for one function. Does not create instructions or change the project.",
    inputSchema: { program, address: reference }, annotations: readOnly,
  }, (input, extra) => respond(() => client().call("disassemble_function", input, extra.signal)));
  server.registerTool("ghidra_xrefs", {
    description: "Page through cross-references to or from a specific address.",
    inputSchema: { program, address: reference, direction: z.enum(["to", "from"]).default("to"), ...pagination },
    annotations: readOnly,
  }, (input, extra) => respond(() => client().call(`get_xrefs_${input.direction}`, {
    program: input.program, address: input.address, offset: input.offset, limit: input.limit,
  }, extra.signal)));
  server.registerTool("ghidra_import", {
    description: "Explicitly import a file already on the backend machine into its persistent project, optionally starting analysis. Requires owner confirmation; backend file-root restrictions apply. Does not execute the binary. Response is not proof analysis finished. On timeout, inspect open programs before retrying to avoid duplicate imports. Imports are not yet content-addressed/resumable jobs.",
    inputSchema: {
      file_path: z.string().min(1).max(4096).refine(isAbsolute, "Use an absolute path on the backend machine."),
      project_folder: z.string().regex(/^\//).max(4096).default("/"),
      auto_analyze: z.boolean().default(true),
      confirmed: z.boolean().default(false).describe("True only after the machine owner approves importing/parsing this binary."),
    },
    annotations: { readOnlyHint: false, destructiveHint: false, idempotentHint: false, openWorldHint: false },
  }, (input, extra) => respond(() => {
    if (!input.confirmed) throw new Error("Explicit owner confirmation is required before importing a binary.");
    return client().call("import_file", {
      file_path: input.file_path, project_folder: input.project_folder, auto_analyze: input.auto_analyze,
    }, extra.signal, true);
  }));
}
