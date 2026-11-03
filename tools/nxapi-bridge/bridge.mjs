// nxapi-bridge: tiny CLI around nxapi's Nintendo Account / Coral (NSO app) client.
// Protocol: see README.md. Every command prints exactly one JSON object line on stdout
// (`watch` prints one per poll); everything else (nxapi logs/warnings) goes to stderr.
//
// The code paths used here are the ones `nxapi nso auth|token|friends|presence` use:
//   login   -> NintendoAccountSessionAuthorisationCoral + getNintendoAccountSessionToken + getToken()
//   friends -> getToken() + CoralApi#getFriendList()      (same as `nxapi nso friends`)
//   watch   -> getToken() once, then CoralApi#getFriendList() every N seconds (the same call
//              ZncDiscordPresence makes for friends); CoralApi renews its own token on expiry.
import * as fs from 'node:fs';
import * as crypto from 'node:crypto';
import * as path from 'node:path';
import { setGlobalDispatcher } from 'undici';

import bridgePkg from './package.json';
import { getDataDir } from './shims/datadir.mjs';

// nxapi internals are imported by file path (the package `exports` map only exposes a small public API,
// and the client-auth provider / token store used by nxapi's own CLI are internal).
import { addUserAgent } from './node_modules/nxapi/dist/util/useragent.js';
import { NxapiClientAssertionProvider, setClientAssertionProvider } from './node_modules/nxapi/dist/util/nxapi-auth.js';
import { pkg as nxapiPkg } from './node_modules/nxapi/dist/util/product.js';
import { initStorage } from './node_modules/nxapi/dist/util/storage.js';
import { buildEnvironmentProxyAgent } from './node_modules/nxapi/dist/util/undici-proxy.js';
import { Jwt } from './node_modules/nxapi/dist/util/jwt.js';
import { ErrorResponse } from './node_modules/nxapi/dist/api/util.js';
import {
    NintendoAccountSessionAuthorisationCoral, ZNCA_CLIENT_ID, CoralErrorResponse,
} from './node_modules/nxapi/dist/api/coral.js';
import { CoralStatus, PresenceState } from './node_modules/nxapi/dist/api/coral-types.js';
import {
    getNintendoAccountSessionToken, NintendoAccountAuthErrorResponse, NintendoAccountErrorResponse,
} from './node_modules/nxapi/dist/api/na.js';
import { getToken } from './node_modules/nxapi/dist/common/auth/coral.js';
import {
    InvalidNintendoAccountTokenError,
} from './node_modules/nxapi/dist/common/auth/na.js';
import { RateLimitError, MembershipRequiredError } from './node_modules/nxapi/dist/common/auth/util.js';
import { init as initRemoteConfig } from './node_modules/nxapi/dist/common/remote-config.js';

const BRIDGE_VERSION = bridgePkg.version;
const USER_AGENT = `SteamGameCustomStatus-NintendoBridge/${BRIDGE_VERSION} (+https://github.com/Vivoxti/Steam-game-custom-status)`;
const REDIRECT_PREFIX = 'npf' + ZNCA_CLIENT_ID + '://auth';

const DEFAULT_INTERVAL = 120;
const MIN_INTERVAL = 30;
const MAX_BACKOFF = 15 * 60;

// ---------------------------------------------------------------------------------------------
// Output helpers
// ---------------------------------------------------------------------------------------------

// Anything that accidentally uses console.log must not corrupt the JSON stream.
console.log = console.info = console.debug = (...args) => console.error(...args);

function writeStdout(text) {
    const buf = Buffer.from(text, 'utf-8');
    let off = 0;
    while (off < buf.length) {
        try {
            off += fs.writeSync(1, buf, off);
        } catch (err) {
            if (err.code === 'EAGAIN') continue;
            if (err.code === 'EPIPE') process.exit(1);
            throw err;
        }
    }
}

function emit(obj) {
    writeStdout(JSON.stringify(obj) + '\n');
}

function finish(code) {
    process.exit(code);
}

class BridgeError extends Error {
    constructor(code, message, extra) {
        super(message);
        this.code = code;
        this.extra = extra;
    }
}

// Maps whatever nxapi/undici throws to a protocol error code. `fatal` = retrying cannot help
// without user action (used by `watch` to decide between "keep going" and "exit 1").
function classifyError(err) {
    if (err instanceof BridgeError) {
        return { code: err.code, message: err.message, fatal: ['not_logged_in', 'auth_failed', 'usage', 'invalid_redirect'].includes(err.code) };
    }
    const message = err instanceof Error ? err.message : String(err);

    if (err instanceof InvalidNintendoAccountTokenError ||
        (err instanceof NintendoAccountAuthErrorResponse && ['invalid_grant', 'invalid_request'].includes(err.data?.error)) ||
        err instanceof MembershipRequiredError) {
        return { code: 'auth_failed', message: describe(err), fatal: true };
    }
    if (err instanceof CoralErrorResponse && [CoralStatus.NSA_NOT_LINKED, CoralStatus.ACCOUNT_DISABLED, CoralStatus.MEMBERSHIP_REQUIRED].includes(err.status)) {
        return { code: 'auth_failed', message: describe(err), fatal: true };
    }
    if (err instanceof RateLimitError) {
        return { code: 'rate_limited', message: 'Too many authentication attempts; waiting before trying again (' + err.key + ')', fatal: false };
    }
    if (err instanceof ErrorResponse) {
        const status = err.response?.status;
        if (status === 401 || status === 403) {
            // e.g. nxapi-znca-api rejecting the client, or Nintendo rejecting the token
            return { code: 'auth_failed', message: describe(err), fatal: false };
        }
        if (err instanceof NintendoAccountErrorResponse || err instanceof NintendoAccountAuthErrorResponse) {
            return { code: status >= 500 || status === 429 ? 'network' : 'auth_failed', message: describe(err), fatal: false };
        }
        return { code: status >= 500 || status === 429 ? 'network' : 'coral_error', message: describe(err), fatal: false };
    }
    if (isNetworkError(err)) {
        return { code: 'network', message: describe(err), fatal: false };
    }
    return { code: 'unknown', message: describe(err), fatal: false };
}

function describe(err) {
    let msg = err instanceof Error ? err.message : String(err);
    // undici reports every connection problem as the unhelpful "fetch failed"; the cause has the detail
    for (let c = err?.cause, i = 0; c && i < 3; c = c.cause, i++) {
        const detail = c.code || c.message;
        if (detail && !msg.includes(detail)) { msg += ' (' + detail + ')'; break; }
    }
    const status = err?.response?.status;
    const detail = err?.data?.error_description ?? err?.data?.errorMessage ?? err?.data?.error;
    return msg + (status ? ' (HTTP ' + status + ')' : '') + (detail && !msg.includes(detail) ? ': ' + detail : '');
}

const NETWORK_CODES = new Set([
    'ECONNRESET', 'ECONNREFUSED', 'ENOTFOUND', 'EAI_AGAIN', 'ETIMEDOUT', 'ENETUNREACH', 'EHOSTUNREACH',
    'EPIPE', 'ECONNABORTED', 'CERT_HAS_EXPIRED',
]);
function isNetworkError(err, depth = 0) {
    if (!err || depth > 4) return false;
    if (err.name === 'AbortError' || err.name === 'TimeoutError' || err.message === 'Timeout') return true;
    if (typeof err.code === 'string' && (NETWORK_CODES.has(err.code) || err.code.startsWith('UND_ERR'))) return true;
    if (err instanceof TypeError && /fetch failed/i.test(err.message)) return true;
    if (err instanceof AggregateError && err.errors?.some(e => isNetworkError(e, depth + 1))) return true;
    return isNetworkError(err.cause, depth + 1);
}

// ---------------------------------------------------------------------------------------------
// Argument parsing
// ---------------------------------------------------------------------------------------------

const VALUE_OPTIONS = new Set(['data', 'verifier', 'redirect-url', 'nsaid', 'interval']);

function parseArgs(argv) {
    const opts = {};
    const positional = [];
    for (let i = 0; i < argv.length; i++) {
        const a = argv[i];
        if (!a.startsWith('--')) { positional.push(a); continue; }
        let [name, value] = a.slice(2).split(/=(.*)/s, 2);
        if (!VALUE_OPTIONS.has(name)) throw new BridgeError('usage', 'Unknown option --' + name);
        if (value === undefined) {
            value = argv[++i];
            if (value === undefined) throw new BridgeError('usage', 'Option --' + name + ' requires a value');
        }
        opts[name] = value;
    }
    return { command: positional[0], opts };
}

function requireOpt(opts, name) {
    if (!opts[name]) throw new BridgeError('usage', 'Missing required option --' + name);
    return opts[name];
}

// ---------------------------------------------------------------------------------------------
// nxapi setup
// ---------------------------------------------------------------------------------------------

function setupNxapi() {
    addUserAgent(USER_AGENT);

    // Same selection logic as nxapi's cli.ts: use the nxapi-auth client id that the nxapi npm package
    // ships in its package.json (`__nxapi_auth.cli.client_id`); NXAPI_AUTH_CLIENT_ID overrides it.
    // (NXAPI_ZNCA_API_CLIENT_ID/_SECRET/_ASSERTION are handled inside nxapi itself and take precedence.)
    const cliClientId = nxapiPkg.__nxapi_auth?.cli?.client_id;
    if (process.env.NXAPI_AUTH_CLIENT_ID) {
        setClientAssertionProvider(new NxapiClientAssertionProvider(
            process.env.NXAPI_AUTH_CLIENT_ID, undefined, process.env.NXAPI_AUTH_SCOPE ?? 'ca:gf ca:er ca:dr'));
    } else if (cliClientId) {
        setClientAssertionProvider(new NxapiClientAssertionProvider(cliClientId, undefined, 'ca:gf ca:er ca:dr ca:na'));
    }

    // Honours HTTP(S)_PROXY / NO_PROXY like the nxapi command does.
    setGlobalDispatcher(buildEnvironmentProxyAgent());
}

// Remote config (znca version + f-API endpoint) is fetched/cached by nxapi; our product shim roots its
// cache in <data>/cache. Must complete before any Coral login (see build.mjs for why it is explicit).
let remoteConfigReady = null;
function ensureRemoteConfig() {
    return remoteConfigReady ??= initRemoteConfig().catch(err => { remoteConfigReady = null; throw err; });
}

let storagePromise = null;
function getStorage(dataDir) {
    return storagePromise ??= initStorage(dataDir);
}

function needDataDir() {
    const dir = getDataDir();
    if (!dir) throw new BridgeError('usage', 'Missing required option --data <dir>');
    return dir;
}

// ---------------------------------------------------------------------------------------------
// Session helpers
// ---------------------------------------------------------------------------------------------

async function getSavedSessionToken(storage) {
    const selected = await storage.getItem('SelectedUser');
    if (!selected) return null;
    const token = await storage.getItem('NintendoAccountToken.' + selected);
    return token || null;
}

function sessionTokenState(token) {
    try {
        const [jwt] = Jwt.decode(token);
        if (jwt.payload.exp <= Date.now() / 1000) return 'expired';
        return 'ok';
    } catch {
        return 'invalid';
    }
}

function accountFromAuthData(data) {
    const su = data.nsoAccount?.user ?? {};
    return {
        naid: data.user?.id ?? null,
        nsaid: su.nsaId ?? null,
        name: su.name ?? null,
        nickname: data.user?.nickname ?? null,
        imageUri: su.imageUri ?? null,
    };
}

async function requireSessionToken(storage) {
    const token = await getSavedSessionToken(storage);
    if (!token) throw new BridgeError('not_logged_in', 'Not logged in');
    const state = sessionTokenState(token);
    if (state !== 'ok') {
        throw new BridgeError('auth_failed', 'Saved Nintendo Account session token is ' + state + '; sign in again');
    }
    return token;
}

// Coral client for the saved session (reuses the cached Coral token while valid, logs in again
// otherwise; the returned client renews the token itself when Nintendo reports it expired).
async function openCoral(storage) {
    const token = await requireSessionToken(storage);
    await ensureRemoteConfig();
    const { nso, data } = await getToken(storage, token, undefined);
    return { nso, data, token };
}

function mapPresence(presence) {
    const p = presence ?? {};
    const g = p.game;
    const hasGame = g && typeof g === 'object' && typeof g.name === 'string' && g.name !== '';
    return {
        state: p.state ?? PresenceState.OFFLINE,
        updatedAt: p.updatedAt ?? 0,
        platform: typeof p.platform === 'number' ? p.platform : null,
        game: hasGame ? {
            name: g.name,
            imageUri: g.imageUri ?? '',
            shopUri: g.shopUri ?? '',
            sysDescription: g.sysDescription ?? '',
            totalPlayTime: g.totalPlayTime ?? 0,
            firstPlayedAt: g.firstPlayedAt ?? 0,
        } : null,
    };
}

function mapFriend(f) {
    return { nsaid: f.nsaId, name: f.name, imageUri: f.imageUri, presence: mapPresence(f.presence) };
}

// ---------------------------------------------------------------------------------------------
// Commands
// ---------------------------------------------------------------------------------------------

function deriveState(verifier) {
    return crypto.createHash('sha256').update('nxapi-bridge-state:' + verifier).digest('base64url');
}

async function cmdVersion() {
    emit({
        ok: true,
        bridge: BRIDGE_VERSION,
        nxapi: String(nxapiPkg.version).split('+')[0],
        node: process.version,
    });
}

async function cmdAuthUrl() {
    // Same authoriser as `nxapi nso auth`. nxapi would keep the random `state` in memory for the second
    // step; the bridge runs login in a separate process, so derive `state` from the verifier instead
    // (login recomputes it and checks that the redirect belongs to this URL).
    const auth = NintendoAccountSessionAuthorisationCoral.create();
    const url = new URL(auth.authorise_url);
    url.searchParams.set('state', deriveState(auth.verifier));
    emit({ ok: true, url: url.toString(), verifier: auth.verifier });
}

function parseRedirect(redirectUrl) {
    const raw = redirectUrl.trim();
    if (!raw.toLowerCase().startsWith(REDIRECT_PREFIX.toLowerCase())) {
        throw new BridgeError('invalid_redirect', 'Redirect URL must start with ' + REDIRECT_PREFIX);
    }
    const hashIndex = raw.indexOf('#');
    if (hashIndex < 0) throw new BridgeError('invalid_redirect', 'Redirect URL has no #fragment with the session token code');
    return new URLSearchParams(raw.slice(hashIndex + 1));
}

async function cmdLogin(opts) {
    const dataDir = needDataDir();
    const verifier = requireOpt(opts, 'verifier');
    const params = parseRedirect(requireOpt(opts, 'redirect-url'));

    if (params.has('error')) {
        throw new BridgeError('invalid_redirect',
            'Nintendo returned an error: ' + (params.get('error_description') ?? params.get('error')));
    }
    const code = params.get('session_token_code');
    if (!code) throw new BridgeError('invalid_redirect', 'Redirect URL does not contain session_token_code');
    if (params.get('state') !== deriveState(verifier)) {
        throw new BridgeError('invalid_redirect', 'Redirect URL does not belong to this login attempt (state mismatch); generate a new URL and try again');
    }

    const storage = await getStorage(dataDir);
    await ensureRemoteConfig();

    // Same two steps as NintendoAccountSessionAuthorisation#getSessionToken + `nxapi nso auth`
    let sessionToken;
    try {
        sessionToken = await getNintendoAccountSessionToken(code, verifier, ZNCA_CLIENT_ID);
    } catch (err) {
        if (err instanceof NintendoAccountAuthErrorResponse || err instanceof NintendoAccountErrorResponse) {
            if (err.response?.status >= 400 && err.response?.status < 500) {
                throw new BridgeError('auth_failed', 'Nintendo rejected the login code (it may have expired or been used already): ' + describe(err));
            }
        }
        throw err;
    }

    const { data } = await getToken(storage, sessionToken.session_token, undefined);

    // Persist like `nxapi nso auth/token` do so getToken() can find the session later.
    await storage.setItem('NintendoAccountToken.' + data.user.id, sessionToken.session_token);
    await storage.setItem('NintendoAccountIds', [data.user.id]);
    await storage.setItem('SelectedUser', data.user.id);

    emit({ ok: true, account: accountFromAuthData(data) });
}

async function cmdStatus() {
    const dataDir = needDataDir();
    const storage = await getStorage(dataDir);
    const token = await getSavedSessionToken(storage);
    if (!token) return emit({ ok: true, loggedIn: false });

    const tokenState = sessionTokenState(token);
    if (tokenState !== 'ok') return emit({ ok: true, loggedIn: false, reason: 'session_' + tokenState });

    // Cached Coral login data holds the account details; no network needed (it may be expired, which
    // is irrelevant for the account info).
    const cached = await storage.getItem('NsoToken.' + token);
    if (cached) return emit({ ok: true, loggedIn: true, account: accountFromAuthData(cached) });

    const { data } = await openCoral(storage);
    emit({ ok: true, loggedIn: true, account: accountFromAuthData(data) });
}

async function cmdLogout() {
    const dataDir = needDataDir();
    // Drop the node-persist store (session token, NA/Coral tokens, rate-limit counters).
    fs.rmSync(path.join(dataDir, 'persist'), { recursive: true, force: true });
    emit({ ok: true });
}

async function cmdFriends() {
    const storage = await getStorage(needDataDir());
    const { nso } = await openCoral(storage);
    const list = await nso.getFriendList();
    emit({ ok: true, friends: list.friends.map(mapFriend) });
}

async function cmdPresence(opts) {
    const nsaid = requireOpt(opts, 'nsaid');
    const storage = await getStorage(needDataDir());
    const { nso } = await openCoral(storage);
    const list = await nso.getFriendList();
    const friend = list.friends.find(f => f.nsaId === nsaid);
    if (!friend) throw new BridgeError('friend_not_found', 'No friend with nsaid ' + nsaid + ' in this account\'s friend list');
    emit({ ok: true, presence: mapPresence(friend.presence) });
}

async function cmdWatch(opts) {
    const nsaid = requireOpt(opts, 'nsaid');
    let interval = DEFAULT_INTERVAL;
    if (opts.interval !== undefined) {
        interval = Number(opts.interval);
        if (!Number.isFinite(interval)) throw new BridgeError('usage', '--interval must be a number of seconds');
        interval = Math.max(MIN_INTERVAL, interval);
    }
    const dataDir = needDataDir();
    const storage = await getStorage(dataDir);

    // Stop when the parent closes our stdin, and on signals.
    process.stdin.on('end', () => finish(0));
    process.stdin.on('close', () => finish(0));
    process.stdin.on('error', () => finish(0));
    process.stdin.resume();
    process.on('SIGINT', () => finish(0));
    process.on('SIGTERM', () => finish(0));
    process.on('SIGBREAK', () => finish(0));

    let client = null;   // CoralApi, reused between polls
    let failures = 0;

    for (;;) {
        const ts = new Date().toISOString();
        try {
            if (!client) client = (await openCoral(storage)).nso;
            const list = await client.getFriendList();
            const friend = list.friends.find(f => f.nsaId === nsaid);
            if (!friend) throw new BridgeError('friend_not_found', 'No friend with nsaid ' + nsaid + ' in this account\'s friend list');
            failures = 0;
            emit({ ok: true, ts, presence: mapPresence(friend.presence) });
        } catch (err) {
            const { code, message, fatal } = classifyError(err);
            emit({ ok: false, error: code, message, ts });
            if (fatal) return finish(1);
            failures++;
            if (err instanceof CoralErrorResponse &&
                [CoralStatus.INVALID_TOKEN, CoralStatus.TOKEN_EXPIRED, CoralStatus.UNAUTHORISED].includes(err.status)) {
                // Cached Coral token is unusable: forget it so the next attempt performs a fresh login.
                client = null;
                try {
                    const token = await getSavedSessionToken(storage);
                    if (token) await storage.removeItem('NsoToken.' + token);
                } catch { /* ignore */ }
            } else if (code === 'auth_failed' || code === 'coral_error') {
                client = null;
            }
        }
        const delay = failures ? Math.min(MAX_BACKOFF, interval * 2 ** Math.min(failures, 6)) : interval;
        await new Promise(resolve => setTimeout(resolve, delay * 1000));
    }
}

// ---------------------------------------------------------------------------------------------

async function main() {
    // In a Node SEA process.argv is [exe, exe?, ...args]; in plain node [node, script, ...args].
    // Both have the user arguments from index 2 on.
    const { command, opts } = parseArgs(process.argv.slice(2));

    switch (command) {
        case 'version': return cmdVersion();
        case 'auth-url': setupNxapi(); return cmdAuthUrl();
        case 'login': setupNxapi(); return cmdLogin(opts);
        case 'status': setupNxapi(); return cmdStatus();
        case 'logout': return cmdLogout();
        case 'friends': setupNxapi(); return cmdFriends();
        case 'presence': setupNxapi(); return cmdPresence(opts);
        case 'watch': setupNxapi(); return cmdWatch(opts);
        default:
            throw new BridgeError('usage', command
                ? 'Unknown command ' + command
                : 'Usage: nxapi-bridge <version|auth-url|login|status|logout|friends|presence|watch> [--data <dir>] ...');
    }
}

process.on('unhandledRejection', err => {
    const { code, message } = classifyError(err);
    emit({ ok: false, error: code, message });
    finish(1);
});

// (no top-level await: the SEA entry must be a CommonJS script)
main().then(() => finish(0), err => {
    const { code, message } = classifyError(err);
    if (process.env.DEBUG && err instanceof Error) console.error(err.stack);
    emit({ ok: false, error: code, message });
    finish(1);
});
