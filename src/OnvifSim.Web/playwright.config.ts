import { defineConfig } from '@playwright/test';
import { discoveryPort, webPort } from './e2e/ports';

export default defineConfig({
  testDir: 'e2e',
  timeout: 60_000,
  use: {
    baseURL: `http://localhost:${webPort}`,
    viewport: { width: 1500, height: 950 },
    screenshot: 'only-on-failure',
    // WebGL without a GPU, as on CI machines.
    launchOptions: { args: ['--use-gl=angle', '--use-angle=swiftshader', '--enable-unsafe-swiftshader'] },
  },
  webServer: {
    command: `node e2e/start-server.mjs --port ${webPort} --rtsp-port 0 --rtp-port 0 --onvif-base-port 0 --discovery-port ${discoveryPort} --ffmpeg none --no-browser`,
    url: `http://localhost:${webPort}/api/world`,
    timeout: 300_000,
    gracefulShutdown: { signal: 'SIGTERM', timeout: 10_000 },
  },
});
