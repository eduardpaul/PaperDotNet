import { fileURLToPath, URL } from 'node:url';
import tailwindcss from '@tailwindcss/vite';
import { tanstackRouter } from '@tanstack/router-plugin/vite';
import react from '@vitejs/plugin-react';
import { defineConfig } from 'vitest/config';

// The API during development (dotnet run --project src/PaperDotNet.Host); the browser sees one origin.
const api = process.env.PAPERDOTNET_API ?? 'http://localhost:5080';
const proxied = ['/v1.0', '/connect', '/.well-known', '/health', '/version', '/openapi'];

export default defineConfig({
  plugins: [tanstackRouter({ target: 'react', autoCodeSplitting: true }), react(), tailwindcss()],
  resolve: { alias: { '@': fileURLToPath(new URL('./src', import.meta.url)) } },
  server: {
    port: 5173,
    proxy: Object.fromEntries(proxied.map((path) => [path, { target: api, changeOrigin: false }])),
  },
  build: { outDir: 'dist', sourcemap: true, chunkSizeWarningLimit: 1500 },
  test: { include: ['src/**/*.test.ts'], environment: 'node' },
});
