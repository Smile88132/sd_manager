import { defineConfig, loadEnv } from 'vite';
import react from '@vitejs/plugin-react';
import path from 'node:path';

// UGES SD Kontrol — derleme çıktısını doğrudan ../backend/wwwroot içine yazar (tek .exe standalone çalışması için)
export default defineConfig(({ mode }) => {
  const rootEnv = loadEnv(mode, path.resolve(__dirname, '..'), '');
  const frontendPort = Number(rootEnv.FRONTEND_PORT || 3100);
  const backendPort = Number(rootEnv.BACKEND_PORT || 5100);

  return {
    plugins: [react()],
    build: {
      outDir: path.resolve(__dirname, '../backend/wwwroot'),
      emptyOutDir: true,
    },
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
