// Finds --data <dir> / --data=<dir> in process.argv. Used by the product shim at module load time
// (before bridge.mjs main() runs), so it must not depend on anything else.
import * as path from 'node:path';

export function getDataDir(argv = process.argv.slice(2)) {
    for (let i = 0; i < argv.length; i++) {
        const a = argv[i];
        if (a === '--data') return argv[i + 1] ? path.resolve(argv[i + 1]) : null;
        if (a.startsWith('--data=')) return a.length > 7 ? path.resolve(a.slice(7)) : null;
    }
    return null;
}
