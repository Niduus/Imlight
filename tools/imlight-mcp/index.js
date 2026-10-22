#!/usr/bin/env node
// Imlight MCP server: lets an AI agent build, run and test the server, read its logs and ids,
// and run in-game commands as a logged-in player through the Director's agent bridge.
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { StdioServerTransport } from "@modelcontextprotocol/sdk/server/stdio.js";
import { z } from "zod";
import { spawn, execFile } from "node:child_process";
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { registerFullControl } from "./full-control.js";

const here = path.dirname(fileURLToPath(import.meta.url));
const ROOT = path.resolve(process.env.IMLIGHT_ROOT || path.join(here, "..", ".."));
const DIRECTOR_PROJ = path.join(ROOT, "src", "Imlight.Director", "Imlight.Director.csproj");
const INI = path.join(ROOT, "src", "Imlight.Director", "Config", "Imlight.ini");
const LOG_DIRS = [path.join(ROOT, "logs"), path.join(ROOT, "src", "Imlight.Director", "logs")];
const SERVER_OUT = path.join(ROOT, "logs", "mcp-director-output.txt");
const MAX_TEXT = 30000;

// Long output keeps its head by default (files, searches) or its tail (logs, builds); the cut is marked.
const text = (t, { tail = false } = {}) => {
  const s = String(t);
  const cut = s.length > MAX_TEXT;
  const body = !cut ? s : tail ? `…(first ${s.length - MAX_TEXT} chars cut)
${s.slice(-MAX_TEXT)}` : `${s.slice(0, MAX_TEXT)}
…(${s.length - MAX_TEXT} more chars cut)`;
  return { content: [{ type: "text", text: body || "(no output)" }] };
};
const fail = (t) => ({ isError: true, content: [{ type: "text", text: String(t) }] });
const json = (o) => text(JSON.stringify(o, null, 2));

// ---------- ini / bridge ----------
function readIni() {
  const sections = {};
  let cur = null;
  for (const raw of fs.readFileSync(INI, "utf8").split(/\r?\n/)) {
    const line = raw.trim();
    if (!line || line.startsWith(";")) continue;
    const m = line.match(/^\[(.+)\]$/);
    if (m) { cur = sections[m[1]] = {}; continue; }
    const i = line.indexOf("=");
    if (i > 0 && cur) cur[line.slice(0, i).trim()] = line.slice(i + 1).trim();
  }
  return sections;
}

function bridgeConfig() {
  const ini = readIni()["Agent Bridge"] || {};
  const url = process.env.IMLIGHT_BRIDGE_URL || `http://127.0.0.1:${ini.Port || 12400}`;
  // The token goes over plain HTTP, so only this PC may be the bridge.
  const host = new URL(url).hostname;
  if (!["127.0.0.1", "localhost", "[::1]", "::1"].includes(host)) {
    throw new Error(`IMLIGHT_BRIDGE_URL must point at this PC (127.0.0.1 or localhost), not ${host}.`);
  }
  return {
    url,
    token: process.env.IMLIGHT_BRIDGE_TOKEN || ini.Token || "",
  };
}

async function bridge(route, body, timeoutMs = 30000) {
  const { url, token } = bridgeConfig();
  if (!token) throw new Error("No agent bridge Token. Set [Agent Bridge] Token in Imlight.ini (and Enabled = true), then restart the server.");
  let res;
  try {
    res = await fetch(url + route, {
      method: body ? "POST" : "GET",
      headers: { "X-Agent-Token": token, "Content-Type": "application/json" },
      body: body ? JSON.stringify(body) : undefined,
      signal: AbortSignal.timeout(timeoutMs),
    });
  } catch (e) {
    if (e?.name === "TimeoutError") throw new Error(`The server did not answer ${route} within ${Math.round(timeoutMs / 1000)}s. It is still reachable if /health answers; a long script keeps running on the server (use background for long work).`);
    throw new Error(`Cannot reach the agent bridge at ${url}. The server is not running, or it was started before the bridge was enabled.`);
  }
  let data;
  try { data = await res.json(); } catch { throw new Error(`Bridge answered ${res.status} with a body that is not JSON.`); }
  if (res.status === 401) throw new Error("The bridge refused the token. IMLIGHT_BRIDGE_TOKEN or [Agent Bridge] Token does not match the running server (restart it after changing the ini).");
  if (!res.ok) throw new Error(data.error || `Bridge answered ${res.status}`);
  return data;
}

// ---------- processes ----------
function run(cmd, args, { cwd = ROOT, timeoutMs = 600000 } = {}) {
  return new Promise((resolve) => {
    execFile(cmd, args, { cwd, timeout: timeoutMs, maxBuffer: 64 * 1024 * 1024, windowsHide: true }, (err, stdout, stderr) => {
      resolve({ code: err ? (typeof err.code === "number" ? err.code : 1) : 0, out: `${stdout}${stderr}`, timedOut: !!err?.killed });
    });
  });
}

const NOISE = /CS8632|CS9107|AK1004|SYSLIB|CS0618/;
const KEEP = /error|warning MSB|Build succeeded|Passed!|Failed!|Failed |Total tests|Skipped|Error\(s\)|Time Elapsed/i;
const trimBuild = (out) => out.split(/\r?\n/).filter((l) => KEEP.test(l) && !NOISE.test(l)).join("\n");

let serverProc = null;

async function bridgeUp() {
  try { await bridge("/health"); return true; } catch { return false; }
}

function newestLog() {
  const files = LOG_DIRS.filter(fs.existsSync).flatMap((d) => fs.readdirSync(d).filter((f) => /^log.*\.txt$/.test(f)).map((f) => path.join(d, f)));
  return files.sort((a, b) => fs.statSync(b).mtimeMs - fs.statSync(a).mtimeMs)[0];
}

function tailLines(file, n, filter) {
  const size = fs.statSync(file).size;
  const len = Math.min(size, 4 * 1024 * 1024);
  const fd = fs.openSync(file, "r");
  const buf = Buffer.alloc(len);
  fs.readSync(fd, buf, 0, len, size - len);
  fs.closeSync(fd);
  let lines = buf.toString("utf8").split(/\r?\n/);
  if (filter) { const re = new RegExp(filter, "i"); lines = lines.filter((l) => re.test(l)); }
  return lines.slice(-n).join("\n");
}

// ---------- id lookup ----------
function searchCsv(file, query, limit) {
  const f = path.join(ROOT, "id-lookup", file);
  if (!fs.existsSync(f)) return [];
  const q = query.toLowerCase();
  const out = [];
  const lines = fs.readFileSync(f, "utf8").split(/\r?\n/);
  for (const l of lines.slice(1)) {
    if (l.toLowerCase().includes(q)) { out.push(l.length > 300 ? l.slice(0, 300) + "…" : l); if (out.length >= limit) break; }
  }
  return [lines[0], ...out];
}

// ---------- tools ----------
const server = new McpServer({ name: "imlight", version: "1.0.0" });

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

server.tool("server_status", "Is the Imlight Director running, is the agent bridge up, and who is logged in? Also uptime, pid and the newest error count.", {}, async () => {
  const up = await bridgeUp();
  let players = [], info = null;
  if (up) {
    try { players = (await bridge("/players")).players; } catch (e) { players = `(could not list: ${e.message})`; }
    try { const i = await bridge("/info"); info = { pid: i.pid, uptimeSeconds: i.uptimeSeconds, workingSetMb: i.workingSetMb }; } catch { /* optional */ }
  }
  return json({ managedByMcp: !!serverProc && serverProc.exitCode === null, bridgeUp: up, ...info, players, bridgeUrl: bridgeConfig().url });
});

// Starts the Director detached, so it keeps running when this MCP server is restarted.
async function startServer({ release, waitSeconds, build }) {
  if (await bridgeUp()) return text("The server is already running.");
  if (serverProc && serverProc.exitCode === null) return text("A server start is already in progress; use server_logs to watch it.");
  fs.mkdirSync(path.dirname(SERVER_OUT), { recursive: true });
  const out = fs.openSync(SERVER_OUT, "w");
  const args = ["run", "--project", DIRECTOR_PROJ, "--configuration", release ? "Release" : "Debug", "--no-launch-profile"];
  if (!build) args.push("--no-build");
  serverProc = spawn("dotnet", args, {
    cwd: ROOT, stdio: ["ignore", out, out], windowsHide: true, detached: true,
    env: { ...process.env, IMLIGHT_DATABASE_DATA_DIRECTORY: path.join(ROOT, "Server", "ImlightEmbeddedDatabase") },
  });
  serverProc.unref();
  const start = Date.now();
  while (Date.now() - start < waitSeconds * 1000) {
    if (serverProc.exitCode !== null) return fail(`The server exited with code ${serverProc.exitCode}.\n${tailLines(SERVER_OUT, 40)}`);
    if (await bridgeUp()) return text(`The server is up (pid ${serverProc.pid}) after ${Math.round((Date.now() - start) / 1000)}s.`);
    await sleep(2000);
  }
  return text(`Still starting after ${waitSeconds}s (pid ${serverProc.pid}). Use server_logs to watch it.\n${tailLines(SERVER_OUT, 15)}`);
}

// Asks the server to exit and waits until it has (the bridge stops answering).
async function stopServer(waitSeconds = 60) {
  if (!(await bridgeUp())) return "The server was not running.";
  try { await bridge("/shutdown", {}, 10000); } catch { /* it may drop the connection while exiting */ }
  const start = Date.now();
  while (Date.now() - start < waitSeconds * 1000) {
    if (!(await bridgeUp())) { serverProc = null; await sleep(1500); return `The server stopped after ${Math.round((Date.now() - start) / 1000)}s.`; }
    await sleep(500);
  }
  throw new Error(`The server was asked to exit but still answers after ${waitSeconds}s.`);
}

server.tool("server_start", "Start the Director (login, game and patch servers) in the background. Returns once the agent bridge answers, or after the timeout. It builds first unless build is false.",
  { release: z.boolean().default(true).describe("Run the Release configuration"), waitSeconds: z.number().int().min(10).max(900).default(300), build: z.boolean().default(true).describe("Build before running (needed after code changes)") },
  async (a) => startServer(a));

server.tool("server_stop", "Stop the running server, whoever started it (asks it to exit through the bridge and waits for it to be gone).",
  { waitSeconds: z.number().int().min(5).max(300).default(60) },
  async ({ waitSeconds }) => {
    try { return text(await stopServer(waitSeconds)); } catch (e) { return fail(e.message); }
  });

server.tool("server_restart",
  "Stop the server, optionally rebuild, and start it again; returns once the bridge answers. Players are disconnected (they must log in again). Use build:false to skip the compile.",
  { build: z.boolean().default(true), release: z.boolean().default(true), waitSeconds: z.number().int().min(30).max(900).default(420) },
  async ({ build, release, waitSeconds }) => {
    const steps = [];
    try {
      steps.push(await stopServer());
      if (build) {
        const r = await run("dotnet", ["build", "src/Imlight.Director/Imlight.Director.csproj", "-c", release ? "Release" : "Debug", "--nologo"]);
        steps.push(`build exit ${r.code}\n${trimBuild(r.out)}`);
        if (r.code) return fail(steps.join("\n"));
      }
      const started = await startServer({ release, waitSeconds, build: false });
      steps.push(started.content[0].text);
      return (started.isError ? fail : text)(steps.join("\n"));
    } catch (e) {
      return fail(`${steps.join("\n")}\n${e.message}`);
    }
  });

server.tool("server_logs", "Read the end of the newest server log, optionally filtered by a regex (case-insensitive, e.g. 'ERR|FATAL') and/or limited to the last N minutes.",
  { lines: z.number().int().min(1).max(1000).default(100), filter: z.string().optional(), sinceMinutes: z.number().min(0.1).max(100000).optional() },
  async ({ lines, filter, sinceMinutes }) => {
    const f = newestLog();
    if (!f) return fail("No log file found.");
    let body = tailLines(f, sinceMinutes ? 1000000 : lines, filter);
    if (sinceMinutes) {
      const cutoff = Date.now() - sinceMinutes * 60000;
      body = body.split("\n").filter((l) => { const m = l.match(/^(\d{4}-\d\d-\d\d \d\d:\d\d:\d\d\.\d+)/); return !m || new Date(m[1].replace(" ", "T")).getTime() >= cutoff; }).slice(-lines).join("\n");
    }
    return text(`${f}\n${body}`, { tail: true });
  });

// MSBuild retries a locked file ten times and prints every try; show the cause once instead.
function summarizeBuild(out) {
  const locked = /MSB3027|MSB3021|MSB3026/.test(out);
  const lines = trimBuild(out).split(/\r?\n/).filter((l) => !/MSB3026/.test(l));
  const hint = locked ? "\nThe build could not copy over files the running server holds. Stop the server first (server_stop / server_restart), or use checkOnly to compile without copying." : "";
  return lines.join("\n") + hint;
}

server.tool("dotnet_build", "Build the solution (or one project) and return errors and the summary. The copy step fails while the server runs; checkOnly compiles into a scratch folder so it works anyway.",
  { project: z.string().optional().describe("Path relative to the repo, default src/Imlight.sln"), release: z.boolean().default(true), checkOnly: z.boolean().default(false).describe("Compile into a scratch folder instead of the real output (project builds only; default project is the Director)") },
  async ({ project, release, checkOnly }) => {
    const args = ["build", checkOnly ? (project || "src/Imlight.Director/Imlight.Director.csproj") : (project || "src/Imlight.sln"), "-c", release ? "Release" : "Debug", "--nologo"];
    if (checkOnly) args.push("-o", path.join(ROOT, "logs", "mcp-build-check"));
    const r = await run("dotnet", args);
    return (r.code ? fail : text)(`exit ${r.code}\n${summarizeBuild(r.out)}`);
  });

server.tool("dotnet_test", "Run the unit tests (test/Imlight.CoreLib.Tests). The optional filter is a dotnet test --filter expression.",
  { filter: z.string().optional() },
  async ({ filter }) => {
    const args = ["test", "test/Imlight.CoreLib.Tests", "--nologo"];
    if (filter) args.push("--filter", filter);
    const r = await run("dotnet", args);
    return (r.code ? fail : text)(`exit ${r.code}\n${trimBuild(r.out)}`);
  });

server.tool("game_players", "List the players currently logged in (character, account, zone, level). Commands need one of them.", {}, async () => {
  try { return json(await bridge("/players")); } catch (e) { return fail(e.message); }
});

server.tool("game_command", "Run an in-game command (what a player types after the dot, e.g. 'level 50') as a logged-in player and return what the server replied. It runs with that player's rights.",
  { command: z.string().min(1), player: z.string().optional().describe("Part of the character or account name; optional when exactly one player is online"), waitMs: z.number().int().min(100).max(15000).default(1500).describe("How long to listen for replies") },
  async ({ command, player, waitMs }) => {
    try {
      const r = await bridge("/command", { command, player, waitMs }, waitMs + 20000);
      // The bridge answers "no such player" with 200 and an error field; that is a failure for the caller.
      if (r.error) return fail(r.error + (r.players ? "\n" + JSON.stringify(r.players) : ""));
      if (!r.replies?.length) r.note = "The server sent no reply (many commands, like heal, reply only when something goes wrong).";
      return json(r);
    } catch (e) { return fail(e.message); }
  });

server.tool("game_commands_run", "Run several in-game commands one after another as the same player, e.g. ['level max', 'mod heal', 'teleport WC_Hub']. Stops at the first bridge error and returns every reply so far.",
  { commands: z.array(z.string().min(1)).min(1).max(40), player: z.string().optional(), waitMs: z.number().int().min(100).max(15000).default(1200).describe("Listening time per command"), pauseMs: z.number().int().min(0).max(20000).default(0).describe("Extra pause after each command (e.g. for a zone change)") },
  async ({ commands, player, waitMs, pauseMs }) => {
    const results = [];
    for (const command of commands) {
      try {
        const r = await bridge("/command", { command, player, waitMs }, waitMs + 20000);
        if (r.error) throw new Error(r.error);
        results.push({ command, replies: r.replies });
      } catch (e) {
        results.push({ command, error: e.message });
        return fail(JSON.stringify(results, null, 2));
      }
      if (pauseMs) await sleep(pauseMs);
    }
    return json(results);
  });

server.tool("game_commands_help", "Show the documented in-game commands (docs/docs/modules/imlight/commands.md), optionally only lines containing a word.",
  { search: z.string().optional() },
  async ({ search }) => {
    const body = fs.readFileSync(path.join(ROOT, "docs", "docs", "modules", "imlight", "commands.md"), "utf8");
    if (!search) return text(body);
    const q = search.toLowerCase();
    return text(body.split(/\r?\n/).filter((l) => l.toLowerCase().includes(q)).join("\n"));
  });

server.tool("id_lookup", "Search the id-lookup tables: ids (items), creatures, quests, bundles, phrases, zones.",
  { kind: z.enum(["ids", "creatures", "quests", "bundles", "phrases", "zones"]), query: z.string().min(1), limit: z.number().int().min(1).max(100).default(25) },
  async ({ kind, query, limit }) => {
    if (kind === "zones") {
      const f = path.join(ROOT, "id-lookup", "zones.txt");
      return text(fs.readFileSync(f, "utf8").split(/\r?\n/).filter((l) => l.toLowerCase().includes(query.toLowerCase())).slice(0, limit).join("\n"));
    }
    return text(searchCsv(`${kind}.csv`, query, limit).join("\n"));
  });

server.tool("config_get", "Read Imlight.ini (the bridge token is hidden), optionally one section.", { section: z.string().optional() }, async ({ section }) => {
  const ini = readIni();
  if (ini["Agent Bridge"]?.Token) ini["Agent Bridge"].Token = "(hidden)";
  if (section && !ini[section]) return fail(`No section ${section}`);
  return json(section ? ini[section] : ini);
});

server.tool("git_status", "Show the branch, short status and recent commits of the repo.", {}, async () => {
  const a = await run("git", ["status", "-sb"]);
  const b = await run("git", ["log", "--oneline", "-10"]);
  return text(`${a.out}\n${b.out}`);
});

registerFullControl(server, { ROOT, text, fail, json, run, bridge, newestLog });

await server.connect(new StdioServerTransport());
