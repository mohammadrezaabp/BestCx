import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

// The built SPA is copied into the host image's wwwroot and served by the same
// process as the APIs, so relative /v1 calls work without CORS in production.
export default defineConfig({
  plugins: [react()],
  build: {
    outDir: 'dist',
    sourcemap: false,
    chunkSizeWarningLimit: 900,
  },
  server: {
    port: 5173,
    proxy: {
      '/v1': { target: 'http://localhost:18080', changeOrigin: true },
      '/health': { target: 'http://localhost:18080', changeOrigin: true },
    },
  },
});
