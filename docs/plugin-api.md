# Plugin API

Lets a Minecraft plugin talk to the MineOS panel: read the servers around it,
start/stop/restart them, send console commands, and report events.

The driving case is a proxy plugin restarting one of its own backends — a hub
plugin that spins a minigame server back up when someone tries to join it.

> Not to be confused with the panel's **plugin management** endpoints
> (`/api/v1/servers/{name}/plugins`), which install and toggle plugin JARs on
> disk. This page is about plugins calling *in*.

## Why this is not the existing API key

MineOS already has an `X-Api-Key`. Do not use it for this. A valid API key is
promoted to a full-access **admin** principal, and a plugin token lives in a
plaintext config file on a game server that runs third-party code and accepts
connections from the public internet. Handing that server an admin key means one
leaked config costs you the whole panel.

A plugin token is different in three ways that matter:

| | `X-Api-Key` | `X-Plugin-Token` |
|---|---|---|
| Authority | Whole panel, as admin | One server (plus its backends, if a proxy) |
| Storage | Plaintext in the database | SHA-256 hash only |
| Expiry | Column exists, never checked | Enforced |

A plugin token is rejected by the panel's own endpoints, and an API key is
rejected by the plugin endpoints. Neither can stand in for the other.

## Issuing a token

Admin only, from the panel's authenticated API:

```bash
curl -X POST http://panel:8080/api/v1/plugin-tokens \
  -H "X-Api-Key: $ADMIN_KEY" \
  -H "Content-Type: application/json" \
  -d '{
        "name": "hub velocity plugin",
        "serverName": "hub",
        "scopes": ["servers:read", "servers:control"],
        "allowProxyBackends": true
      }'
```

```json
{
  "token": { "id": 1, "tokenPrefix": "mosp_EXAMPLE1", "...": "..." },
  "secret": "mosp_EXAMPLE1xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx"
}
```

**`secret` is shown once and never again.** Only its hash is stored, so it cannot
be recovered — if it is lost, revoke the token and issue another. The
`tokenPrefix` is kept in clear so you can match a token in the panel to the one
pasted into a plugin config.

Add `"expiresAt": "2027-01-01T00:00:00Z"` to make it expire. Unlike the old API
key path, this is actually enforced.

Other operator endpoints:

- `GET /api/v1/plugin-tokens` — list (never includes secrets); `?serverName=` filters
- `GET /api/v1/plugin-tokens/scopes` — the valid scope strings
- `DELETE /api/v1/plugin-tokens/{id}` — revoke, effective on the next request

## Scopes

Scopes come in read/write pairs, so a plugin that only needs to look at something
is never handed the ability to change it.

| Capability | Read | Write |
|---|---|---|
| Servers | `servers:read` — list servers in reach, read status | `servers:control` — start, stop, restart |
| Console | `console:read` — read recent console output | `console:write` — run a console command |
| Events | `events:read` — read events already reported | `events:write` — report an event |
| Players | `players:read` — read known players | `players:write` — whitelist, op, ban, unban |

Granting a read scope never implies its write half, or the reverse. A log-reading
plugin takes `console:read` alone and cannot run commands; a plugin that only
reports telemetry takes `events:write` alone and cannot read anything back.

An unknown scope is **refused at issue time** rather than silently dropped — a
mistyped scope would otherwise mint a token that looks correct and fails in the
field, where the plugin author cannot see why.

## Reach: which servers a token can touch

A token always reaches **its own server**. With `allowProxyBackends: true`, a
token issued to a Velocity or BungeeCord proxy also reaches **the backends that
proxy fronts**.

That backend list is read from the proxy's own configuration at request time, not
stored alongside the token. So detaching a server from the proxy immediately ends
the plugin's authority over it, and there is no second place for an operator to
remember to update.

A server outside a token's reach answers **404, not 403**. A 403 would confirm
the server exists, which would let a plugin map every server on the panel by
probing names.

## Endpoints

All take `X-Plugin-Token: <secret>`, all live under `/api/v1/plugin`.

| Method | Path | Scope |
|---|---|---|
| GET | `/me` | any valid token |
| GET | `/servers` | `servers:read` |
| GET | `/servers/{server}/status` | `servers:read` |
| POST | `/servers/{server}/start` | `servers:control` |
| POST | `/servers/{server}/stop` | `servers:control` |
| POST | `/servers/{server}/restart` | `servers:control` |
| GET | `/servers/{server}/console` | `console:read` |
| POST | `/servers/{server}/command` | `console:write` |
| GET | `/servers/{server}/players` | `players:read` |
| POST | `/servers/{server}/players/whitelist` | `players:write` |
| DELETE | `/servers/{server}/players/whitelist/{uuid}` | `players:write` |
| POST | `/servers/{server}/players/op` | `players:write` |
| DELETE | `/servers/{server}/players/op/{uuid}` | `players:write` |
| POST | `/servers/{server}/players/ban` | `players:write` |
| DELETE | `/servers/{server}/players/ban/{uuid}` | `players:write` |
| GET | `/events` | `events:read` |
| POST | `/events` | `events:write` |

`stop` and `restart` accept `?timeoutSeconds=`. Left off, they use the same
operator-configured shutdown timeout the panel itself applies.

### `GET /me`

Start here. Tells a plugin what it is and what it can reach, so it can fail at
startup with a clear message rather than at the moment a player needs it.

```json
{
  "tokenName": "hub velocity plugin",
  "serverName": "hub",
  "scopes": ["servers:control", "servers:read"],
  "allowProxyBackends": true,
  "serversInReach": ["hub", "survival", "creative"]
}
```

### `POST /servers/{server}/restart`

```bash
curl -X POST http://panel:8080/api/v1/plugin/servers/survival/restart \
  -H "X-Plugin-Token: $TOKEN"
```

```json
{ "server": "survival", "action": "restart", "accepted": true }
```

Returns **409** if the server cannot take the action right now (already stopping,
mid-update). Restart is a request to the panel, not a completed restart — poll
`/servers/{server}/status` if you need to know when it is back.

### `POST /events`

```json
{ "type": "player.join", "playerName": "steve", "data": "{\"world\":\"overworld\"}" }
```

`202` on acceptance. `type` is yours to define; `data` is opaque JSON the panel
stores but does not interpret.

The event is filed against **the token's server**. There is deliberately no way
to name a different server in the body — nothing here for a caller to forge.

## Responses

| Code | Meaning |
|---|---|
| 401 | Missing `X-Plugin-Token`, or the token is unknown, revoked or expired |
| 403 | Token is real but lacks the required scope |
| 404 | Server is not in this token's reach (or does not exist — same answer) |
| 409 | Server cannot take the action in its current state |

401 is deliberately identical for unknown, revoked and expired tokens. Telling a
caller which of the three it hit is free reconnaissance.

## Writing the plugin side

Minimal Java, no dependencies beyond the JDK:

```java
public final class MineOsClient {
    private final HttpClient http = HttpClient.newHttpClient();
    private final String base;   // "http://panel:8080/api/v1/plugin"
    private final String token;

    public MineOsClient(String base, String token) {
        this.base = base;
        this.token = token;
    }

    public int restart(String server) throws Exception {
        HttpRequest request = HttpRequest.newBuilder()
            .uri(URI.create(base + "/servers/" + URLEncoder.encode(server, UTF_8) + "/restart"))
            .header("X-Plugin-Token", token)
            .timeout(Duration.ofSeconds(10))
            .POST(HttpRequest.BodyPublishers.noBody())
            .build();

        return http.send(request, HttpResponse.BodyHandlers.ofString()).statusCode();
    }
}
```

Two things worth doing:

- **Call `/me` on plugin enable** and log the reach. An operator who forgot
  `allowProxyBackends` finds out at startup instead of when a player is waiting.
- **Never call the panel on the main thread.** A restart request crosses the
  network; blocking the server tick on it will stall the game for every player.

Keep the token out of your JAR and out of version control — read it from the
plugin's config file or an environment variable, the same as a database password.

## Not in this version

- **Panel → plugin push.** Everything is plugin-initiated; a plugin that needs to
  know when something changes must poll. A WebSocket channel is the natural next
  step, and the panel already runs one for the admin console.
- **Cross-server messaging.** Chat bridging and player transfers need that push
  channel first.
