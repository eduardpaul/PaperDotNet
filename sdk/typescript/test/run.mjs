// Runs the SDK's end-to-end tests against a real PaperDotNet server: builds and starts the host on a free port with a
// temporary SQLite database, waits until it is ready, runs `node --test`, and stops the server.
// Usage: npm run test:e2e   (PAPERDOTNET_URL=http://… runs against a server that is already running instead)
import { spawn, spawnSync } from 'node:child_process';
import { mkdtempSync, rmSync } from 'node:fs';
import { createServer } from 'node:net';
import { tmpdir } from 'node:os';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const root = resolve(here, '../../..');
const host = join(root, 'src/PaperDotNet.Host');

async function freePort() {
  return new Promise((ok, fail) => {
    const server = createServer();
    server.once('error', fail);
    server.listen(0, '127.0.0.1', () => {
      const { port } = server.address();
      server.close(() => ok(port));
    });
  });
}

async function waitUntilReady(url, server, timeoutMs = 120_000) {
  const started = Date.now();
  while (Date.now() - started < timeoutMs) {
    if (server.exitCode !== null) {
      throw new Error(`The server exited with code ${server.exitCode}.`);
    }

    try {
      const response = await fetch(`${url}/health/ready`);
      if (response.ok) {
        return;
      }
    } catch {
      // Not listening yet.
    }

    await new Promise((r) => setTimeout(r, 250));
  }

  throw new Error('The server did not become ready.');
}

let server;
let dataPath;
let url = process.env.PAPERDOTNET_URL;
if (!url) {
  const build = spawnSync('dotnet', ['build', host, '-v', 'q', '-nologo'], { stdio: 'inherit' });
  if (build.status !== 0) {
    process.exit(build.status ?? 1);
  }

  const port = await freePort();
  url = `http://127.0.0.1:${port}`;
  dataPath = mkdtempSync(join(tmpdir(), 'paperdotnet-sdk-e2e-'));
  server = spawn('dotnet', [join(host, 'bin/Debug/net10.0/paperdotnet.dll')], {
    cwd: dataPath,
    env: {
      ...process.env,
      ASPNETCORE_ENVIRONMENT: 'Development',
      ASPNETCORE_URLS: url,
      PAPERDOTNET__Storage__DataPath: join(dataPath, 'data'),
      PAPERDOTNET__Bootstrap__AdminPassword: 'admin-password-e2e',
      // The working directory is a temporary folder, so appsettings.Development.json is not read: set what it sets.
      PAPERDOTNET__Auth__RequireHttps: 'false',
      PAPERDOTNET__Tenancy__AllowHeader: 'true',
      PAPERDOTNET__Auth__FirstPartyRedirectUris__0: 'http://localhost:5173/callback',
      PAPERDOTNET__Logging__LogLevel__Default: 'Warning',
    },
    stdio: ['ignore', process.env.PAPERDOTNET_E2E_LOGS ? 'inherit' : 'ignore', 'inherit'],
  });
  await waitUntilReady(url, server);
}

const tests = spawnSync(process.execPath, ['--test', '--test-concurrency=1', '--test-timeout=60000', '--test-force-exit', ...process.argv.slice(2), join(here, '*.test.mjs')], {
  stdio: 'inherit',
  env: { ...process.env, PAPERDOTNET_URL: url, PAPERDOTNET_ADMIN_PASSWORD: process.env.PAPERDOTNET_ADMIN_PASSWORD ?? 'admin-password-e2e' },
});

if (server) {
  const exited = new Promise((r) => (server.exitCode !== null ? r() : server.once('exit', r)));
  server.kill('SIGTERM');
  const force = setTimeout(() => server.kill('SIGKILL'), 10_000);
  await exited;
  clearTimeout(force);
  rmSync(dataPath, { recursive: true, force: true });
}

process.exit(tests.status ?? 1);
