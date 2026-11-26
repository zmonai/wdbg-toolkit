import assert from "node:assert/strict";
import { test } from "node:test";
import { configureSsh, isClientAddress } from "./sshSetup.js";
import type { CommandInput, CommandResult } from "./commandRunner.js";

test("SSH accepts explicit IP/CIDR scopes and rejects unrestricted or injected input", () => {
  for (const address of ["192.0.2.1", "192.0.2.0/24", "2001:db8::1", "2001:db8::/64"]) {
    assert.equal(isClientAddress(address), true, address);
  }
  for (const address of ["Any", "*", "LocalSubnet", "0.0.0.0", "::", "0.0.0.0/0",
    "192.0.2.1/0", "::/0", "192.0.2.1/33", "2001:db8::/129", "host.example",
    "192.0.2.1;Start-Service sshd", "192.0.2.1/24/1", "fe80::1%3", "127.1"]) {
    assert.equal(isClientAddress(address), false, address);
  }
});

test("SSH mutation requires consent and client scope before invoking commands", async () => {
  const noExecution = async (): Promise<CommandResult> => { throw new Error("Must not execute"); };
  await assert.rejects(configureSsh({ action: "install_enable", allowedClients: ["192.0.2.1"] },
    undefined, noExecution, "win32"), /confirmation/);
  await assert.rejects(configureSsh({ action: "disable" }, undefined, noExecution, "win32"), /confirmation/);
  await assert.rejects(configureSsh({ action: "install_enable", confirmed: true },
    undefined, noExecution, "win32"), /allowedClients/);
  await assert.rejects(configureSsh({ action: "status" }, undefined, noExecution, "linux"), /Windows/);
});

test("SSH passes data literally to the shared script and propagates failure results", async () => {
  const failure: CommandResult = {
    executable: "powershell", arguments: [], stdout: "", stderr: "servicing failed", exitCode: 1,
    signal: null, durationMs: 1, timedOut: false, outputLimitExceeded: false, cancelled: false,
  };
  const controller = new AbortController();
  const runner = async (input: CommandInput, signal?: AbortSignal) => {
    assert.equal(signal, controller.signal);
    assert.match(input.arguments![3], /Configure-Ssh\.ps1$/);
    const config = JSON.parse(Buffer.from(input.arguments![5], "base64").toString("utf8"));
    assert.deepEqual(config, { action: "install_enable", allowedClients: ["192.0.2.1/32"], confirmed: true });
    assert.equal(input.timeoutMs, 300000);
    return failure;
  };
  assert.equal(await configureSsh({ action: "install_enable", allowedClients: ["192.0.2.1/32"], confirmed: true },
    controller.signal, runner, "win32"), failure);
});
