// Builds and starts a simulator for the end-to-end tests on a fresh default world in a temporary
// directory, which goes away with the server. Arguments are passed on to the simulator.
import { spawn, spawnSync } from 'node:child_process';
import { mkdtempSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const project = join(dirname(fileURLToPath(import.meta.url)), '..', '..', 'OnvifSim.Server');
const build = spawnSync('dotnet', ['build', project, '-c', 'Debug', '-v', 'quiet', '-nologo'], { stdio: 'inherit' });
if (build.status !== 0) process.exit(build.status ?? 1);

const data = mkdtempSync(join(tmpdir(), 'onvifsim-e2e-'));
const server = spawn('dotnet', [join(project, 'bin', 'Debug', 'net10.0', 'OnvifSim.dll'), '--data', data, ...process.argv.slice(2)], {
  stdio: 'inherit',
});
for (const signal of ['SIGINT', 'SIGTERM']) process.on(signal, () => server.kill(signal));
server.on('exit', (code) => {
  rmSync(data, { recursive: true, force: true });
  process.exit(code ?? 0);
});
