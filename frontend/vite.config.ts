import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

// https://vite.dev/config/
export default defineConfig({
  plugins: [react()],
  server: {
    port: 5173,
    proxy: {
      // 开发时把 /api 转发到后端 dev server；生产由 nginx 反代。
      '/api': {
        target: 'http://localhost:5122',
        changeOrigin: true,
      },
    },
  },
})
