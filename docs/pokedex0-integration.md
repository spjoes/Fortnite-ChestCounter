# Fortnite Chest Counter ↔ Pokédex0 Integration Contract

This is the authoritative contract between Pokédex0 and the Fortnite Chest
Counter Windows app (`github.com/spjoes/Fortnite-ChestCounter`, C#/.NET). The
app connects to a user's Pokédex0 account with OAuth 2.1 (authorization code +
PKCE) and, after each Fortnite match, adds that match's chest count to a shiny
hunt the user picked. Users track Fortnite chests as a Gimmighoul hunt with a
custom method name and, optionally, custom 1-in-N odds.

Every statement below was checked against the server code and Better Auth
1.7.23 as deployed. Where production was probed directly, that is noted.

## Base URL

```text
https://www.pokedex0.com
```

Use the `www` origin for every request. `https://pokedex0.com` answers with a
`307` redirect to `www`, and the OAuth issuer, the API audience, and the token
`aud` claim are all on `www`. In particular the OAuth `resource` value must be
exactly `https://www.pokedex0.com/api`; `https://pokedex0.com/api` is rejected
with `invalid_target` (verified against production).

Discovery (optional, but recommended over hard-coding endpoints):

```http
GET https://www.pokedex0.com/.well-known/oauth-authorization-server
```

Relevant fields of the live response:

```json
{
  "issuer": "https://www.pokedex0.com",
  "authorization_endpoint": "https://www.pokedex0.com/api/auth/oauth2/authorize",
  "token_endpoint": "https://www.pokedex0.com/api/auth/oauth2/token",
  "revocation_endpoint": "https://www.pokedex0.com/api/auth/oauth2/revoke",
  "code_challenge_methods_supported": ["S256"],
  "authorization_response_iss_parameter_supported": true
}
```

The protected-resource metadata at
`https://www.pokedex0.com/.well-known/oauth-protected-resource/api` reports
`"resource": "https://www.pokedex0.com/api"`.

## OAuth Client

```text
client_id:     fortnite-chestcounter
client name:   Fortnite Chest Counter   (shown on the consent screen and in Connected Apps)
client type:   public native client (applicationType "native")
client secret: none — never send one (token_endpoint_auth_method "none")
PKCE:          required, S256 only
grant types:   authorization_code, refresh_token
redirect_uri:  http://127.0.0.1:<port>/oauth/callback
scopes:        openid profile offline_access hunts:read hunts:increment
```

The app cannot request `hunts:manage`; it cannot create, edit, complete, or
delete hunts. Asking for any scope outside the list above fails authorization.

### Redirect URI and loopback port

The registered redirect URI is `http://127.0.0.1/oauth/callback`. For native
clients Better Auth ignores the port of a loopback redirect URI when matching
(RFC 8252 §7.3), so the app may listen on any free port:

| Requested `redirect_uri` | Production result |
|---|---|
| `http://127.0.0.1:47613/oauth/callback` | accepted → `302` to `/auth/sign-in?...` |
| `http://127.0.0.1:51234/oauth/callback` | accepted → `302` to `/auth/sign-in?...` |
| `http://127.0.0.1:47613/wrong` | rejected → `302` to `https://www.pokedex0.com/api/auth/error?error=invalid_redirect&error_description=invalid+redirect+uri` |
| `http://localhost:47613/oauth/callback` | rejected (`invalid_redirect`) |

Rules:

- Host must be the literal `127.0.0.1`, not `localhost` or `[::1]`.
- Scheme `http`, path exactly `/oauth/callback`, no query string, no fragment,
  no user info. Only the port may vary.
- Bind the listener to `127.0.0.1` only.
- When the redirect URI is rejected, the browser shows a Pokédex0 error page
  and the app's listener never receives a callback. Use a timeout.

## Authorization Flow

1. Generate a PKCE `code_verifier`: 32 random bytes, base64url without padding
   (43 characters; the allowed range is 43–128 characters of
   `[A-Za-z0-9-._~]`).
2. `code_challenge = base64url_nopad(SHA256(ASCII(code_verifier)))`.
3. Generate `state`: at least 16 random bytes, base64url without padding.
4. Start the loopback listener on `127.0.0.1:<port>` and build
   `redirect_uri = http://127.0.0.1:<port>/oauth/callback`.
5. Open the system browser at:

```text
https://www.pokedex0.com/api/auth/oauth2/authorize
  ?client_id=fortnite-chestcounter
  &redirect_uri=http%3A%2F%2F127.0.0.1%3A<port>%2Foauth%2Fcallback
  &response_type=code
  &scope=openid+profile+offline_access+hunts%3Aread+hunts%3Aincrement
  &state=<state>
  &code_challenge=<code_challenge>
  &code_challenge_method=S256
  &resource=https%3A%2F%2Fwww.pokedex0.com%2Fapi
```

These are the same parameters the website's own connect pages build
(`lib/oauth-connect.ts#getOAuthAuthorizePath`). All parameters are required.
The scope separator may be `+` or `%20`.

6. The user signs in if needed, then sees “Fortnite Chest Counter wants to
   connect.” with one line per scope:
   - Identify your Pokédex0 account.
   - Read your public profile name and username.
   - Stay connected after the current browser session ends.
   - Read your hunts.
   - Increment hunt counters.
7. On approval the browser is sent to:

```text
http://127.0.0.1:<port>/oauth/callback?code=<code>&state=<state>&iss=https%3A%2F%2Fwww.pokedex0.com
```

   On denial or a protocol error after the redirect URI was validated:

```text
http://127.0.0.1:<port>/oauth/callback?error=<code>&error_description=<text>&state=<state>&iss=https%3A%2F%2Fwww.pokedex0.com
```

   (for example `error=access_denied`, or `error=invalid_target` for a wrong
   `resource`).
8. Verify `state` matches exactly. Verify `iss` equals
   `https://www.pokedex0.com` when present. Reply to the browser with a small
   “You can close this window” page, then stop the listener.
9. Exchange the code (next section) promptly. Codes are single-use and
   short-lived.
10. Discard `state` and `code_verifier` after success or failure.

## Token Endpoint

```text
POST https://www.pokedex0.com/api/auth/oauth2/token
Content-Type: application/x-www-form-urlencoded
```

Do not send an `Authorization` header or a client secret.

### Authorization code exchange

```text
grant_type=authorization_code
&code=<code>
&code_verifier=<code_verifier>
&redirect_uri=http%3A%2F%2F127.0.0.1%3A<port>%2Foauth%2Fcallback
&client_id=fortnite-chestcounter
&resource=https%3A%2F%2Fwww.pokedex0.com%2Fapi
```

`redirect_uri` must be byte-for-byte the value sent to the authorize endpoint,
including the port. A different value fails with `invalid_grant`
(`redirect_uri mismatch`).

### Refresh

```text
grant_type=refresh_token
&refresh_token=<refresh_token>
&client_id=fortnite-chestcounter
&resource=https%3A%2F%2Fwww.pokedex0.com%2Fapi
```

Omit `scope` to keep the granted scopes.

### Successful response (both grants)

```json
{
  "access_token": "eyJhbGciOi...",
  "expires_in": 1800,
  "expires_at": 1794683466,
  "token_type": "Bearer",
  "refresh_token": "....",
  "scope": "openid profile offline_access hunts:read hunts:increment",
  "id_token": "eyJhbGciOi..."
}
```

- The access token is a JWT for audience `https://www.pokedex0.com/api`.
  Treat it as opaque; use `expires_at` (Unix seconds) or `expires_in`.
- **Access tokens that include `hunts:increment` last 30 minutes**
  (`expires_in: 1800`). Refresh shortly before `expires_at`, or on a `401`.
- **Refresh tokens rotate.** Every successful refresh returns a new
  `refresh_token` and invalidates the one you sent. Persist the new token
  (with DPAPI or Windows Credential Manager) before using the new access
  token.
- **Never reuse a rotated refresh token.** Presenting an already-rotated
  refresh token fails with `invalid_grant` *and revokes the entire grant* for
  this user and app; the user must reconnect. Allow only one refresh in flight
  at a time (single-flight lock) and never retry a refresh with an old token.
- A refresh token is valid for 30 days from issue. Each refresh issues a new
  30-day token, so a regularly used app stays connected.

### Token errors

`400` with an OAuth error body, for example:

```json
{ "error": "invalid_grant", "error_description": "invalid refresh token" }
```

Treat `invalid_grant` on refresh as “disconnected”: clear stored tokens and
ask the user to connect again. `invalid_client` means a wrong `client_id`.

## Calling the API

```http
Authorization: Bearer <access_token>
```

All endpoints below are under `https://www.pokedex0.com`. Responses are JSON;
error bodies are `{ "error": "<message>" }`.

Common auth statuses:

| Status | Body `error` | Meaning | App action |
|---|---|---|---|
| `401` | `Unauthorized` | Missing, malformed, expired, or wrong-audience token | Refresh once, then retry once with the new token. If the refresh fails, show “disconnected”. |
| `403` | `Forbidden` | Token is valid but lacks the required scope | Reconnect. |
| `403` | `Connected app access was revoked` | The user revoked the app in Settings → Connected Apps | Clear tokens and show “disconnected”. Refresh will also fail. |

### `GET /api/oauth/session`

Required scopes: `openid profile`.

```json
{
  "user": {
    "id": "6f1c2b9e-1d2a-4c5e-9f00-3b7a1c2d4e5f",
    "username": "ash",
    "display_name": "Ash"
  },
  "client_id": "fortnite-chestcounter",
  "scopes": ["openid", "profile", "offline_access", "hunts:read", "hunts:increment"]
}
```

`username` and `display_name` may be `null`. Use it after connecting to show
“Connected as …”.

### `GET /api/shiny-hunts`

Required scope: `hunts:read`.

Returns `{ "hunts": [ ... ] }`: every hunt the user owns plus every
multiplayer hunt they have accepted an invitation to, in all statuses, sorted
by `updated_at` descending. There is no pagination and no filter parameter.

Show only hunts the user can count on now:

```csharp
var countable = hunts.Where(h => (h.ViewerEffectiveStatus ?? h.Status) == "active");
```

Fields that matter to the app:

| Field | Type | Notes |
|---|---|---|
| `id` | string (UUID) | Hunt id for the increment URL. |
| `name` | string | Hunt name. |
| `status` | `"active" \| "completed" \| "abandoned"` | Shared hunt status. |
| `viewer_effective_status` | same | The user's own status. For a multiplayer hunt the user has personally finished, this is `"completed"` while `status` is still `"active"`. **Filter on this.** |
| `is_multiplayer` | bool | Chooses the increment endpoint. |
| `is_owner` | bool | The user created the hunt. |
| `display_count` | int | Headline total: own count for solo hunts, sum of accepted participants for multiplayer hunts. |
| `current_count` | int | Solo hunts: the counter. Multiplayer hunts: the hunt row's legacy counter; do not display it. |
| `viewer_participant_count` | int or null | The user's personal count. Solo owner: same as `current_count`. Multiplayer: the user's participant count. |
| `viewer_participant_status` | `"accepted" \| "pending" \| "declined"` or null | `"accepted"` for the user's own solo hunts and for multiplayer hunts they joined. |
| `viewer_participant_role` | `"owner" \| "participant"` or null | |
| `participant_count` | int | `1` for solo hunts. |
| `target_count` | int or null | Optional goal. |
| `hunt_method` | string or null | The method label. For a custom method this is the user's own text, for example `"Fortnite chests"`. |
| `custom_odds_denominator` | int or null | Custom odds 1 in N, set by the owner for a custom method (1–1,000,000). `null` means no custom odds. Never set alongside a verified method (`shiny_method_effect_id` is then `null`). |
| `shiny_method_effect_id` | int or null | Set only for a verified, catalogued method. |
| `pokemon` | object or null | `{ id, name, national_dex_number, primary_type, secondary_type }`. Gimmighoul is `national_dex_number` 999. |
| `form` | object or null | `{ id, form_name, form_type, primary_type, secondary_type }`, for example `"Gimmighoul (Chest Form)"`. |
| `game` | object or null | Catalogue game `{ id, name, generation, shiny_mechanics }`, or `null` when the hunt has no game. There is no free-text “custom game”; a Fortnite hunt is typically `null` here. |
| `image_url` | string or null | Site-relative 144×144 PNG URL, for example `/api/integrations/sprite?dex=999&v=…`. Prefix with the base URL. Treat as opaque. |
| `last_encounter_at` | ISO 8601 string or null | |
| `revision` | int | Monotonic counter revision; a higher value is newer. |
| `updated_at` | ISO 8601 string | |

Other fields (`public`, `notes` (always `null` here), `started_at`,
`completed_at`, `shiny_charm_*`, `settings_revision`, `viewer_participant_*`,
`viewer_effective_completed_at`, `game.shiny_mechanics`, …) may be ignored.
Clients must ignore unknown fields.

Example (one solo hunt, Fortnite-style, `game.shiny_mechanics` trimmed):

```json
{
  "hunts": [
    {
      "id": "0b8f3c1e-6a43-4d6e-9a51-2c7d0e5f9a10",
      "user_id": "6f1c2b9e-1d2a-4c5e-9f00-3b7a1c2d4e5f",
      "public": true,
      "is_multiplayer": false,
      "pokemon_id": 999,
      "form_id": 251,
      "game_id": null,
      "name": "Fortnite Gimmighoul",
      "hunt_method": "Fortnite chests",
      "current_count": 1287,
      "target_count": null,
      "status": "active",
      "started_at": "2026-11-01T17:02:11.000Z",
      "completed_at": null,
      "notes": null,
      "created_at": "2026-11-01T17:02:11.000Z",
      "updated_at": "2026-11-14T02:41:09.512Z",
      "revision": 214,
      "settings_revision": 3,
      "last_encounter_at": "2026-11-14T02:41:09.512Z",
      "shiny_method_effect_id": null,
      "shiny_charm_active": false,
      "shiny_charm_from_count": null,
      "shiny_charm_effect_id": null,
      "custom_odds_denominator": 4096,
      "pokemon": {
        "id": 999,
        "name": "Gimmighoul",
        "national_dex_number": 999,
        "primary_type": "Ghost",
        "secondary_type": null
      },
      "form": {
        "id": 251,
        "form_name": "Gimmighoul (Chest Form)",
        "form_type": "form",
        "primary_type": null,
        "secondary_type": null
      },
      "game": null,
      "is_owner": true,
      "display_count": 1287,
      "participant_count": 1,
      "viewer_participant_status": "accepted",
      "viewer_participant_role": "owner",
      "viewer_participant_count": 1287,
      "viewer_participant_last_encounter_at": "2026-11-14T02:41:09.512Z",
      "viewer_participant_completed_at": null,
      "viewer_effective_status": "active",
      "viewer_effective_completed_at": null,
      "image_url": "/api/integrations/sprite?dex=999&v=…&form=…"
    }
  ]
}
```

## Adding a Match's Chests

Required scope: `hunts:increment`.

Pick the endpoint from `is_multiplayer`:

```text
Solo hunt:         POST /api/shiny-hunts/{huntId}/increment
Multiplayer hunt:  POST /api/shiny-hunts/{huntId}/participants/increment
```

Using the wrong one fails with `400` (`Multiplayer hunts must use the
participant counter endpoint.` / `This hunt is not multiplayer.`).

### Request body

```http
Content-Type: application/json
```

```json
{
  "operations": [
    { "operationId": "3f2b8c4e-9d1a-4b7e-8c2f-5a6d7e8f9a0b", "increment": 7 }
  ]
}
```

- `operationId`: a UUID (versions 1–5, RFC 4122 variant). `Guid.NewGuid()`
  works. Compared case-insensitively. **Required.**
- `increment` (alias `amount`): a non-zero whole JSON number, from
  `-1000000` to `1000000`. **Required.** Do not send matches with zero chests;
  `0` is rejected.
- `operations`: 1 to 100 entries, each with a distinct `operationId`.
- The single-operation form `{ "operationId": "…", "increment": 7 }` is also
  accepted.
- The resulting count must stay between 0 and 2,147,483,647, so a negative
  correction cannot take a counter below zero (`400`).

The body is validated before the token is checked, so a malformed body
returns `400` even if the token is also bad.

### Idempotency: one `operationId` per match

Every operation is recorded in a durable ledger keyed by (user, `operationId`)
in the same transaction as the counter change. This is what makes retries safe:

1. When a match ends, create one `operationId` for that match and store it
   locally (an outbox entry: match, hunt id, endpoint, chest count,
   `operationId`) *before* sending.
2. Send it. If the request times out, the connection drops, or the response is
   `401` (after refreshing), `503`, or `5xx`, resend **the same body with the
   same `operationId`**. Never create a new id for a retry; that would count
   the match twice.
3. Remove the outbox entry when the server confirms it (`200`), or on a
   permanent error (below).

Server behavior:

| Situation | Result |
|---|---|
| New `operationId` | Applied once. Listed in `appliedOperationIds`. |
| Same `operationId`, same hunt, same endpoint, same `increment` (a retry) | `200`, nothing changes, **not** listed in `appliedOperationIds`; the response still carries the current counts. This holds even if the hunt has since become inactive. |
| Same `operationId` with a different `increment`, a different hunt, or the other endpoint | `409` `{ "error": "A counter operation ID was already used with different data." }`. Nothing in that request is applied. |
| Several operations in one request | All-or-nothing: either every new operation in the batch is applied, or none is. Already-recorded operations in the batch are skipped, not re-applied. |

A failed request (`4xx` other than the replay case, or `5xx`) records nothing,
so its `operationId` was not consumed.

Batching: if the app was offline for several matches, it may send up to 100
queued operations for the **same hunt and endpoint** in one request. Each match
keeps its own `operationId`.

### Responses

Solo (`/increment`):

```json
{
  "acknowledgement": {
    "acknowledgedAt": 1794681666132,
    "currentCount": 1294,
    "displayCount": 1294,
    "lastEncounterAt": "2026-11-14T19:01:06.120Z",
    "operationIds": ["3f2b8c4e-9d1a-4b7e-8c2f-5a6d7e8f9a0b"],
    "revision": 215,
    "scope": "single",
    "viewerParticipantCount": 1294,
    "viewerParticipantLastEncounterAt": "2026-11-14T19:01:06.120Z"
  },
  "success": true,
  "hunt": { "...": "same shape as one GET /api/shiny-hunts item" },
  "count": 1294,
  "operationIds": ["3f2b8c4e-9d1a-4b7e-8c2f-5a6d7e8f9a0b"],
  "appliedOperationIds": ["3f2b8c4e-9d1a-4b7e-8c2f-5a6d7e8f9a0b"]
}
```

Multiplayer (`/participants/increment`):

```json
{
  "acknowledgement": {
    "acknowledgedAt": 1794681666132,
    "currentCount": 0,
    "displayCount": 5321,
    "lastEncounterAt": null,
    "operationIds": ["3f2b8c4e-9d1a-4b7e-8c2f-5a6d7e8f9a0b"],
    "revision": 88,
    "scope": "participant",
    "viewerParticipantCount": 1294,
    "viewerParticipantLastEncounterAt": "2026-11-14T19:01:06.120Z"
  },
  "hunt": { "...": "same fields as a GET /api/shiny-hunts item, plus a participants array" },
  "participant": { "current_count": 1294, "...": "the user's participant row" },
  "operationIds": ["3f2b8c4e-9d1a-4b7e-8c2f-5a6d7e8f9a0b"],
  "appliedOperationIds": ["3f2b8c4e-9d1a-4b7e-8c2f-5a6d7e8f9a0b"]
}
```

Use `acknowledgement.displayCount` (hunt total) and
`acknowledgement.viewerParticipantCount` (the user's own count) to update the
UI. `acknowledgement.revision` orders snapshots: ignore a snapshot whose
revision is lower than one you already showed. `hunt` can be `null` in a rare
race where the hunt was deleted right after the increment.

### Increment errors

| Status | `error` | App action |
|---|---|---|
| `400` | Validation message (bad JSON, missing/invalid `operationId`, zero or out-of-range increment, more than 100 operations, duplicate ids, count out of range, wrong endpoint for the hunt type) | Permanent. Drop the outbox entry and log it; it is a client bug or a bad correction. |
| `401` | `Unauthorized` | Refresh once and retry with the same body. If refresh fails, keep the outbox entry and show “disconnected”. |
| `403` | `Forbidden` (solo hunt the user does not own, or missing scope) | Permanent for this hunt. |
| `403` | `You are not an accepted participant on this hunt.` | Permanent for this hunt (the user left or was removed). |
| `403` | `Connected app access was revoked` | Disconnected. Keep the outbox; send it after the user reconnects. |
| `404` | `Hunt not found` / `Hunt not found.` | The hunt was deleted. Ask the user to pick another hunt. |
| `409` | `Only active hunts can be incremented.` (solo) | The hunt is completed or abandoned. Ask the user to pick another hunt. |
| `409` | `This hunt is not active. Resume your personal hunt first if everyone is already done.` / `Your part of this hunt is already marked complete. Resume it before changing the counter.` (multiplayer) | Same as above. |
| `409` | `A counter operation ID was already used with different data.` | Client bug: an id was reused for different data. Do not retry. |
| `503` | `Counter synchronization conflicted. Retrying is safe.` (`Retry-After: 1`) | Retry the same body after the delay. |
| `500` | `Internal Server Error` | Retry the same body later with backoff. |

Only send hunt ids that came from `GET /api/shiny-hunts`.

When the user moves pending matches to another hunt after a `404`/`409`, give
those operations new `operationId`s: the server never recorded the failed
ones, but new ids keep the outbox simple.

## Disconnecting

The user can revoke Fortnite Chest Counter under Pokédex0 account settings →
Connected Apps. Revocation deletes the consent, deletes access tokens, and
revokes refresh tokens. The app then sees `403 Connected app access was
revoked` on API calls and `invalid_grant` on refresh.

For a “Disconnect” button in the app, delete the stored tokens locally. You may
also revoke the refresh token at the `revocation_endpoint` from discovery
(`token=<refresh_token>&token_type_hint=refresh_token&client_id=fortnite-chestcounter`,
form-encoded, no secret). Ignore its result: an unknown or already-revoked
token returns `400 invalid_request` (`token not found`). The user can always
revoke from the website as well.

## Recommended App Logic

Startup:

1. Load stored tokens. If none, show “Connect to Pokédex0”.
2. If `expires_at` is less than 60 seconds away, refresh (single-flight).
3. `GET /api/oauth/session` to show the connected account.
4. `GET /api/shiny-hunts`, keep hunts with
   `(viewer_effective_status ?? status) == "active"`, and let the user choose
   one. Remember its `id` and `is_multiplayer`.
5. Flush the outbox.

Match end:

1. Read the chest count. If it is 0, do nothing.
2. Add an outbox entry with a new `operationId`.
3. POST it to the endpoint for the selected hunt.
4. On `200`, update the shown count from `acknowledgement` and remove the
   entry.
5. On a transient failure, keep the entry and retry with the same body
   (backoff, and on reconnect or next start).
6. On a permanent failure, follow the error table above.
