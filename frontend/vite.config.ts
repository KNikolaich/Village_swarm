/// <reference types="vitest/config" />
import path from 'node:path'
import tailwindcss from '@tailwindcss/vite'
import react from '@vitejs/plugin-react'
import { defineConfig } from 'vite'
import { VitePWA } from 'vite-plugin-pwa'
import { espWebToolsI18n } from './vite-plugins/esp-web-tools-i18n'

export default defineConfig({
  plugins: [
    react(),
    espWebToolsI18n(),
    tailwindcss(),
    // PWA (spec 8.1): installable on the phone, app shell cached offline. API data is never cached here.
    VitePWA({
      registerType: 'autoUpdate',
      includeAssets: ['apple-touch-icon.png', 'favicon.ico', 'favicon.svg'],
      // public/manifest.webmanifest is linked from index.html by hand and is only Caddy's fallback: the api builds the real one (it carries the
      // hive alias, backend HiveManifest). The plugin would precache it and the service worker would keep serving an old name.
      manifest: false,
      workbox: {
        navigateFallback: '/index.html',
        navigateFallbackDenylist: [/^\/api\//, /^\/hubs\//, /^\/swagger/, /^\/openapi/],
        runtimeCaching: [],
      },
    }),
  ],
  test: { include: ['src/**/*.test.ts'] },
  // Served as is so espWebToolsI18n can rewrite the flashing dialogs in dev too (pre-bundled deps skip plugins).
  optimizeDeps: { exclude: ['esp-web-tools'] },
  resolve: {
    alias: { '@': path.resolve(__dirname, './src') },
  },
  server: {
    // Hive.Api in development (backend/src/Hive.Api/Properties/launchSettings.json); HIVE_API overrides it.
    proxy: {
      '/api': process.env.HIVE_API ?? 'http://localhost:5080',
      '/hubs': { target: process.env.HIVE_API ?? 'http://localhost:5080', ws: true },
    },
  },
})
