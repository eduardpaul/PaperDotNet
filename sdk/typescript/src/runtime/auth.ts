// Sign-in for PaperDotNet clients with oauth4webapi (MIT): authorization code + PKCE for browsers, refresh, the
// first-party password grant for scripts and tests, and sign-out. The generated client asks a TokenSource for tokens.
import * as oauth from 'oauth4webapi';

/** Gives the generated client an access token for each request. */
export interface TokenSource {
  getAccessToken(): Promise<string | undefined>;
  /** Called once after a 401 to get a new token; returns false when there is none. */
  refresh?(): Promise<boolean>;
}

/** Tokens as stored between page loads. */
export interface StoredTokens {
  accessToken: string;
  refreshToken?: string;
  /** Epoch milliseconds. */
  expiresAt?: number;
  idToken?: string;
}

/** Where a session keeps its tokens and the pending sign-in (PKCE verifier and state). */
export interface TokenStore {
  get(key: string): Promise<string | undefined> | string | undefined;
  set(key: string, value: string): Promise<void> | void;
  remove(key: string): Promise<void> | void;
}

/** Keeps tokens in memory (scripts, tests, server-side rendering). */
export function memoryStore(): TokenStore {
  const values = new Map<string, string>();
  return { get: (k) => values.get(k), set: (k, v) => void values.set(k, v), remove: (k) => void values.delete(k) };
}

/** Keeps tokens in the browser's sessionStorage or localStorage. */
export function webStorageStore(storage: Storage, prefix = 'paperdotnet.'): TokenStore {
  return {
    get: (k) => storage.getItem(prefix + k) ?? undefined,
    set: (k, v) => storage.setItem(prefix + k, v),
    remove: (k) => storage.removeItem(prefix + k),
  };
}

export interface OAuthSessionOptions {
  /** Address of the installation, e.g. https://dms.example.com */
  baseUrl: string;
  /** The OAuth client id; the first-party client is `paperdotnet`. */
  clientId?: string;
  /** Where the browser returns after sign-in (a registered redirect URI). */
  redirectUri?: string;
  /** Requested scopes; `api` gives full access as the user, `offline_access` a refresh token. */
  scope?: string;
  /**
   * The tenant for token requests, when the host name does not select it (development). Browser sign-in pages are
   * reached by navigation, so there the host name must select the tenant.
   */
  tenant?: string;
  store?: TokenStore;
  /** Allow http:// (local development only). Default: true for http:// base URLs. */
  allowInsecureRequests?: boolean;
  fetch?: typeof fetch;
}

const TokensKey = 'tokens';
const PendingKey = 'pending';

/**
 * An OAuth session against a PaperDotNet installation. In a browser:
 * `location.href = (await session.beginSignIn()).href`, then on the redirect page `await session.completeSignIn(location.href)`.
 */
export class OAuthSession implements TokenSource {
  private server?: oauth.AuthorizationServer;
  private refreshing?: Promise<boolean>;
  private readonly client: oauth.Client;
  private readonly store: TokenStore;

  constructor(private readonly options: OAuthSessionOptions) {
    this.client = { client_id: options.clientId ?? 'paperdotnet' };
    this.store = options.store ?? memoryStore();
  }

  /** The tokens of the signed-in user, if any. */
  async tokens(): Promise<StoredTokens | undefined> {
    const json = await this.store.get(TokensKey);
    return json ? (JSON.parse(json) as StoredTokens) : undefined;
  }

  async isSignedIn(): Promise<boolean> {
    return (await this.tokens()) !== undefined;
  }

  /** Starts a browser sign-in: returns the authorization URL to navigate to (the PKCE verifier and state are stored). */
  async beginSignIn(extraParameters: Record<string, string> = {}): Promise<URL> {
    const server = await this.discover();
    if (!this.options.redirectUri) {
      throw new Error('beginSignIn needs a redirectUri.');
    }

    const verifier = oauth.generateRandomCodeVerifier();
    const state = oauth.generateRandomState();
    await this.store.set(PendingKey, JSON.stringify({ verifier, state }));
    const url = new URL(server.authorization_endpoint!);
    url.searchParams.set('client_id', this.client.client_id);
    url.searchParams.set('redirect_uri', this.options.redirectUri);
    url.searchParams.set('response_type', 'code');
    url.searchParams.set('scope', this.scope);
    url.searchParams.set('code_challenge', await oauth.calculatePKCECodeChallenge(verifier));
    url.searchParams.set('code_challenge_method', 'S256');
    url.searchParams.set('state', state);

    for (const [name, value] of Object.entries(extraParameters)) {
      url.searchParams.set(name, value);
    }

    return url;
  }

  /** Finishes a browser sign-in with the URL the browser returned to (checks the state, exchanges the code). */
  async completeSignIn(callbackUrl: string | URL): Promise<StoredTokens> {
    const server = await this.discover();
    const pending = await this.store.get(PendingKey);
    if (!pending) {
      throw new Error('No sign-in is in progress.');
    }

    const { verifier, state } = JSON.parse(pending) as { verifier: string; state: string };
    const parameters = oauth.validateAuthResponse(server, this.client, new URL(callbackUrl), state);
    const response = await oauth.authorizationCodeGrantRequest(
      server, this.client, oauth.None(), parameters, this.options.redirectUri!, verifier, this.requestOptions());
    const result = await oauth.processAuthorizationCodeResponse(server, this.client, response);
    await this.store.remove(PendingKey);
    return this.save(result);
  }

  /** Signs in with user name and password (the first-party client only; scripts, CLIs and tests). */
  async signInWithPassword(userName: string, password: string): Promise<StoredTokens> {
    const server = await this.discover();
    const response = await oauth.genericTokenEndpointRequest(
      server, this.client, oauth.None(), 'password', { username: userName, password, scope: this.scope }, this.requestOptions());
    return this.save(await oauth.processGenericTokenEndpointResponse(server, this.client, response));
  }

  /** Uses tokens obtained elsewhere (e.g. from the server side of the app). */
  async useTokens(tokens: StoredTokens): Promise<void> {
    await this.store.set(TokensKey, JSON.stringify(tokens));
  }

  async getAccessToken(): Promise<string | undefined> {
    const tokens = await this.tokens();
    if (tokens?.expiresAt !== undefined && tokens.expiresAt - Date.now() < 60_000 && tokens.refreshToken) {
      await this.refresh();
      return (await this.tokens())?.accessToken;
    }

    return tokens?.accessToken;
  }

  /** Gets new tokens with the refresh token; false (and signed out) when that is no longer possible. */
  refresh(): Promise<boolean> {
    // One refresh at a time: parallel requests wait for the same one.
    this.refreshing ??= this.refreshOnce().finally(() => (this.refreshing = undefined));
    return this.refreshing;
  }

  /** Revokes the refresh token and forgets the tokens; returns the server's end-session URL for a browser redirect. */
  async signOut(postLogoutRedirectUri?: string): Promise<URL | undefined> {
    const server = await this.discover();
    const tokens = await this.tokens();
    if (tokens?.refreshToken && server.revocation_endpoint) {
      try {
        const response = await oauth.revocationRequest(server, this.client, oauth.None(), tokens.refreshToken, this.requestOptions());
        await oauth.processRevocationResponse(response);
      } catch {
        // Signing out locally still works when the server cannot be reached.
      }
    }

    await this.store.remove(TokensKey);
    if (!server.end_session_endpoint) {
      return undefined;
    }

    const url = new URL(server.end_session_endpoint);
    if (postLogoutRedirectUri) {
      url.searchParams.set('post_logout_redirect_uri', postLogoutRedirectUri);
    }

    if (tokens?.idToken) {
      url.searchParams.set('id_token_hint', tokens.idToken);
    }

    return url;
  }

  private get scope(): string {
    return this.options.scope ?? 'openid offline_access api';
  }

  private async refreshOnce(): Promise<boolean> {
    const tokens = await this.tokens();
    if (!tokens?.refreshToken) {
      return false;
    }

    try {
      const server = await this.discover();
      const response = await oauth.refreshTokenGrantRequest(server, this.client, oauth.None(), tokens.refreshToken, this.requestOptions());
      await this.save(await oauth.processRefreshTokenResponse(server, this.client, response), tokens.refreshToken);
      return true;
    } catch {
      await this.store.remove(TokensKey);
      return false;
    }
  }

  private async save(result: oauth.TokenEndpointResponse, previousRefreshToken?: string): Promise<StoredTokens> {
    const tokens: StoredTokens = {
      accessToken: result.access_token,
      refreshToken: result.refresh_token ?? previousRefreshToken,
      expiresAt: result.expires_in !== undefined ? Date.now() + result.expires_in * 1000 : undefined,
      idToken: result.id_token,
    };
    await this.store.set(TokensKey, JSON.stringify(tokens));
    return tokens;
  }

  private async discover(): Promise<oauth.AuthorizationServer> {
    if (!this.server) {
      const issuer = new URL(this.options.baseUrl);
      const response = await oauth.discoveryRequest(issuer, { ...this.requestOptions(), algorithm: 'oidc' });
      this.server = await oauth.processDiscoveryResponse(issuer, response);
    }

    return this.server;
  }

  // Typed loosely: oauth4webapi types each call's options by its HTTP method and body.
  // eslint-disable-next-line @typescript-eslint/no-explicit-any
  private requestOptions(): any {
    const insecure = this.options.allowInsecureRequests ?? this.options.baseUrl.startsWith('http://');
    return {
      headers: this.options.tenant ? { 'X-Tenant': this.options.tenant } : undefined,
      [oauth.allowInsecureRequests]: insecure,
      ...(this.options.fetch ? { [oauth.customFetch]: this.options.fetch } : {}),
    };
  }
}

/** A fixed token (API tokens, or tokens managed elsewhere). */
export function staticToken(accessToken: string): TokenSource {
  return { getAccessToken: () => Promise.resolve(accessToken) };
}
