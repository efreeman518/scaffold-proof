import { defineConfig, loadEnv } from 'vite'
import react from '@vitejs/plugin-react'

export default defineConfig(({ mode }) => {
  const env = loadEnv(mode, process.cwd(), '')
  const apiTarget = env.VITE_API_BASE_URL?.trim()
  const port = Number(env.PORT || env.VITE_PORT || 5178)

  return {
    plugins: [react()],
    server: {
      host: '0.0.0.0',
      port,
      proxy: apiTarget
        ? {
            '/api': {
              changeOrigin: true,
              secure: false,
              target: apiTarget,
            },
          }
        : undefined,
    },
    preview: {
      host: '0.0.0.0',
      port,
    },
  }
})
