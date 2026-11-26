import { defineConfig, loadEnv } from 'vite';
import react from '@vitejs/plugin-react';
import path from 'node:path';

// UGES SD Kontrol — SD/.env dosyasından BACKEND_PORT ve FRONTEND_PORT değerlerini okur
export default defineConfig(({ mode }) => {
  const rootEnv = loadEnv(mode, path.resolve(__dirname, '..'), '');
  const frontendPort = Number(rootEnv.FRONTEND_PORT || 3100);
  const backendPort = Number(rootEnv.BACKEND_PORT || 5100);

  return {
    plugins: [react()],
    server: {
      port: frontendPort,
      strictPort: true,
      host: true,
      proxy: {
        '/api': { target: `http://127.0.0.1:${backendPort}`, changeOrigin: true },
      },
    },
  };
});
