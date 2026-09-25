// Sign-in as a frontend does it: password grant (scripts), authorization code + PKCE (browsers), refresh and sign-out.
import assert from 'node:assert/strict';
import { test } from 'node:test';
import { OAuthSession, createPaperDotNetClient, isStatus, memoryStore, problemOf, staticToken } from '../dist/index.js';
import { adminPassword, baseUrl, redirectUri } from './helpers.mjs';

test('password sign-in gives tokens the generated client uses', async () => {
  const session = new OAuthSession({ baseUrl });
  const tokens = await session.signInWithPassword('admin', adminPassword);
  assert.ok(tokens.accessToken);
  assert.ok(tokens.refreshToken, 'offline_access gives a refresh token');

  const { api } = createPaperDotNetClient({ baseUrl, auth: session });
  const me = await api.v10.me.get();
  assert.equal(me.userName, 'admin');
});

test('an expired or revoked access token is refreshed once and the call is repeated', async () => {
  const store = memoryStore();
  const session = new OAuthSession({ baseUrl, store });
  const tokens = await session.signInWithPassword('admin', adminPassword);
  // A broken access token: the server answers 401, the client refreshes and repeats the call.
  await session.useTokens({ ...tokens, accessToken: 'not-a-token' });

  const { api } = createPaperDotNetClient({ baseUrl, auth: session });
  const me = await api.v10.me.get();
  assert.equal(me.userName, 'admin');
  assert.notEqual((await session.tokens()).accessToken, 'not-a-token');
});

test('tokens close to expiry are refreshed before the call', async () => {
  const session = new OAuthSession({ baseUrl });
  const tokens = await session.signInWithPassword('admin', adminPassword);
  await session.useTokens({ ...tokens, expiresAt: Date.now() + 1000 });
  const token = await session.getAccessToken();
  assert.notEqual(token, tokens.accessToken);
  assert.ok((await session.tokens()).expiresAt > Date.now() + 60_000);
});

test('a wrong password is an OAuth error, a wrong token an ApiProblem with 401', async () => {
  const session = new OAuthSession({ baseUrl });
  await assert.rejects(session.signInWithPassword('admin', 'wrong-password'));

  const { api } = createPaperDotNetClient({ baseUrl, auth: staticToken('wrong') });
  const error = await api.v10.me.get().then(() => undefined, (e) => e);
  assert.ok(isStatus(error, 401), `expected 401, got ${problemOf(error)?.responseStatusCode}`);
});

test('browser sign-in: login page session, authorization code with PKCE, sign-out', async () => {
  // 1. The sign-in page posts the credentials with the generated client (the browser keeps the session cookie).
  let cookie = '';
  const cookieFetch = async (input, init = {}) => {
    const headers = new Headers(init.headers);
    if (cookie) {
      headers.set('Cookie', cookie);
    }

    const response = await fetch(input, { ...init, headers, redirect: 'manual' });
    const set = response.headers.getSetCookie?.() ?? [];
    if (set.length) {
      cookie = set.map((c) => c.split(';')[0]).join('; ');
    }

    return response;
  };
  const anonymous = createPaperDotNetClient({ baseUrl, fetch: cookieFetch });
  await anonymous.api.v10.auth.login.post({ userName: 'admin', password: adminPassword });
  assert.ok(cookie, 'the login sets a session cookie');

  // 2. The app starts the sign-in and the browser follows the authorization URL.
  const session = new OAuthSession({ baseUrl, redirectUri, store: memoryStore() });
  const authorize = await session.beginSignIn();
  assert.equal(authorize.searchParams.get('code_challenge_method'), 'S256');
  const response = await cookieFetch(authorize);
  assert.equal(response.status, 302);
  const callback = new URL(response.headers.get('Location'));
  assert.equal(`${callback.origin}${callback.pathname}`, redirectUri);

  // 3. The redirect page completes it.
  const tokens = await session.completeSignIn(callback);
  assert.ok(tokens.accessToken);
  assert.ok(tokens.idToken);
  const { api } = createPaperDotNetClient({ baseUrl, auth: session });
  assert.equal((await api.v10.me.get()).userName, 'admin');

  // A second completion (replayed callback) is refused.
  await assert.rejects(session.completeSignIn(callback));

  // 4. Sign-out revokes the refresh token and returns the end-session URL.
  const refreshToken = tokens.refreshToken;
  const endSession = await session.signOut('http://localhost:5173/');
  assert.ok(endSession);
  assert.equal(await session.isSignedIn(), false);
  const other = new OAuthSession({ baseUrl });
  await other.useTokens({ accessToken: 'x', refreshToken });
  assert.equal(await other.refresh(), false, 'the revoked refresh token no longer works');
});
