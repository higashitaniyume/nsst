import { fileURLToPath } from 'node:url';
import { defineConfig } from 'vite';

/**
 * The production bundle is written straight into the Go package that embeds it
 * with go:embed, so `npm run build` followed by `go build` yields a single
 * self-contained binary. `emptyOutDir` guarantees that stale assets never leak
 * into the embedded filesystem.
 */
export default defineConfig({
  build: {
    outDir: fileURLToPath(new URL('../internal/webui/dist', import.meta.url)),
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
