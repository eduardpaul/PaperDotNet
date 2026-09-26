// The app's single SDK client (ADR-0033): same origin as the API, tokens in sessionStorage (per tab; other tabs
// sign in silently through the server's sign-in session), sign-in through the authorization code flow with PKCE.
import { createPaperDotNetClient, OAuthSession, webStorageStore, type PaperDotNetClient } from '@paperdotnet/client';

const origin = window.location.origin;
const ReturnToKey = 'paperdotnet.returnTo';

export const session = new OAuthSession({
  baseUrl: origin,
  redirectUri: `${origin}/callback`,
  store: webStorageStore(window.sessionStorage),
});

export const client: PaperDotNetClient = createPaperDotNetClient({ baseUrl: origin, auth: session });
export const api = client.api;

/** Starts the browser sign-in; the server shows /login when there is no sign-in session. */
export async function signIn(returnTo = window.location.pathname + window.location.search): Promise<never> {
  window.sessionStorage.setItem(ReturnToKey, returnTo);
  window.location.assign((await session.beginSignIn()).href);
  return new Promise<never>(() => {});
}

/** Finishes the sign-in on /callback; returns where the user was going. */
export async function completeSignIn(): Promise<string> {
  await session.completeSignIn(window.location.href);
  const returnTo = window.sessionStorage.getItem(ReturnToKey) ?? '/';
  window.sessionStorage.removeItem(ReturnToKey);
  // Only paths of this app, never another origin.
  return returnTo.startsWith('/') && !returnTo.startsWith('//') ? returnTo : '/';
}

/** Revokes the tokens and ends the server's sign-in session. */
export async function signOut(): Promise<void> {
  const endSession = await session.signOut(`${origin}/callback`);
  window.location.assign(endSession?.href ?? '/login');
}
