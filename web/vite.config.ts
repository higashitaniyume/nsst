import { fileURLToPath } from 'node:url';
import { defineConfig } from 'vite';

/**
 * The production bundle is written to `web/dist`, and the C# server embeds that
 * directory as an assembly resource, so `npm run build` followed by `dotnet publish`
 * yields a single self-contained binary. `emptyOutDir` guarantees that stale assets
 * never leak into the embedded filesystem.
 *
 * This is an ordinary build output: gitignored, produced by the build, not committed.
 * It used to live under `internal/webui/` and be committed, because Go's
 * `//go:embed all:dist` refuses to compile when the directory is missing.
 */
export default defineConfig({
  build: {
    outDir: fileURLToPath(new URL('./dist', import.meta.url)),
    emptyOutDir: true,
    target: 'es2020',
    assetsDir: 'assets',
    sourcemap: false,
    reportCompressedSize: false,
    chunkSizeWarningLimit: 1024,
  },
  server: {
    port: 5173,
    strictPort: false,
    proxy: {
      // Live streams must not be buffered by the dev proxy.
      '/api': {
        target: 'http://127.0.0.1:8080',
        changeOrigin: true,
        ws: true,
      },
    },
  },
});
