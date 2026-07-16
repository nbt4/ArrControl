import { defineConfig } from 'vitest/config';
import react from '@vitejs/plugin-react';
export default defineConfig({
  plugins: [react()],
  server: { proxy: { '/api': 'http://localhost:5080' } },
  test: { exclude: ['e2e/**', 'node_modules/**', 'dist/**'] },
});
