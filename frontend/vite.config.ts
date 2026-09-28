import vue from '@vitejs/plugin-vue'
import { defineConfig } from 'vite'

// 开发时 /api 与 /ws 代理到 C# 后端（端口 5099，见 backend/Mdk.Api/Properties/launchSettings.json）
export default defineConfig({
  plugins: [vue()],
  server: {
    port: 5173,
    proxy: {
      '/api': { target: 'http://localhost:5099', changeOrigin: true },
      '/ws': { target: 'ws://localhost:5099', ws: true },
    },
  },
})
