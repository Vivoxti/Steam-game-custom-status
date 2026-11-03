// Replacement for nxapi/dist/util/product.js (swapped in by build.mjs).
//
// The original uses top-level await (not bundleable to the CommonJS script Node SEA needs),
// reads package.json / resources from disk, probes git and docker, and derives its data/cache/log
// directories from env-paths (%APPDATA%/%LOCALAPPDATA%\nxapi-nodejs). This version:
//   - embeds nxapi's own package.json (it carries `__nxapi_git` and `__nxapi_auth.cli.client_id`),
//   - embeds resources/common/remote-config.json as the default remote config,
//   - roots every "paths" entry inside the directory passed with `--data`.
// All derived values (version/release/git/docker/dev) follow the original expressions so that the
// nxapi-auth client assertion is identical to the one the npm-installed `nxapi` command sends.
import * as os from 'node:os';
import * as path from 'node:path';
import pkgJson from '../node_modules/nxapi/package.json';
import defaultRemoteConfig from '../node_modules/nxapi/resources/common/remote-config.json';
import { getDataDir } from './datadir.mjs';

export const embedded_default_remote_config = defaultRemoteConfig;
export const embedded_nxapi_auth_cli_client_id = undefined;
export const embedded_nxapi_auth_app_client_id = undefined;

export const dir = '';
export const pkg = pkgJson;

const match = pkg.version.match(/^(\d+\.\d+\.\d+)-next\b/i);
export const version = match?.[1] ?? pkg.version;
export const release = pkg.__nxapi_release ?? null;
export const docker = pkg.__nxapi_docker ?? null;
export const git = pkg.__nxapi_git ?? null;
export const dev = process.env.NODE_ENV !== 'production' &&
    (!release || process.env.NODE_ENV === 'development');
export const product = 'nxapi ' + version +
    (!release && git ? '-' + git.revision.substr(0, 7) + (git.branch ? ' (' + git.branch + ')' : '') :
        !release ? '-?' : '');

// Only commands that pass --data ever touch these; the placeholder is never written to.
const root = getDataDir() ?? path.join(os.tmpdir(), 'nxapi-bridge-no-data-dir');
export const paths = {
    data: root,
    config: path.join(root, 'config'),
    cache: path.join(root, 'cache'),
    log: path.join(root, 'log'),
    temp: path.join(root, 'temp'),
};
