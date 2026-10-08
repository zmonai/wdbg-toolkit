import assert from "node:assert/strict";
import { test } from "node:test";
import { executeCommand } from "./commandRunner.js";

test("captures stdout, stderr, nonzero status, and literal arguments", async () => {
  const result = await executeCommand({
    executable: process.execPath,
    arguments: ["-e", "console.log(process.argv[1]); console.error('diagnostic'); process.exitCode = 7;", "a b & echo unsafe"],
  });
  assert.equal(result.stdout.trim(), "a b & echo unsafe");
  assert.equal(result.stderr.trim(), "diagnostic");
  assert.equal(result.exitCode, 7);
  assert.equal(result.timedOut, false);
});

test("reports spawn errors and invalid working directory", async () => {
  const missing = await executeCommand({ executable: "wdbgmcp-no-such-command-123456" });
  assert.match(missing.error ?? "", /ENOENT/);
  const invalid = await executeCommand({ executable: process.execPath, workingDirectory: `${process.cwd()}\\no-such-directory-123456` });
  assert.match(invalid.error ?? "", /ENOENT/);
});

test("uses the requested working directory and removes the connection secret", async () => {
  const oldToken = process.env.WDBGMCP_TOKEN;
  process.env.WDBGMCP_TOKEN = "test-secret";
  try {
    const result = await executeCommand({
      executable: process.execPath,
      workingDirectory: process.cwd(),
      arguments: ["-e", "console.log(process.cwd()); console.log(process.env.WDBGMCP_TOKEN ?? 'no-token');"],
    });
    assert.equal(result.exitCode, 0);
    assert.equal(result.stdout.trim(), `${process.cwd()}\nno-token`);
  } finally {
    if (oldToken === undefined) delete process.env.WDBGMCP_TOKEN;
    else process.env.WDBGMCP_TOKEN = oldToken;
  }
});

test("terminates a process tree on timeout", async () => {
  const result = await executeCommand({
    executable: process.execPath,
    arguments: ["-e", "const c=require('node:child_process').spawn(process.execPath,['-e','setInterval(()=>{},1000)'],{stdio:'ignore'}); console.log(c.pid); setInterval(()=>{},1000);"],
    timeoutMs: 1500,
  });
  assert.equal(result.timedOut, true);
  const descendant = Number(result.stdout.trim());
  assert.ok(descendant > 0);
  assert.throws(() => process.kill(descendant, 0));
  assert.ok(result.durationMs < 10000);
});

test("bounds output and terminates commands exceeding the limit", async () => {
  const result = await executeCommand({
    executable: process.execPath,
    arguments: ["-e", "setInterval(()=>{process.stdout.write('x'.repeat(4096));process.stderr.write('y'.repeat(4096));},10);"],
    maxOutputBytes: 1024,
  });
  assert.equal(result.outputLimitExceeded, true);
  assert.equal(Buffer.byteLength(result.stdout) + Buffer.byteLength(result.stderr), 1024);
});

test("does not hang when a parent exits leaving a descendant", async () => {
  const result = await executeCommand({
    executable: process.execPath,
    arguments: ["-e", "const c=require('node:child_process').spawn(process.execPath,['-e','setInterval(()=>{},1000)'],{stdio:['ignore','inherit','inherit']}); process.stdout.write(String(c.pid)+'\\n',()=>process.exit(0));"],
    timeoutMs: 1500,
  });
  const descendant = Number(result.stdout.trim());
  try {
    assert.ok(descendant > 0);
    if (result.timedOut) {
      assert.match(result.error ?? "", /descendant/);
    } else {
      assert.equal(result.exitCode, 0);
    }
    assert.ok(result.durationMs < 10000);
  } finally {
    if (descendant > 0) {
      try {
        process.kill(descendant, "SIGKILL");
      } catch (error) {
        if (!(error instanceof Error && "code" in error && error.code === "ESRCH")) throw error;
      }
    }
  }
});

test("cancels a running command", async () => {
  const abort = new AbortController();
  const pending = executeCommand({ executable: process.execPath, arguments: ["-e", "setInterval(()=>{},1000);"] }, abort.signal);
  setTimeout(() => abort.abort(), 200);
  assert.equal((await pending).cancelled, true);
});

test("rejects invalid limits and relative working directories", async () => {
  await assert.rejects(executeCommand({ executable: process.execPath, timeoutMs: 300001 }));
  await assert.rejects(executeCommand({ executable: process.execPath, workingDirectory: ".." }));
});

test("rejects already cancelled requests and caps concurrent commands", async () => {
  const aborted = new AbortController();
  aborted.abort();
  await assert.rejects(executeCommand({ executable: process.execPath }, aborted.signal), /cancelled/);
  const abort = new AbortController();
  const commands = Array.from({ length: 4 }, () => executeCommand({
    executable: process.execPath, arguments: ["-e", "setInterval(()=>{},1000);"],
  }, abort.signal));
  try {
    await assert.rejects(executeCommand({ executable: process.execPath }), /Four commands/);
  } finally {
    abort.abort();
    await Promise.all(commands);
  }
});
