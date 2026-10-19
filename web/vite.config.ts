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
    proxy: {
      '/api': process.env.WISHLISTARR_API_URL ?? 'http://localhost:5082',
    },
  },
  test: {
    environment: 'jsdom',
    setupFiles: ['./src/test/setup.ts'],
  },
})
