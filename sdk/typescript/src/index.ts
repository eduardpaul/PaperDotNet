// The TypeScript SDK (API-03): the Kiota-generated client plus a factory for authentication and the tenant.
import { AnonymousAuthenticationProvider } from '@microsoft/kiota-abstractions';
import { FetchRequestAdapter, KiotaClientFactory } from '@microsoft/kiota-http-fetchlibrary';
import { createPaperDotNetApiClient, type PaperDotNetApiClient } from './generated/paperDotNetApiClient.js';

export * from './generated/paperDotNetApiClient.js';
export * from './generated/models/index.js';

export interface PaperDotNetClientOptions {
  /** Address of the installation, e.g. https://dms.example.com */
  baseUrl: string;
  /** An API token or an OAuth access token. */
  accessToken: string;
  /** The tenant, when the installation does not resolve it from the host name. */
  tenant?: string;
}

/**
 * Creates a client:
 * `const api = createPaperDotNetClient({ baseUrl, accessToken }); await api.v10.workspaces.get();`
 */
export function createPaperDotNetClient(options: PaperDotNetClientOptions): PaperDotNetApiClient {
  const headers: Record<string, string> = { Authorization: `Bearer ${options.accessToken}` };
  if (options.tenant) {
    headers['X-Tenant'] = options.tenant;
  }

  const fetchWithHeaders = (input: string | URL | Request, init?: RequestInit) =>
    fetch(input, { ...init, headers: { ...Object.fromEntries(new Headers(init?.headers).entries()), ...headers } });
  const adapter = new FetchRequestAdapter(
    new AnonymousAuthenticationProvider(), undefined, undefined, KiotaClientFactory.create(fetchWithHeaders));
  adapter.baseUrl = options.baseUrl.replace(/\/+$/, '');
  return createPaperDotNetApiClient(adapter);
}
