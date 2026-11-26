import { isIP } from "node:net";
import { fileURLToPath } from "node:url";
import { z } from "zod";
import { executeCommand, type CommandInput, type CommandResult } from "./commandRunner.js";

export function isClientAddress(value: string): boolean {
  const parts = value.split("/");
  if (parts.length > 2 || parts[0].includes("%")) return false;
  const family = isIP(parts[0]);
  if (!family || parts[0] === "0.0.0.0" || parts[0] === "::") return false;
  if (parts.length === 1) return true;
  return /^\d+$/.test(parts[1]) && Number(parts[1]) > 0 &&
    Number(parts[1]) <= (family === 4 ? 32 : 128);
}

export const sshInput = {
  action: z.enum(["status", "install_enable", "disable"]).default("status"),
  allowedClients: z.array(z.string().max(64).refine(isClientAddress, "Use explicit IP addresses or CIDR prefixes greater than zero."))
    .max(64).default([]).describe("Required for install_enable. No wildcard, hostname, or unrestricted subnet."),
  confirmed: z.boolean().default(false).describe("Set true only after the machine owner explicitly approves installation, automatic sshd startup, and scoped TCP 22 firewall changes."),
};
const schema = z.object(sshInput);
type Runner = (input: CommandInput, signal?: AbortSignal) => Promise<CommandResult>;

export async function configureSsh(
  input: z.input<typeof schema>,
  signal?: AbortSignal,
  runner: Runner = executeCommand,
  platform: string = process.platform,
): Promise<CommandResult> {
  const options = schema.parse(input);
  if (platform !== "win32") throw new Error("SSH setup is supported only on Windows.");
  if (options.action !== "status" && !options.confirmed) {
    throw new Error("Explicit machine-owner confirmation is required before changing SSH.");
  }
  if (options.action === "install_enable" && options.allowedClients.length === 0) {
    throw new Error("Supply allowedClients before enabling SSH; there is no default network scope.");
  }
  const script = fileURLToPath(new URL("../scripts/Configure-Ssh.ps1", import.meta.url));
  return runner({
    executable: `${process.env.SystemRoot ?? "C:\\Windows"}\\System32\\WindowsPowerShell\\v1.0\\powershell.exe`,
    arguments: ["-NoProfile", "-NonInteractive", "-File", script, "-ConfigBase64",
      Buffer.from(JSON.stringify(options), "utf8").toString("base64")],
    timeoutMs: 300000,
    maxOutputBytes: 65536,
  }, signal);
}
