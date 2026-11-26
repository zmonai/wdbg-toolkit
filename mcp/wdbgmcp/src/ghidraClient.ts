import { z } from "zod";

const parameterSchema = z.object({
  name: z.string().regex(/^[A-Za-z_][A-Za-z0-9_]*$/),
  type: z.enum(["string", "integer", "number", "boolean", "array", "object", "json", "any"]),
  source: z.enum(["query", "body"]),
  required: z.boolean().default(false),
  description: z.string().optional(),
  default: z.unknown().optional(),
});
const toolSchema = z.object({
  path: z.string().regex(/^\/[A-Za-z_][A-Za-z0-9_]*$/),
  method: z.enum(["GET", "POST"]),
  description: z.string(),
  category: z.string().optional(),
  read_only: z.boolean(),
  destructive: z.boolean().default(false),
  params: z.array(parameterSchema).max(128),
});
const catalogSchema = z.object({ tools: z.array(z.unknown()).max(2000) });
export type GhidraTool = z.infer<typeof toolSchema>;
export type GhidraParameters = Record<string, unknown>;

export function validateBackendUrl(value: string): URL {
  const url = new URL(value);
  if (!["http:", "https:"].includes(url.protocol) ||
      !["127.0.0.1", "[::1]", "localhost"].includes(url.hostname) ||
      url.username || url.password || url.search || url.hash || url.pathname !== "/") {
    throw new Error("Ghidra backend must be a loopback HTTP(S) origin without credentials, a path, query, or fragment.");
  }
  return url;
}

export class GhidraClient {
  readonly baseUrl: URL;
  private readonly token?: string;
  private readonly maxResponseBytes: number;
  private readonly timeoutMs: number;

  constructor(options: { url?: string; token?: string; maxResponseBytes?: number; timeoutMs?: number } = {}) {
    this.baseUrl = validateBackendUrl(options.url ?? process.env.WDBGMCP_GHIDRA_URL ?? "http://127.0.0.1:8089");
    this.token = options.token ?? process.env.WDBGMCP_GHIDRA_TOKEN;
    if (this.token && /[\s\x00-\x1f\x7f]/.test(this.token)) {
      throw new Error("Ghidra bearer token must not contain whitespace/control characters.");
    }
    this.maxResponseBytes = options.maxResponseBytes ?? 1048576;
    this.timeoutMs = options.timeoutMs ?? 60000;
  }

  private async request(path: string, method: string, parameters: URLSearchParams,
    body?: GhidraParameters, signal?: AbortSignal): Promise<unknown> {
    const url = new URL(path, this.baseUrl);
    url.search = parameters.toString();
    const controller = new AbortController();
    const cancel = () => controller.abort(signal?.reason);
    if (signal?.aborted) cancel();
    signal?.addEventListener("abort", cancel, { once: true });
    const timer = setTimeout(() => controller.abort(new Error("Ghidra request timed out; backend work may still be running.")), this.timeoutMs);
    try {
      const headers: Record<string, string> = { Accept: "application/json, text/plain" };
      if (this.token) headers.Authorization = `Bearer ${this.token}`;
      if (body) headers["Content-Type"] = "application/json";
      const response = await fetch(url, {
        method, headers, body: body ? JSON.stringify(body) : undefined,
        signal: controller.signal, redirect: "error",
      });
      const reader = response.body?.getReader();
      const chunks: Uint8Array[] = [];
      let length = 0;
      if (reader) {
        try {
          while (true) {
            const chunk = await reader.read();
            if (chunk.done) break;
            length += chunk.value.length;
            if (length > this.maxResponseBytes) {
              await reader.cancel();
              throw new Error("Ghidra response exceeded 1 MiB/configured limit. Use pagination or a smaller function/range; no complete result was returned.");
            }
            chunks.push(chunk.value);
          }
        } finally {
          reader.releaseLock();
        }
      }
      const text = Buffer.concat(chunks).toString("utf8");
      if (!response.ok) throw new Error(`Ghidra HTTP ${response.status}: ${text.slice(0, 2000)}`);
      let result: unknown = text;
      if (response.headers.get("content-type")?.includes("json") || /^[\s]*[\[{]/.test(text)) {
        try { result = JSON.parse(text); }
        catch { throw new Error("Ghidra returned malformed JSON; no complete result was returned."); }
      }
      if (result && typeof result === "object" && !Array.isArray(result)) {
        const envelope = result as Record<string, unknown>;
        if (envelope.error || envelope.success === false) {
          throw new Error(`Ghidra backend error: ${JSON.stringify(envelope).slice(0, 2000)}`);
        }
      }
      return result;
    } catch (error) {
      if (controller.signal.aborted) {
        throw new Error("Ghidra request cancelled or timed out. Backend work may still be running; inspect its state before retrying.", { cause: error });
      }
      if (error instanceof TypeError && error.message === "fetch failed") {
        throw new Error(`Could not reach Ghidra at ${this.baseUrl.origin}. Start the user-installed backend on the machine running wdbgmcp and check its URL/authentication settings.`, { cause: error });
      }
      throw error;
    } finally {
      clearTimeout(timer);
      signal?.removeEventListener("abort", cancel);
    }
  }

  async catalog(signal?: AbortSignal): Promise<GhidraTool[]> {
    const response = await this.request("/mcp/schema", "GET", new URLSearchParams(), undefined, signal);
    const parsed = catalogSchema.safeParse(response);
    // Entries this client cannot call safely (e.g. nested /server/... paths) are skipped, not fatal.
    const tools = parsed.success
      ? parsed.data.tools.flatMap((entry) => {
        const tool = toolSchema.safeParse(entry);
        return tool.success ? [tool.data] : [];
      })
      : [];
    if (tools.length === 0) {
      throw new Error("Unsupported Ghidra backend schema. Install a compatible backend exposing /mcp/schema with path/method/read_only/params.");
    }
    return tools;
  }

  async call(toolName: string, input: GhidraParameters, signal?: AbortSignal, confirmedImport = false): Promise<unknown> {
    if (!/^[A-Za-z_][A-Za-z0-9_]*$/.test(toolName)) throw new Error("Invalid Ghidra tool name.");
    if (Buffer.byteLength(JSON.stringify(input), "utf8") > 65536) {
      throw new Error("Ghidra parameters exceed the 64 KiB limit.");
    }
    const catalog = await this.catalog(signal);
    const tool = catalog.find((entry) => entry.path === `/${toolName}`);
    if (!tool) throw new Error(`Ghidra tool '${toolName}' is not advertised by this backend.`);
    if (confirmedImport ? toolName !== "import_file" || tool.method !== "POST" || tool.destructive
      : !tool.read_only || tool.destructive) {
      throw new Error("Only non-destructive read-only Ghidra tools are allowed here. Use the explicitly confirmed import tool for imports.");
    }
    if (tool.params.some((param) => param.name === "program") &&
        (typeof input.program !== "string" || !input.program.trim())) {
      throw new Error("Supply an explicit program project path in parameters.program.");
    }
    const query = new URLSearchParams();
    const body: GhidraParameters = {};
    for (const name of Object.keys(input)) {
      if (!tool.params.some((param) => param.name === name)) {
        throw new Error(`Unknown parameter '${name}' for Ghidra ${toolName}. Inspect ghidra_list_tools.`);
      }
    }
    for (const parameter of tool.params) {
      const value = input[parameter.name];
      if (value === undefined) {
        if (parameter.required) throw new Error(`Missing required Ghidra parameter '${parameter.name}'.`);
        continue;
      }
      const valid = parameter.type === "json" || parameter.type === "any" ? true
        : parameter.type === "integer" ? typeof value === "number" && Number.isSafeInteger(value)
        : parameter.type === "number" ? typeof value === "number" && Number.isFinite(value)
        : parameter.type === "array" ? Array.isArray(value)
        : parameter.type === "object" ? value !== null && typeof value === "object" && !Array.isArray(value)
        : typeof value === parameter.type;
      if (!valid || (parameter.required && value === "")) {
        throw new Error(`Invalid value for Ghidra parameter '${parameter.name}' (${parameter.type}).`);
      }
      if (parameter.name === "limit" && (typeof value !== "number" || value < 1 || value > 500)) {
        throw new Error("Ghidra limit must be between 1 and 500; use offset for further pages.");
      }
      if (parameter.name === "offset" && (typeof value !== "number" || value < 0)) {
        throw new Error("Ghidra offset must be nonnegative.");
      }
      if (parameter.source === "query") {
        query.set(parameter.name, typeof value === "object" ? JSON.stringify(value) : String(value));
      } else {
        body[parameter.name] = value;
      }
    }
    if (tool.method === "GET" && Object.keys(body).length > 0) {
      throw new Error("Unsupported Ghidra schema: GET tools cannot accept body parameters.");
    }
    return this.request(tool.path, tool.method, query,
      tool.method === "POST" ? body : undefined, signal);
  }
}
