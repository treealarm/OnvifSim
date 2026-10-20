import react from '@vitejs/plugin-react';
import { defineConfig } from 'vite';

// `npm run dev` serves the editor with hot reload and passes /api to a simulator running on its default
// port, or on ONVIFSIM_PORT when set (the same variable the simulator reads); `npm run build` writes dist/,
// which the server's build copies next to its binary.
const simulatorPort = process.env.ONVIFSIM_PORT ?? '8080';

export default defineConfig({
  plugins: [react()],
  server: {
    proxy: { '/api': `http://localhost:${simulatorPort}` },
  },
  build: {
    // three.js alone is over the default warning limit, and the editor is served locally.
    chunkSizeWarningLimit: 2000,
  },
});
