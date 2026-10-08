import { spawn } from "node:child_process";
import { isAbsolute } from "node:path";
import { z } from "zod";

export const commandInput = {
  executable: z.string().min(1).max(4096).refine((value) => !value.includes("\0"))
    .describe("Executable name on PATH or absolute path, e.g. whoami.exe, ipconfig.exe, cdb.exe, or powershell.exe. Not a shell command line."),
  arguments: z.array(z.string().max(32768).refine((value) => !value.includes("\0"))).max(256).default([])
    .describe("Arguments passed literally, without shell expansion. For PowerShell use -NoProfile -NonInteractive -Command followed by the script."),
  workingDirectory: z.string().max(4096).refine((value) => isAbsolute(value) && !value.includes("\0"))
    .optional().describe("Optional absolute working directory on the server machine."),
  timeoutMs: z.number().int().min(100).max(300000).default(30000),
  maxOutputBytes: z.number().int().min(1024).max(1048576).default(262144)
    .describe("Combined stdout/stderr byte limit. Exceeding it terminates the command and reports outputLimitExceeded."),
};

const commandSchema = z.object(commandInput);
export type CommandInput = z.input<typeof commandSchema>;

export interface CommandResult {
  executable: string;
  arguments: string[];
  stdout: string;
  stderr: string;
  exitCode: number | null;
  signal: string | null;
  durationMs: number;
  timedOut: boolean;
  outputLimitExceeded: boolean;
  cancelled: boolean;
  error?: string;
}

let activeCommands = 0;

export async function executeCommand(input: CommandInput, signal?: AbortSignal): Promise<CommandResult> {
  const options = commandSchema.parse(input);
  if (activeCommands >= 4) {
    throw new Error("Four commands are already running. Wait for one to finish before retrying.");
  }
  if (signal?.aborted) {
    throw new Error("Command request was cancelled before execution.");
  }
  activeCommands++;
  try {
    return await new Promise<CommandResult>((resolve) => {
      const started = Date.now();
      // Do not pass the HTTP credential to AI-launched commands.
      const environment = { ...process.env };
      delete environment.WDBGMCP_TOKEN;
      const child = spawn(options.executable, options.arguments, {
        cwd: options.workingDirectory,
        env: environment,
        shell: false,
        windowsHide: true,
        detached: process.platform !== "win32",
        stdio: ["ignore", "pipe", "pipe"],
      });
      const stdout: Buffer[] = [];
      const stderr: Buffer[] = [];
      let outputBytes = 0;
      let timedOut = false;
      let outputLimitExceeded = false;
      let cancelled = false;
      let error: string | undefined;
      let stopping = false;
      let terminationTimer: NodeJS.Timeout | undefined;
      let completed = false;

      const stop = () => {
        if (stopping || !child.pid) return;
        stopping = true;
        // A detached descendant can keep pipes open even after the parent exits.
        // Do not hang the MCP request if process-tree termination cannot close them.
        terminationTimer = setTimeout(() => {
          error = "Process streams did not close after termination; descendant processes may still be running.";
          child.stdout.destroy();
          child.stderr.destroy();
          finish(child.exitCode, child.signalCode);
        }, 5000);
        if (process.platform === "win32" && (child.exitCode !== null || child.signalCode !== null)) {
          error = "Command already exited with open descendant pipes; cannot terminate its tree safely.";
          return;
        }
        if (process.platform === "win32") {
          const killer = spawn(`${process.env.SystemRoot ?? "C:\\Windows"}\\System32\\taskkill.exe`,
            ["/PID", String(child.pid), "/T", "/F"], { windowsHide: true, stdio: "ignore" });
          killer.on("error", (failure) => {
            error = `Could not terminate process tree: ${failure.message}`;
            child.kill();
          });
          killer.on("exit", (code) => {
            if (code !== 0 && child.exitCode === null) {
              error = `Process-tree termination failed (taskkill exit code ${code}).`;
              child.kill();
            }
          });
        } else {
          try {
            process.kill(-child.pid, "SIGKILL");
          } catch (failure) {
            if (!(failure instanceof Error && "code" in failure && failure.code === "ESRCH")) {
              error = `Could not terminate process group: ${String(failure)}`;
              child.kill("SIGKILL");
            }
          }
        }
      };
      const capture = (chunks: Buffer[], chunk: Buffer) => {
        const remaining = options.maxOutputBytes - outputBytes;
        if (remaining > 0) chunks.push(chunk.subarray(0, remaining));
        outputBytes += Math.min(chunk.length, remaining);
        if (chunk.length > remaining) {
          outputLimitExceeded = true;
          stop();
        }
      };
      child.stdout.on("data", (chunk: Buffer) => capture(stdout, chunk));
      child.stderr.on("data", (chunk: Buffer) => capture(stderr, chunk));
      child.on("error", (failure) => { error = failure.message; });
      const timer = setTimeout(() => { timedOut = true; stop(); }, options.timeoutMs);
      const cancel = () => { cancelled = true; stop(); };
      signal?.addEventListener("abort", cancel, { once: true });
      const finish = (exitCode: number | null, exitSignal: string | null) => {
        if (completed) return;
        completed = true;
        clearTimeout(timer);
        clearTimeout(terminationTimer);
        signal?.removeEventListener("abort", cancel);
        resolve({
          executable: options.executable,
          arguments: options.arguments,
          stdout: Buffer.concat(stdout).toString("utf8"),
          stderr: Buffer.concat(stderr).toString("utf8"),
          exitCode,
          signal: exitSignal,
          durationMs: Date.now() - started,
          timedOut,
          outputLimitExceeded,
          cancelled,
          ...(error ? { error } : {}),
        });
      };
      child.on("close", finish);
    });
  } finally {
    activeCommands--;
  }
}
