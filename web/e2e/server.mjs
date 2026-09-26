// Starts a real PaperDotNet host that serves the built UI (web/dist) with a temporary SQLite database, for Playwright
// (playwright.config.ts starts it as its webServer). Stops the host and removes the data on SIGTERM/SIGINT.
import { spawn, spawnSync } from 'node:child_process';
import { mkdtempSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const root = resolve(here, '../..');
const host = join(root, 'src/PaperDotNet.Host');
const port = process.env.PAPERDOTNET_E2E_PORT ?? '5199';
const origin = `http://localhost:${port}`;

const build = spawnSync('dotnet', ['build', host, '-v', 'q', '-nologo'], { stdio: 'inherit' });
if (build.status !== 0) process.exit(build.status ?? 1);

const dataPath = mkdtempSync(join(tmpdir(), 'paperdotnet-web-e2e-'));
const server = spawn('dotnet', [join(host, 'bin/Debug/net10.0/paperdotnet.dll')], {
  cwd: dataPath,
  env: {
    ...process.env,
    ASPNETCORE_ENVIRONMENT: 'Production',
    ASPNETCORE_URLS: origin,
    PAPERDOTNET__Web__RootPath: join(root, 'web/dist'),
    PAPERDOTNET__Storage__DataPath: join(dataPath, 'data'),
    PAPERDOTNET__Bootstrap__AdminPassword: process.env.PAPERDOTNET_ADMIN_PASSWORD ?? 'admin-password-e2e',
    PAPERDOTNET__Auth__RequireHttps: 'false',
    PAPERDOTNET__Auth__FirstPartyRedirectUris__0: `${origin}/callback`,
    PAPERDOTNET__Auth__PasskeyServerDomain: 'localhost',
    PAPERDOTNET__Auth__PasskeyOrigins__0: origin,
    PAPERDOTNET__Logging__LogLevel__Default: 'Warning',
  },
  stdio: ['ignore', process.env.PAPERDOTNET_E2E_LOGS ? 'inherit' : 'ignore', 'inherit'],
});

let stopping = false;
const stop = () => {
  if (stopping) return;
  stopping = true;
  server.kill('SIGTERM');
  const force = setTimeout(() => server.kill('SIGKILL'), 10_000);
  server.once('exit', () => {
    clearTimeout(force);
    rmSync(dataPath, { recursive: true, force: true });
    process.exit(0);
  });
};

process.on('SIGTERM', stop);
process.on('SIGINT', stop);
server.once('exit', (code) => {
  if (!stopping) {
    rmSync(dataPath, { recursive: true, force: true });
    process.exit(code ?? 1);
  }
});
