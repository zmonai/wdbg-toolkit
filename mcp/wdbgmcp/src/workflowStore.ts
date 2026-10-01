import { readdir, readFile, stat } from "node:fs/promises";
import path from "node:path";
import os from "node:os";

/**
 * Mirrors WdbgToolkit.Workflows.WorkflowRunManifest (manifest.json written by the
 * WPF app's WorkflowRunner). Property names match the C# record's JSON output exactly
 * (System.Text.Json default PascalCase, no naming policy applied).
 */
export interface WorkflowRunManifest {
  ActionId: string;
  ScenarioId: string;
  StartedAt: string;
  Succeeded: boolean;
  Summary: string;
  Data: Record<string, string>;
  Artifacts: string[];
  Error?: string | null;
}

export interface WorkflowRunSummary {
  scenarioId: string;
  runId: string;
  runDirectory: string;
  manifest: WorkflowRunManifest;
}

/**
 * Root directory the WPF app writes workflow runs into:
 * %ProgramData%\WdbgToolkit on Windows. Overridable via WDBGMCP_ROOT for testing or
 * when reading runs captured on a different machine.
 */
export function getRootDirectory(): string {
  if (process.env.WDBGMCP_ROOT) {
    return process.env.WDBGMCP_ROOT;
  }

  const programData = process.env.ProgramData ?? path.join(os.homedir(), "AppData", "Local");
  return path.join(programData, "WdbgToolkit");
}

/** Resolves a path and ensures it stays within `root`, to avoid escaping the workflow root via '..'. */
function resolveWithinRoot(root: string, ...segments: string[]): string {
  const resolvedRoot = path.resolve(root);
  const resolved = path.resolve(resolvedRoot, ...segments);
  if (resolved !== resolvedRoot && !resolved.startsWith(resolvedRoot + path.sep)) {
    throw new Error(`Path escapes the workflow root: ${segments.join(path.sep)}`);
  }

  return resolved;
}

async function listSubdirectories(directory: string): Promise<string[]> {
  try {
    const entries = await readdir(directory, { withFileTypes: true });
    return entries.filter((entry) => entry.isDirectory()).map((entry) => entry.name);
  } catch (error) {
    if ((error as NodeJS.ErrnoException).code === "ENOENT") {
      return [];
    }

    throw error;
  }
}

/** Lists scenario ids that have at least one recorded workflow run. */
export async function listScenarios(root = getRootDirectory()): Promise<string[]> {
  const entries = await listSubdirectories(root);
  // "Scripts" and "CrashDumps" are shared, non-scenario directories used by actions directly.
  return entries.filter((entry) => entry !== "Scripts" && entry !== "CrashDumps");
}

/** Lists runs for a scenario (or all scenarios when omitted), most recent first. */
export async function listRuns(scenarioId?: string, root = getRootDirectory()): Promise<WorkflowRunSummary[]> {
  const scenarioIds = scenarioId ? [scenarioId] : await listScenarios(root);
  const runs: WorkflowRunSummary[] = [];

  for (const scenario of scenarioIds) {
    const scenarioDir = resolveWithinRoot(root, scenario);
    const runIds = await listSubdirectories(scenarioDir);
    for (const runId of runIds) {
      const runDirectory = resolveWithinRoot(root, scenario, runId);
      const manifest = await tryReadManifest(runDirectory);
      if (manifest) {
        runs.push({ scenarioId: scenario, runId, runDirectory, manifest });
      }
    }
  }

  runs.sort((a, b) => (a.manifest.StartedAt < b.manifest.StartedAt ? 1 : -1));
  return runs;
}

async function tryReadManifest(runDirectory: string): Promise<WorkflowRunManifest | null> {
  try {
    const raw = await readFile(path.join(runDirectory, "manifest.json"), "utf-8");
    return JSON.parse(raw) as WorkflowRunManifest;
  } catch (error) {
    if ((error as NodeJS.ErrnoException).code === "ENOENT") {
      return null;
    }

    throw error;
  }
}

/** Reads a single run's manifest.json by scenario + run id. */
export async function getRun(
  scenarioId: string,
  runId: string,
  root = getRootDirectory(),
): Promise<WorkflowRunSummary | null> {
  const runDirectory = resolveWithinRoot(root, scenarioId, runId);
  const manifest = await tryReadManifest(runDirectory);
  return manifest ? { scenarioId, runId, runDirectory, manifest } : null;
}

/** Reads an artifact file's contents as UTF-8 text. `artifactPath` must be absolute and under the run's root. */
export async function readArtifact(artifactPath: string, root = getRootDirectory()): Promise<string> {
  const resolvedRoot = path.resolve(root);
  const resolved = path.resolve(artifactPath);
  if (resolved !== resolvedRoot && !resolved.startsWith(resolvedRoot + path.sep)) {
    throw new Error(`Artifact path is outside the workflow root (${resolvedRoot}): ${artifactPath}`);
  }

  const info = await stat(resolved);
  if (!info.isFile()) {
    throw new Error(`Not a file: ${artifactPath}`);
  }

  return readFile(resolved, "utf-8");
}
