import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

// https://vite.dev/config/
export default defineConfig({
  plugins: [react()],
  server: {
    port: 5173,
    proxy: {
      // In development /api is forwarded to the backend dev server; in production the backend serves the built SPA itself, so it is already same-origin.
      '/api': {
        target: 'http://localhost:5122',
        changeOrigin: true,
      },
    },
  },
})
