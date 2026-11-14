// A local stand-in for Pokédex0, for the tests of the Chest Counter's Pokédex0 connection (never used by the app).
// It mirrors docs/pokedex0-integration.md: OAuth 2.1 code + PKCE (S256) for the public client "fortnite-chestcounter",
// refresh tokens that rotate with no reuse interval (presenting a rotated one revokes the whole grant), the session,
// GET /api/shiny-hunts ({ hunts: [...] }) and both increment endpoints with a durable operation ledger
// (idempotent retries, 409 on an id reused with other data, all-or-nothing batches, 400 on 0 / below-zero counts).
//
//   node tests/mock-pokedex0/server.mjs [port]      (port 0 or none: a free port)
// Prints "LISTENING <port>" once ready. The base URL is http://127.0.0.1:<port> (the app accepts http on loopback).
//
// Test controls (POST /__admin/<name>, JSON body):
//   state                 -> everything the tests check (counts, refreshes, grants, ops)
//   reset                 -> initial data
//   expireAccessTokens    -> every access token answers 401 from now on
//   setHuntStatus         {id, status}
//   setCount              {id, count}            (solo current_count / the viewer's participant count)
//   deleteHunt            {id}
//   failNext              {count, status, retryAfter?}   next increment requests fail (503 / 500)
//   denyNext              {}                     next authorization is denied (error=access_denied)
//   revokeGrants          {}                     the user revoked the app in Connected Apps
//   refreshDelay          {ms}                   the token endpoint answers refreshes after this delay
import http from "node:http";
import crypto from "node:crypto";

const CLIENT_ID = "fortnite-chestcounter";
const SCOPES = ["openid", "profile", "offline_access", "hunts:read", "hunts:increment"];
const USER = { id: "6f1c2b9e-1d2a-4c5e-9f00-3b7a1c2d4e5f", username: "ash", display_name: "Ash <Ketchum>" };
const MAX_INCREMENT = 1_000_000;
const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i;

export const HUNTS = {
  solo: "0b8f3c1e-6a43-4d6e-9a51-2c7d0e5f9a10",
  multi: "7d2a9e44-1b3c-4f5d-8e6a-9c0b1d2e3f40",
  done: "1c2d3e4f-5a6b-4c7d-8e9f-0a1b2c3d4e50",
  other: "2e3f4a5b-6c7d-4e8f-9a0b-1c2d3e4f5a60",
};

let base = "";
let s;

function initialState() {
  return {
    hunts: {
      [HUNTS.solo]: hunt(HUNTS.solo, "Fortnite Gimmighoul", false, "active", 1287, { odds: 4096, method: "Fortnite chests" }),
      [HUNTS.multi]: hunt(HUNTS.multi, "Squad chests", true, "active", 0, { participant: 40, others: 500 }),
      [HUNTS.done]: hunt(HUNTS.done, "Old hunt", false, "completed", 9000),
      // A name that would break an inline handler or the page if it was not escaped.
      [HUNTS.other]: hunt(HUNTS.other, `Chests' "quote" </option><img src=x onerror=alert(1)>`, false, "active", 5),
    },
    codes: new Map(),        // code -> { challenge, redirectUri, grantId, expiresAt, used }
    grants: new Map(),       // grantId -> { revoked }
    access: new Map(),       // token -> { grantId, expiresAt }
    refresh: new Map(),      // token -> { grantId, rotated }
    ops: new Map(),          // operationId (lower case) -> { huntId, scope, increment }
    stats: { refreshes: 0, refreshReuse: 0, codeExchanges: 0, incrementRequests: 0, revocations: 0, authorizeRequests: 0 },
    failNext: null,
    denyNext: false,
    refreshDelay: 0,
    lastAuthorize: null,
  };
}

function hunt(id, name, multi, status, count, extra = {}) {
  return {
    id, name, is_multiplayer: multi, status, current_count: multi ? 0 : count,
    participant_count: multi ? extra.participant ?? 0 : count, others: extra.others ?? 0,
    odds: extra.odds ?? null, method: extra.method ?? null, revision: 1, viewerCompleted: false,
  };
}

function payload(h) {
  const display = h.is_multiplayer ? h.participant_count + h.others : h.current_count;
  return {
    id: h.id, user_id: USER.id, public: true, is_multiplayer: h.is_multiplayer, pokemon_id: 999, form_id: 251, game_id: null,
    name: h.name, hunt_method: h.method, current_count: h.current_count, target_count: null, status: h.status,
    notes: null, revision: h.revision, shiny_method_effect_id: null, custom_odds_denominator: h.odds,
    pokemon: { id: 999, name: "Gimmighoul", national_dex_number: 999, primary_type: "Ghost", secondary_type: null },
    form: { id: 251, form_name: "Gimmighoul (Chest Form)", form_type: "form", primary_type: null, secondary_type: null },
    game: null, is_owner: !h.is_multiplayer, display_count: display, participant_count: h.is_multiplayer ? 2 : 1,
    viewer_participant_status: "accepted", viewer_participant_role: h.is_multiplayer ? "participant" : "owner",
    viewer_participant_count: h.is_multiplayer ? h.participant_count : h.current_count,
    viewer_effective_status: h.is_multiplayer && h.viewerCompleted ? "completed" : h.status,
    image_url: "/api/integrations/sprite?dex=999&v=1", some_future_field: { nested: [1, 2, 3] },
  };
}

const b64url = (buf) => Buffer.from(buf).toString("base64url");
const random = () => b64url(crypto.randomBytes(32));

function issueTokens(grantId) {
  const access = "at_" + random();
  const refresh = "rt_" + random();
  const expiresAt = Math.floor(Date.now() / 1000) + 1800;
  s.access.set(access, { grantId, expiresAt: expiresAt * 1000 });
  s.refresh.set(refresh, { grantId, rotated: false });
  return { access_token: access, expires_in: 1800, expires_at: expiresAt, token_type: "Bearer", refresh_token: refresh, scope: SCOPES.join(" "), id_token: "eyJ.mock.id" };
}

function revokeGrant(grantId) {
  const g = s.grants.get(grantId);
  if (g) g.revoked = true;
  for (const [t, a] of s.access) if (a.grantId === grantId) s.access.delete(t);
}

function json(res, status, body, headers = {}) {
  const text = JSON.stringify(body);
  res.writeHead(status, { "Content-Type": "application/json", "Content-Length": Buffer.byteLength(text), ...headers });
  res.end(text);
}

function readBody(req) {
  return new Promise((resolve) => {
    const chunks = [];
    req.on("data", (c) => chunks.push(c));
    req.on("end", () => resolve(Buffer.concat(chunks).toString("utf8")));
  });
}

// Bearer token -> null (ok) or an error response already sent.
function auth(req, res) {
  const h = req.headers.authorization || "";
  const m = /^Bearer (.+)$/.exec(h);
  const a = m && s.access.get(m[1]);
  if (!a || a.expiresAt < Date.now()) {
    json(res, 401, { error: "Unauthorized" });
    return false;
  }
  if (s.grants.get(a.grantId)?.revoked) {
    json(res, 403, { error: "Connected app access was revoked" });
    return false;
  }
  return true;
}

function redirectOk(uri) {
  try {
    const u = new URL(uri);
    return u.protocol === "http:" && u.hostname === "127.0.0.1" && u.pathname === "/oauth/callback" && !u.search && !u.hash && !u.username;
  } catch {
    return false;
  }
}

async function handle(req, res) {
  const url = new URL(req.url, base);
  const path = url.pathname;

  if (req.method === "GET" && path === "/.well-known/oauth-authorization-server") {
    return json(res, 200, {
      issuer: base,
      authorization_endpoint: `${base}/api/auth/oauth2/authorize`,
      token_endpoint: `${base}/api/auth/oauth2/token`,
      revocation_endpoint: `${base}/api/auth/oauth2/revoke`,
      code_challenge_methods_supported: ["S256"],
      authorization_response_iss_parameter_supported: true,
    });
  }

  if (req.method === "GET" && path === "/api/auth/oauth2/authorize") {
    s.stats.authorizeRequests++;
    const q = Object.fromEntries(url.searchParams);
    s.lastAuthorize = q;
    const redirectUri = q.redirect_uri;
    if (q.client_id !== CLIENT_ID || !redirectOk(redirectUri)) {
      res.writeHead(302, { Location: `${base}/api/auth/error?error=invalid_redirect&error_description=invalid+redirect+uri` });
      return res.end();
    }
    const back = (params) => {
      const u = new URL(redirectUri);
      for (const [k, v] of Object.entries({ ...params, state: q.state, iss: base })) u.searchParams.set(k, v);
      res.writeHead(302, { Location: u.toString() });
      res.end();
    };
    const scopes = (q.scope || "").split(/[ +]/).filter(Boolean);
    if (q.response_type !== "code" || !q.state || q.code_challenge_method !== "S256" || !q.code_challenge
        || scopes.some((x) => !SCOPES.includes(x)) || SCOPES.some((x) => !scopes.includes(x))) {
      return back({ error: "invalid_request", error_description: "bad <request> 'parameters'" });
    }
    if (q.resource !== `${base}/api`) return back({ error: "invalid_target", error_description: "requested resource invalid" });
    if (s.denyNext) {
      s.denyNext = false;
      return back({ error: "access_denied", error_description: "User <b>denied</b> access" });
    }
    const code = random();
    const grantId = crypto.randomUUID();
    s.grants.set(grantId, { revoked: false });
    s.codes.set(code, { challenge: q.code_challenge, redirectUri, grantId, expiresAt: Date.now() + 60_000, used: false });
    return back({ code });
  }

  if (req.method === "POST" && path === "/api/auth/oauth2/token") {
    if (req.headers.authorization) return json(res, 400, { error: "invalid_client" });
    const form = Object.fromEntries(new URLSearchParams(await readBody(req)));
    if (form.client_id !== CLIENT_ID || form.client_secret) return json(res, 401, { error: "invalid_client" });
    if (form.resource !== `${base}/api`) return json(res, 400, { error: "invalid_target" });
    if (form.grant_type === "authorization_code") {
      s.stats.codeExchanges++;
      const c = s.codes.get(form.code);
      if (!c || c.used || c.expiresAt < Date.now()) return json(res, 400, { error: "invalid_grant", error_description: "invalid code" });
      c.used = true;
      if (c.redirectUri !== form.redirect_uri) return json(res, 400, { error: "invalid_grant", error_description: "redirect_uri mismatch" });
      const challenge = b64url(crypto.createHash("sha256").update(form.code_verifier || "", "ascii").digest());
      if (challenge !== c.challenge) return json(res, 400, { error: "invalid_grant", error_description: "code verifier mismatch" });
      return json(res, 200, issueTokens(c.grantId));
    }
    if (form.grant_type === "refresh_token") {
      s.stats.refreshes++;
      if (s.refreshDelay) await new Promise((r) => setTimeout(r, s.refreshDelay));
      const r = s.refresh.get(form.refresh_token);
      if (!r) return json(res, 400, { error: "invalid_grant", error_description: "invalid refresh token" });
      if (r.rotated) {
        // Zero reuse interval: a rotated refresh token presented again revokes the entire grant.
        s.stats.refreshReuse++;
        revokeGrant(r.grantId);
        return json(res, 400, { error: "invalid_grant", error_description: "invalid refresh token" });
      }
      if (s.grants.get(r.grantId)?.revoked) return json(res, 400, { error: "invalid_grant", error_description: "invalid refresh token" });
      r.rotated = true;
      return json(res, 200, issueTokens(r.grantId));
    }
    return json(res, 400, { error: "unsupported_grant_type" });
  }

  if (req.method === "POST" && path === "/api/auth/oauth2/revoke") {
    const form = Object.fromEntries(new URLSearchParams(await readBody(req)));
    const r = s.refresh.get(form.token);
    if (!r || form.client_id !== CLIENT_ID) return json(res, 400, { error: "invalid_request", error_description: "token not found" });
    s.stats.revocations++;
    revokeGrant(r.grantId);
    return json(res, 200, {});
  }

  if (req.method === "GET" && path === "/api/oauth/session") {
    if (!auth(req, res)) return;
    return json(res, 200, { user: USER, client_id: CLIENT_ID, scopes: SCOPES });
  }

  if (req.method === "GET" && path === "/api/shiny-hunts") {
    if (!auth(req, res)) return;
    return json(res, 200, { hunts: Object.values(s.hunts).map(payload) });
  }

  const inc = /^\/api\/shiny-hunts\/([^/]+)\/(increment|participants\/increment)$/.exec(path);
  if (req.method === "POST" && inc) return increment(req, res, inc[1], inc[2] === "increment" ? "single" : "participant");

  if (req.method === "POST" && path.startsWith("/__admin/")) return admin(req, res, path.slice("/__admin/".length));

  json(res, 404, { error: "Not found" });
}

async function increment(req, res, huntId, scope) {
  s.stats.incrementRequests++;
  // The body is validated before the token (as on Pokédex0).
  let body;
  try {
    body = JSON.parse(await readBody(req));
  } catch {
    return json(res, 400, { error: "Counter request body must contain valid JSON." });
  }
  const list = Array.isArray(body?.operations) ? body.operations : body?.operationId ? [body] : null;
  if (!list || list.length === 0) return json(res, 400, { error: "Counter operations must be a non-empty array." });
  if (list.length > 100) return json(res, 400, { error: "A counter request may contain at most 100 operations." });
  const ops = [];
  for (const o of list) {
    const inc = o.amount ?? o.increment;
    if (typeof o.operationId !== "string" || !UUID.test(o.operationId)) return json(res, 400, { error: "Counter operation ID must be a valid UUID." });
    if (!Number.isInteger(inc)) return json(res, 400, { error: "Increment must be a whole number." });
    if (inc === 0) return json(res, 400, { error: "Increment must not be zero." });
    if (Math.abs(inc) > MAX_INCREMENT) return json(res, 400, { error: `Increment must be between -${MAX_INCREMENT} and ${MAX_INCREMENT}.` });
    ops.push({ operationId: o.operationId.toLowerCase(), increment: inc });
  }
  if (new Set(ops.map((o) => o.operationId)).size !== ops.length) return json(res, 400, { error: "Counter operation IDs must be unique within a request." });

  if (!auth(req, res)) return;

  if (s.failNext && s.failNext.count > 0) {
    s.failNext.count--;
    const status = s.failNext.status;
    return status === 503
      ? json(res, 503, { error: "Counter synchronization conflicted. Retrying is safe." }, { "Retry-After": String(s.failNext.retryAfter ?? 1) })
      : json(res, status, { error: "Internal Server Error" });
  }

  const h = s.hunts[huntId];
  if (!h) return json(res, 404, { error: scope === "single" ? "Hunt not found" : "Hunt not found." });

  // Ledger: an id already recorded with other data -> 409, nothing applied.
  const fresh = [];
  for (const o of ops) {
    const known = s.ops.get(o.operationId);
    if (known) {
      if (known.huntId !== huntId || known.scope !== scope || known.increment !== o.increment) {
        return json(res, 409, { error: "A counter operation ID was already used with different data." });
      }
    } else {
      fresh.push(o);
    }
  }
  const ack = () => {
    const display = h.is_multiplayer ? h.participant_count + h.others : h.current_count;
    return {
      acknowledgedAt: Date.now(), currentCount: h.current_count, displayCount: display, lastEncounterAt: null,
      operationIds: ops.map((o) => o.operationId), revision: h.revision, scope,
      viewerParticipantCount: h.is_multiplayer ? h.participant_count : h.current_count, viewerParticipantLastEncounterAt: null,
    };
  };
  const answer = (applied) => json(res, 200, {
    acknowledgement: ack(), success: true, hunt: payload(h), count: h.current_count,
    operationIds: ops.map((o) => o.operationId), appliedOperationIds: applied.map((o) => o.operationId),
  });
  if (fresh.length === 0) return answer([]); // a retry: 200 even if the hunt became inactive since

  if (scope === "single" && h.is_multiplayer) return json(res, 400, { error: "Multiplayer hunts must use the participant counter endpoint." });
  if (scope === "participant" && !h.is_multiplayer) return json(res, 400, { error: "This hunt is not multiplayer." });
  if (h.status !== "active") {
    return json(res, 409, { error: scope === "single" ? "Only active hunts can be incremented." : "This hunt is not active. Resume your personal hunt first if everyone is already done." });
  }
  if (h.is_multiplayer && h.viewerCompleted) return json(res, 409, { error: "Your part of this hunt is already marked complete. Resume it before changing the counter." });
  const delta = fresh.reduce((t, o) => t + o.increment, 0);
  const current = h.is_multiplayer ? h.participant_count : h.current_count;
  if (current + delta < 0 || current + delta > 2147483647) return json(res, 400, { error: "Count must be between 0 and 2147483647." });

  // All or nothing: every new operation of the batch is applied together.
  if (h.is_multiplayer) h.participant_count += delta; else h.current_count += delta;
  h.revision++;
  for (const o of fresh) s.ops.set(o.operationId, { huntId, scope, increment: o.increment });
  return answer(fresh);
}

async function admin(req, res, name) {
  const text = await readBody(req);
  const a = text ? JSON.parse(text) : {};
  switch (name) {
    case "state":
      return json(res, 200, {
        hunts: Object.fromEntries(Object.values(s.hunts).map((h) => [h.id, { status: h.status, count: h.is_multiplayer ? h.participant_count : h.current_count }])),
        stats: s.stats,
        ops: Object.fromEntries(s.ops),
        grants: [...s.grants.values()],
        lastAuthorize: s.lastAuthorize,
        // Every token and code ever issued: the tests check that none of them is written anywhere by the counter.
        secrets: [...s.access.keys(), ...s.refresh.keys(), ...s.codes.keys()],
      });
    case "reset": s = initialState(); break;
    case "expireAccessTokens": for (const v of s.access.values()) v.expiresAt = 0; break;
    case "setHuntStatus": s.hunts[a.id].status = a.status; break;
    case "setCount": if (s.hunts[a.id].is_multiplayer) s.hunts[a.id].participant_count = a.count; else s.hunts[a.id].current_count = a.count; break;
    case "deleteHunt": delete s.hunts[a.id]; break;
    case "failNext": s.failNext = { count: a.count ?? 1, status: a.status ?? 503, retryAfter: a.retryAfter }; break;
    case "denyNext": s.denyNext = true; break;
    case "revokeGrants": for (const id of s.grants.keys()) s.grants.get(id).revoked = true; break;
    case "refreshDelay": s.refreshDelay = a.ms ?? 0; break;
    default: return json(res, 404, { error: "unknown admin action" });
  }
  json(res, 200, { ok: true });
}

s = initialState();
const server = http.createServer((req, res) => {
  handle(req, res).catch((e) => {
    console.error(e);
    json(res, 500, { error: "Internal Server Error" });
  });
});
const port = Number(process.argv[2] ?? process.env.PORT ?? 0);
server.listen(port, "127.0.0.1", () => {
  base = `http://127.0.0.1:${server.address().port}`;
  console.log(`LISTENING ${server.address().port}`);
});
