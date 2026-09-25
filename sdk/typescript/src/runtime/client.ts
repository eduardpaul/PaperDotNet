// The client factory: the Kiota-generated client with authentication, the tenant and one retry after a refreshed token.
import type { AuthenticationProvider, RequestInformation } from '@microsoft/kiota-abstractions';
import { FetchRequestAdapter, KiotaClientFactory } from '@microsoft/kiota-http-fetchlibrary';
import { createPaperDotNetApiClient, type PaperDotNetApiClient } from '../generated/paperDotNetApiClient.js';
import { staticToken, type TokenSource } from './auth.js';

export interface PaperDotNetClientOptions {
  /** Address of the installation, e.g. https://dms.example.com */
  baseUrl: string;
  /** An API token or OAuth access token (fixed), or a TokenSource such as an OAuthSession. */
  accessToken?: string;
  auth?: TokenSource;
  /** The tenant, when the installation does not resolve it from the host name. */
  tenant?: string;
  /** A fetch implementation (default: the global one). */
  fetch?: typeof fetch;
}

/** The generated client plus what the helpers of this package need. */
export interface PaperDotNetClient {
  /** The typed API: `client.api.v10.workspaces.get()`. */
  readonly api: PaperDotNetApiClient;
  readonly baseUrl: string;
  readonly tenant?: string;
  readonly auth?: TokenSource;
  /** fetch with authentication, the tenant and the 401 retry (for helpers that stream). */
  readonly fetch: typeof fetch;
}

/**
 * Creates a client:
 * `const { api } = createPaperDotNetClient({ baseUrl, auth: session }); await api.v10.workspaces.get();`
 */
export function createPaperDotNetClient(options: PaperDotNetClientOptions): PaperDotNetClient {
  const auth = options.auth ?? (options.accessToken ? staticToken(options.accessToken) : undefined);
  const baseUrl = options.baseUrl.replace(/\/+$/, '');
  const authenticatedFetch = createAuthenticatedFetch(auth, options.tenant, options.fetch ?? globalThis.fetch.bind(globalThis));
  const authentication: AuthenticationProvider = {
    // The token is added by authenticatedFetch, so that a retry after refreshing it uses the new one.
    authenticateRequest: (_request: RequestInformation) => Promise.resolve(),
  };
  const adapter = new FetchRequestAdapter(authentication, undefined, undefined, KiotaClientFactory.create(authenticatedFetch));
  adapter.baseUrl = baseUrl;
  return { api: createPaperDotNetApiClient(adapter), baseUrl, tenant: options.tenant, auth, fetch: authenticatedFetch };
}

function createAuthenticatedFetch(auth: TokenSource | undefined, tenant: string | undefined, inner: typeof fetch): typeof fetch {
  const send = async (input: string | URL | Request, init: RequestInit | undefined, token: string | undefined) => {
    const headers = new Headers(init?.headers ?? (input instanceof Request ? input.headers : undefined));
    if (token) {
      headers.set('Authorization', `Bearer ${token}`);
    }

    if (tenant) {
      headers.set('X-Tenant', tenant);
    }

    return inner(input, { ...init, headers });
  };

  return async (input: string | URL | Request, init?: RequestInit) => {
    const response = await send(input, init, await auth?.getAccessToken());
    if (response.status === 401 && auth?.refresh && !(init?.body instanceof ReadableStream) && (await auth.refresh())) {
      return send(input, init, await auth.getAccessToken());
    }

    return response;
  };
}
