// Builds dist/nxapi-bridge.exe (Node Single Executable Application) and dist/nxapi-bridge.exe.br.
//
//   npm ci && npm run build
//
// Steps: esbuild (bridge.mjs + nxapi + deps -> one CommonJS script) -> SEA blob -> copy node.exe,
// strip its (now invalid anyway) Authenticode signature -> postject -> brotli.
import * as esbuild from 'esbuild';
import * as fs from 'node:fs';
import * as path from 'node:path';
import * as zlib from 'node:zlib';
import * as crypto from 'node:crypto';
import { execFileSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';

const root = path.dirname(fileURLToPath(import.meta.url));
const dist = path.join(root, 'dist');
const nxapiDir = path.join(root, 'node_modules', 'nxapi');

const SEA_FUSE = 'NODE_SEA_FUSE_fce680ab2cc467b6e072b8b5df1996b2';

fs.rmSync(dist, { recursive: true, force: true });
fs.mkdirSync(dist, { recursive: true });

// --- 1. bundle ----------------------------------------------------------------------------------

/** Source rewrites of nxapi modules (each must match or the build fails, so an nxapi bump can't silently break). */
const REMOTE_CONFIG_REWRITES = [
    // default config: the embedded copy is always provided by our product shim, so never read it from disk
    [/JSON\.parse\(await fs\.readFile\(path\.join\(dir, 'resources', 'common', 'remote-config\.json'\), 'utf-8'\)\)/, '{}'],
    // local debug override file (dev builds only): drop
    [/const debug_fixed_config = !dev \? null :[\s\S]*?\|\| null;\n/, 'const debug_fixed_config = null;\n'],
    // top-level-await loading of the remote config -> explicit init() called by bridge.mjs
    [/export const cache = [\s\S]*$/, `
export let cache = null;
const config = {};
export default config;
/** Loads (fetches or reads from <data>/cache) the remote config and fills the exported default object. */
export async function init() {
    cache = mode === RemoteConfigMode.DISABLE ? null :
        mode === RemoteConfigMode.OPPORTUNISTIC ? await tryLoadRemoteConfig() :
            await loadRemoteConfig();
    Object.assign(config, cache?.data ?? default_config);
    if (cache && !config.require_version.includes(version)) {
        throw new Error('nxapi update required');
    }
    debug('using config', RemoteConfigMode[mode], config);
}
`],
];

const nxapiPatchPlugin = {
    name: 'nxapi-patch',
    setup(build) {
        // util/product.js -> shims/product.mjs
        build.onResolve({ filter: /(^|\/)product\.js$/ }, args => {
            const target = path.resolve(args.resolveDir, args.path);
            if (target === path.join(nxapiDir, 'dist', 'util', 'product.js')) {
                return { path: path.join(root, 'shims', 'product.mjs') };
            }
            return null;
        });

        build.onLoad({ filter: /remote-config\.js$/ }, async args => {
            if (args.path !== path.join(nxapiDir, 'dist', 'common', 'remote-config.js')) return null;
            let src = await fs.promises.readFile(args.path, 'utf-8');
            for (const [re, replacement] of REMOTE_CONFIG_REWRITES) {
                if (!re.test(src)) throw new Error('remote-config.js rewrite no longer matches: ' + re);
                src = src.replace(re, replacement);
            }
            return { contents: src, loader: 'js', resolveDir: path.dirname(args.path) };
        });
    },
};

const bundle = path.join(dist, 'bridge.cjs');
const result = await esbuild.build({
    entryPoints: [path.join(root, 'bridge.mjs')],
    outfile: bundle,
    bundle: true,
    platform: 'node',
    format: 'cjs',
    target: 'node24',
    legalComments: 'none',
    logLevel: 'info',
    plugins: [nxapiPatchPlugin],
    loader: { '.json': 'json' },
    // Fail loudly if anything we did not expect needs resolving.
    external: [],
    metafile: true,
    // Warnings that matter (e.g. unsupported `import.meta`) are surfaced below.
});

const warnings = result.warnings.filter(w => /import\.meta|eval/i.test(w.text));
if (warnings.length) {
    console.error(await esbuild.formatMessages(warnings, { kind: 'warning', color: false }));
    throw new Error('Unexpected bundling warnings (see above)');
}

// --- 2. SEA blob --------------------------------------------------------------------------------

const seaConfig = path.join(dist, 'sea-config.json');
const blob = path.join(dist, 'sea-prep.blob');
fs.writeFileSync(seaConfig, JSON.stringify({
    main: bundle,
    output: blob,
    disableExperimentalSEAWarning: true,
    useSnapshot: false,
    useCodeCache: false,
}));
execFileSync(process.execPath, ['--experimental-sea-config', seaConfig], { stdio: 'inherit' });

// --- 3. exe = node.exe + blob -------------------------------------------------------------------

const exe = path.join(dist, 'nxapi-bridge.exe');
fs.copyFileSync(process.execPath, exe);
stripAuthenticodeSignature(exe);

const postject = path.join(root, 'node_modules', 'postject', 'dist', 'cli.js');
execFileSync(process.execPath, [
    postject, exe, 'NODE_SEA_BLOB', blob, '--sentinel-fuse', SEA_FUSE,
], { stdio: 'inherit' });

fs.rmSync(blob);
fs.rmSync(seaConfig);

// --- 4. brotli ----------------------------------------------------------------------------------

const exeData = fs.readFileSync(exe);
const t0 = Date.now();
const br = zlib.brotliCompressSync(exeData, {
    params: {
        [zlib.constants.BROTLI_PARAM_QUALITY]: 9,
        [zlib.constants.BROTLI_PARAM_SIZE_HINT]: exeData.length,
        [zlib.constants.BROTLI_PARAM_LGWIN]: 24,
    },
});
fs.writeFileSync(exe + '.br', br); // plain raw brotli stream, no header
const roundTrip = zlib.brotliDecompressSync(br);
if (!crypto.timingSafeEqual(crypto.createHash('sha256').update(roundTrip).digest(),
    crypto.createHash('sha256').update(exeData).digest())) {
    throw new Error('brotli round-trip mismatch');
}

const mb = n => (n / 1024 / 1024).toFixed(1) + ' MB';
console.log(`bundle:  ${mb(fs.statSync(bundle).size)} (${path.relative(root, bundle)}, kept for debugging)`);
console.log(`exe:     ${mb(exeData.length)} (${exeData.length} bytes)`);
console.log(`exe.br:  ${mb(br.length)} (${br.length} bytes, ${((Date.now() - t0) / 1000).toFixed(1)} s)`);

// ------------------------------------------------------------------------------------------------

/**
 * Removes the Authenticode signature from a PE file in place. node.exe is signed by the Node.js
 * project; after the SEA blob is injected the signature is invalid, and an invalid signature is
 * worse than none. (Equivalent of `signtool remove /s`, without needing the Windows SDK.)
 */
function stripAuthenticodeSignature(file) {
    const buf = fs.readFileSync(file);
    const peOffset = buf.readUInt32LE(0x3c);
    if (buf.toString('latin1', peOffset, peOffset + 4) !== 'PE\0\0') throw new Error('not a PE file');
    const optional = peOffset + 24;
    const magic = buf.readUInt16LE(optional);
    const dirsOffset = optional + (magic === 0x20b ? 112 : 96);
    const secEntry = dirsOffset + 4 * 8; // IMAGE_DIRECTORY_ENTRY_SECURITY
    const certOffset = buf.readUInt32LE(secEntry);
    const certSize = buf.readUInt32LE(secEntry + 4);
    if (!certOffset || !certSize) return;
    if (certOffset + certSize !== buf.length && certOffset + certSize + 8 < buf.length) {
        console.warn('signature is not at the end of the file; leaving it in place');
        return;
    }
    buf.writeUInt32LE(0, secEntry);
    buf.writeUInt32LE(0, secEntry + 4);
    buf.writeUInt32LE(0, optional + 64); // CheckSum
    fs.writeFileSync(file, buf.subarray(0, certOffset));
}
