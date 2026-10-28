/// <reference types="vitest/config" />
import react from '@vitejs/plugin-react'
import { defineConfig } from 'vite'

export default defineConfig({
  plugins: [react()],
  build: {
    outDir: '../src/Wishlistarr.Api/wwwroot',
    emptyOutDir: true,
  },
  server: {
    // Vitest sets VITEST=true. Fast Refresh's first update aborts the kid home
    // request and leaves the rows on their loading state.
    ...(process.env.VITEST === 'true' ? { hmr: false } : {}),
    proxy: {
      '/api': process.env.WISHLISTARR_API_URL ?? 'http://localhost:5082',
      '/notifications/confirm':
        process.env.WISHLISTARR_API_URL ?? 'http://localhost:5082',
    },
  },
  test: {
    environment: 'jsdom',
    setupFiles: ['./src/test/setup.ts'],
  },
})
