# nxapi-bridge

Small helper executable used by Steam-game-custom-status to read a Nintendo Switch / Switch 2 friend's
current game. It is a thin CLI over [nxapi](https://github.com/samuelthomas2774/nxapi) (Nintendo Account +
Coral/"Nintendo Switch Online app" API), packaged as a Node.js Single Executable Application.

Nintendo no longer returns your *own* presence from Coral, so the bridge logs in as a secondary "bot"
Nintendo Account that is friends with the main account and reads the main account's presence from the
bot's friend list.

**License: AGPL-3.0-or-later** (this folder only) because the executable bundles nxapi. See [NOTICE](NOTICE).
The repository-root `LICENSE` is unchanged.

## Build

Requires Node.js 24 (tested with 24.18.0) on Windows x64.

```sh
cd tools/nxapi-bridge
npm ci && npm run build
```

Output (git-ignored):

- `dist/nxapi-bridge.exe` - the SEA binary (about 89 MB: it contains the Node runtime).
- `dist/nxapi-bridge.exe.br` - the exe as a plain raw brotli stream (quality 9, no header; decode with
  `System.IO.Compression.BrotliStream`), about 26 MB. Build takes about 1.5 minutes, nearly all of it brotli.
- `dist/bridge.cjs` - the intermediate esbuild bundle (kept for debugging).

How it is built (`build.mjs`): esbuild bundles `bridge.mjs` + nxapi + dependencies into one CommonJS script ->
`node --experimental-sea-config` -> copy of `node.exe` with its (soon invalid) Authenticode signature stripped ->
`postject`. Node 24.18 SEA cannot run ES modules, hence the CommonJS bundle. Two nxapi modules are replaced at
bundle time because they use top-level `await`, `import.meta.url` and disk reads (`shims/product.mjs`, and a
rewrite of `remote-config.js` into an explicit `init()`); the build fails loudly if the rewrite stops matching.
Versions are pinned in `package.json` / `package-lock.json` (nxapi `1.6.1-next.257`).

### Coral client authentication

nxapi's default f-token service (nxapi-znca-api) requires nxapi-auth client authentication. The nxapi npm package
carries its public CLI client id in its own `package.json` (`__nxapi_auth.cli.client_id`); nxapi's CLI turns
that into a client assertion at start-up. The bridge embeds nxapi's `package.json` (via the product shim) and
does the same thing as `cli.ts`, so requests are identical to the npm-installed `nxapi` command. To use your own client
instead, set `NXAPI_AUTH_CLIENT_ID` (and optionally `NXAPI_AUTH_SCOPE`), or nxapi's
`NXAPI_ZNCA_API_CLIENT_ID` / `_SECRET` / `_ASSERTION` variables, in the environment of the process.

The remote configuration (Coral app version, f-service URL) is fetched from nxapi's server and cached in
`<data>/cache/config.json`, as nxapi normally does. An offline start with no cache fails with `network`.

## Protocol

```
nxapi-bridge <command> [--data <dir>] [options]
```

`--data <dir>` is the only place the bridge writes (`<dir>/persist` = session token and cached tokens,
`<dir>/cache` = nxapi remote config). Nothing goes to `%APPDATA%`/`%LOCALAPPDATA%\nxapi-nodejs`.
Every command prints exactly one JSON object + `\n` on stdout (`watch`: one per line); logs go to stderr
(`DEBUG=nxapi:*` enables nxapi debug output there). On failure: `{"ok":false,"error":"<code>","message":"<text>"}`
and exit code 1.

| Command | Result |
|---|---|
| `version` | `{"ok":true,"bridge":"1.0.0","nxapi":"1.6.1-next.257","node":"v24.18.0"}` (no `--data` needed) |
| `auth-url` | `{"ok":true,"url":"https://accounts.nintendo.com/connect/1.0.0/authorize?...","verifier":"..."}` (no `--data` needed) |
| `login --verifier <v> --redirect-url <npf71b963c1b7b6d119://auth#session_token_code=...>` | `{"ok":true,"account":{"naid","nsaid","name","nickname","imageUri"}}`; saves the session in `--data` |
| `status` | `{"ok":true,"loggedIn":true,"account":{...}}` or `{"ok":true,"loggedIn":false}` (no network: uses cached login data) |
| `logout` | `{"ok":true}`; deletes `<data>/persist` |
| `friends` | `{"ok":true,"friends":[{"nsaid","name","imageUri","presence":<presence>}]}` |
| `presence --nsaid <id>` | `{"ok":true,"presence":<presence>}`; `friend_not_found` if not in the bot's friend list |
| `watch --nsaid <id> [--interval <s>]` | long-running; one line per poll `{"ok":true,"ts":"<ISO>","presence":<presence>}` |

`<presence>` = `{"state":"ONLINE|PLAYING|INACTIVE|OFFLINE","updatedAt":<unix s>,"platform":<number|null>,"game":null|{"name","imageUri","shopUri","sysDescription","totalPlayTime":<minutes>,"firstPlayedAt":<unix s>}}`.
An empty Coral `game` object becomes `null`. `platform` is Nintendo's raw value: nxapi's types know
`1` = Nintendo Switch (`NX`) and `2` = Nintendo Switch 2 (`OUNCE`); it is only present while the user is selected in a
game/app, otherwise `null`. Other values are passed through unchanged (not verified live).

Login flow: run `auth-url`, open the URL, sign in as the bot account, on the "Linking an External Account" page
copy the link of "Select this person" (starts with `npf71b963c1b7b6d119://auth`) and pass it to `login` together with
the `verifier`. The OAuth `state` is derived from the verifier, so `login` can run in a separate process and rejects
redirects that belong to a different `auth-url`.

`watch`: `--interval` defaults to 120 s, minimum 30 s (lower values are raised). The Coral token is obtained once and reused
(nxapi renews it itself when Nintendo reports it expired). Transient failures print an error line and continue with
exponential backoff (up to 15 min); `not_logged_in` and `auth_failed` print the error line and exit 1. It exits 0 when stdin reaches EOF
and on SIGINT/SIGTERM/SIGBREAK.

Error codes: `not_logged_in`, `invalid_redirect`, `auth_failed`, `network`, `friend_not_found`, `unknown`, plus
`usage` (bad arguments/unknown command), `rate_limited` (nxapi's own limit of 4 sign-ins/hour when not run from a
terminal; transient) and `coral_error` (other HTTP/Coral API error; transient).

## Files

`bridge.mjs` (CLI), `build.mjs` (build), `shims/product.mjs` + `shims/datadir.mjs` (replacement for nxapi's
`util/product.js`, rooted in `--data`), `package.json`, `package-lock.json`, `NOTICE`.
