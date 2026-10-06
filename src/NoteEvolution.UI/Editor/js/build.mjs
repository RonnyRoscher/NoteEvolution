// Bundles editor.js with TipTap into one minified ES module. The output is checked in, so building the app does
// not need Node; run `npm install` and `node build.mjs` here after changing editor.js or the package versions.
import { build } from 'esbuild';

await build({
    entryPoints: ['editor.js'],
    outfile: '../../wwwroot/js/editor.bundle.js',
    bundle: true,
    format: 'esm',
    minify: true,
    target: 'es2020',
    legalComments: 'eof',
    logLevel: 'info',
});
