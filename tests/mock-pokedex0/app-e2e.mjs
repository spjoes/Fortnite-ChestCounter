// End-to-end run of the real counter against the mock Pokédex0, with real replays, never touching %APPDATA%\ChestCounter.
//
//   node tests/mock-pokedex0/app-e2e.mjs <ChestCounter.exe> <folder with at least 5 .replay files>
//
// The counter runs without its window (CHESTCOUNTER_GUI_URL_FILE: the address of its page is written to a file) in a
// temporary data folder (--data) and a temporary replays folder (--demos) where the replays are copied one by one.
// CHESTCOUNTER_POKEDEX0_AUTHORIZE_FILE makes it write the authorization address instead of opening the browser:
// this script plays the browser (authorize page of the mock -> redirect to the counter's /oauth/callback).
import { spawn } from "node:child_process";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { fileURLToPath } from "node:url";

const [exe, replaysDir] = process.argv.slice(2);
if (!exe || !replaysDir) {
  console.error("usage: node app-e2e.mjs <ChestCounter.exe> <replays folder>");
  process.exit(2);
}
const here = path.dirname(fileURLToPath(import.meta.url));
const SOLO = "0b8f3c1e-6a43-4d6e-9a51-2c7d0e5f9a10";
const replays = fs.readdirSync(replaysDir).filter((f) => f.endsWith(".replay")).sort().map((f) => path.join(replaysDir, f));
if (replays.length < 5) throw new Error("need 5 replays");

const tmp = fs.mkdtempSync(path.join(os.tmpdir(), "cc-e2e-"));
const data = path.join(tmp, "data");
const demos = path.join(tmp, "demos");
fs.mkdirSync(data);
fs.mkdirSync(demos);
const urlFile = path.join(tmp, "gui-url.txt");
const authFile = path.join(tmp, "authorize-url.txt");
// No overlay window, no update check, English: a quiet headless run.
fs.writeFileSync(path.join(data, "config.json"), JSON.stringify({ Language: "en", ShowOverlay: false, UpdateMode: "off", DemosFolder: demos, LastVersion: "9.9.9" }));

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const steps = [];
const ok = (what) => { steps.push(what); console.log("  ✔ " + what); };
function check(cond, what, detail) {
  if (!cond) throw new Error(`FAILED: ${what}${detail !== undefined ? " — " + JSON.stringify(detail) : ""}`);
  ok(what);
}
async function until(what, fn, ms = 180_000) {
  const end = Date.now() + ms;
  let last;
  while (Date.now() < end) {
    try { last = await fn(); if (last) return last; } catch (e) { last = e.message; }
    await sleep(500);
  }
  throw new Error(`TIMEOUT: ${what} (last: ${JSON.stringify(last)?.slice(0, 400)})`);
}

// ---- Mock Pokédex0 ----------------------------------------------------------------------------------------------
const mock = spawn(process.execPath, [path.join(here, "server.mjs"), "0"], { stdio: ["ignore", "pipe", "inherit"] });
const port = await new Promise((resolve) => mock.stdout.once("data", (d) => resolve(String(d).trim().split(" ")[1])));
const P0 = `http://127.0.0.1:${port}`;
const admin = async (name, body) => (await fetch(`${P0}/__admin/${name}`, { method: "POST", body: body ? JSON.stringify(body) : "" })).json();
const count = async (id = SOLO) => (await admin("state")).hunts[id].count;
console.log(`mock Pokédex0 on ${P0}, data in ${tmp}`);

// ---- The counter --------------------------------------------------------------------------------------------------
let app;
let gui;
let token;
async function startApp() {
  try { fs.unlinkSync(urlFile); } catch {}
  app = spawn(exe, ["--data", data, "--demos", demos, "--lang", "en"], {
    env: { ...process.env, CHESTCOUNTER_GUI_URL_FILE: urlFile, CHESTCOUNTER_POKEDEX0_URL: P0, CHESTCOUNTER_POKEDEX0_AUTHORIZE_FILE: authFile },
    stdio: "ignore",
  });
  const url = new URL(await until("GUI address", () => fs.existsSync(urlFile) && fs.readFileSync(urlFile, "utf8")));
  gui = url.origin;
  token = url.searchParams.get("t");
}
const state = async () => (await fetch(`${gui}/api/state`, { headers: { "X-Token": token } })).json();
const action = async (name, value) => (await (await fetch(`${gui}/api/action`, { method: "POST", headers: { "X-Token": token, "Content-Type": "application/json" }, body: JSON.stringify({ action: name, value }) })).json()).result;
const num = (s) => Number(String(s).replace(/[^\d-]/g, ""));
const addReplay = (i) => fs.copyFileSync(replays[i], path.join(demos, path.basename(replays[i])));
const matches = async () => num((await state()).matches);

async function stopApp() {
  try { await action("quit"); } catch {}
  await until("counter quit", () => app.exitCode !== null, 30_000);
}

let failed = false;
try {
  // 1. Two matches counted before connecting.
  addReplay(0);
  addReplay(1);
  await startApp();
  const counted = await until("2 replays analyzed", async () => { const s = await state(); return !s.progress && s.status.kind === "" && num(s.matches) >= 1 && s; });
  const before = num(counted.matches);
  check(before >= 1, `counter running headless, ${before} match(es) counted from the first replays (total ${counted.total})`);
  check(counted.pokedex0 && !counted.pokedex0.connected, "not connected: no Pokédex0 state beyond the card");
  check((await admin("state")).stats.authorizeRequests === 0, "no request to Pokédex0 before connecting");

  // 2. Connect through the "browser".
  check(!(await action("pokedex0Connect")), "Connect action accepted");
  const authorizeUrl = await until("authorize address written", () => fs.existsSync(authFile) && fs.readFileSync(authFile, "utf8"));
  const auth = await fetch(authorizeUrl, { redirect: "manual" });
  const callback = auth.headers.get("location");
  check(auth.status === 302 && callback.startsWith(`${gui.replace("localhost", "127.0.0.1")}/oauth/callback?code=`), "mock authorize redirects to the counter's /oauth/callback", callback?.slice(0, 60));
  const page = await (await fetch(callback)).text();
  check(page.includes("Connected to Pok"), "callback page says connected");
  const code = new URL(callback).searchParams.get("code");
  check(!page.includes(code), "callback page does not echo the code");
  const replay = await (await fetch(callback)).text();
  check(replay.includes("Link expired"), "the same callback again is refused (single-use state)");
  const connected = await until("connected + hunts", async () => { const s = await state(); return s.pokedex0.connected && s.pokedex0.huntsLoaded && s; });
  check(connected.pokedex0.user === "Ash <Ketchum> (@ash)", "connected as the mock user (raw text in JSON, escaped by the page)", connected.pokedex0.user);
  check(connected.pokedex0.hunts.length === 3, "3 active hunts offered (completed one hidden)", connected.pokedex0.hunts.map((h) => h.label));

  // 3. Link the solo hunt: baseline, nothing sent.
  const bad = await action("pokedex0Hunt", "'); alert(1); ('");
  check(bad && bad.error, "forged hunt id answered with an error");
  check(!(await action("pokedex0Hunt", SOLO)), "solo hunt chosen");
  const linked = await state();
  check(linked.pokedex0.hunt.id === SOLO, "hunt linked");
  await sleep(1500);
  check((await count()) === 1287, "baseline: existing matches NOT sent (1287)");
  const baselineChests = linked.pokedex0.initial ? linked.pokedex0.initial.chests : 0;
  check(baselineChests === num(linked.total), `one-time sync offers exactly the counted chests (${baselineChests}) = total shown`, linked.pokedex0.initial);

  // 4. A new match: its chests are added.
  const totalBefore = num(linked.total);
  addReplay(2);
  const after3 = await until("3rd replay counted", async () => { const s = await state(); return num(s.matches) > before && s.status.kind === "" && s; });
  const newChests = num(after3.total) - totalBefore;
  await until("new match sent", async () => (await count()) === 1287 + newChests && (await state()).pokedex0.pending === 0);
  check(true, `new match: +${newChests} chests sent (mock 1287 → ${1287 + newChests})`);
  check((await state()).pokedex0.home?.includes("Fortnite Gimmighoul"), "home line shows the hunt");

  // 5. One-time sync, exactly once.
  const stale = await action("pokedex0InitialSync", baselineChests + 1);
  check(stale && stale.error, "one-time sync with a number other than the one shown: refused");
  const r = await action("pokedex0InitialSync", baselineChests);
  check(!r, "one-time sync queued");
  await until("one-time sync sent", async () => (await count()) === 1287 + newChests + baselineChests && (await state()).pokedex0.initialDone);
  check(true, `one-time sync: +${baselineChests} once`);
  const again = await action("pokedex0InitialSync", baselineChests);
  check(again && again.error, "one-time sync cannot run twice");

  // 6. Expired access token: refresh (rotation) and retry, across a restart.
  await stopApp();
  check(true, "counter closed");
  await admin("expireAccessTokens");
  const expected = await count();
  const statsBefore = (await admin("state")).stats;
  addReplay(3);
  await startApp();
  await until("4th replay counted and sent", async () => (await state()).pokedex0.pending === 0 && (await count()) > expected);
  const statsAfter = (await admin("state")).stats;
  check(statsAfter.refreshes === statsBefore.refreshes + 1 && statsAfter.refreshReuse === 0, "after restart: one refresh (no reuse), match sent", statsAfter);
  const afterRestart = await count();

  // 7. Hunt completed on the website: paused, queue kept.
  await admin("setHuntStatus", { id: SOLO, status: "completed" });
  const total4 = num((await state()).total);
  addReplay(4);
  const paused = await until("paused", async () => { const s = await state(); return s.pokedex0.paused === "inactive" && s; });
  const waiting = num((await state()).total) - total4;
  check(paused.pokedex0.pending === waiting && (await count()) === afterRestart, `hunt completed: paused with ${waiting} chests kept`, paused.pokedex0.status);
  await admin("setHuntStatus", { id: SOLO, status: "active" });
  await action("pokedex0Retry");
  await until("sent after retry", async () => (await count()) === afterRestart + waiting && (await state()).pokedex0.pending === 0);
  check(true, "reactivated + Retry: the kept chests are sent once");

  // 8. Nothing secret written anywhere.
  const secrets = (await admin("state")).secrets;
  const stateJson = JSON.stringify(await state());
  const files = [];
  const walk = (d) => { for (const f of fs.readdirSync(d, { withFileTypes: true })) { const p = path.join(d, f.name); if (f.isDirectory()) { if (f.name !== "webview") walk(p); } else files.push(p); } };
  walk(data);
  const leaks = [];
  for (const f of files) {
    const text = fs.readFileSync(f).toString("latin1");
    for (const s of secrets) if (text.includes(s)) leaks.push(`${path.basename(f)}`);
  }
  for (const s of secrets) if (stateJson.includes(s)) leaks.push("GUI state");
  check(leaks.length === 0 && secrets.length > 0, `no token or code in ${files.length} data files nor in the GUI state (${secrets.length} secrets checked)`, leaks);
  check(fs.existsSync(path.join(data, "pokedex0-auth.bin")) && fs.existsSync(path.join(data, "pokedex0.json")), "pokedex0-auth.bin (DPAPI) and pokedex0.json in the data folder");

  // 9. Disconnect: everything forgotten, refresh token revoked.
  await action("pokedex0Disconnect");
  await until("revoked", async () => (await admin("state")).stats.revocations === 1, 10_000);
  const s9 = await state();
  check(!s9.pokedex0.connected && !s9.pokedex0.hunt && !fs.existsSync(path.join(data, "pokedex0-auth.bin")) && !fs.existsSync(path.join(data, "pokedex0.json")), "disconnected: tokens and ledger deleted, refresh token revoked");
  await stopApp();
  check(true, "counter closed");
} catch (e) {
  failed = true;
  console.error(e.message);
  try { app?.kill(); } catch {}
} finally {
  mock.kill();
  try { fs.rmSync(tmp, { recursive: true, force: true }); } catch {}
}
console.log(failed ? `E2E FAILED after ${steps.length} checks` : `E2E PASSED (${steps.length} checks)`);
process.exit(failed ? 1 : 0);
