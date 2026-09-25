// Optimistic concurrency: responses carry `@odata.etag` (odataEtag); send it back as If-Match with changes.
import type { RequestConfiguration } from '@microsoft/kiota-abstractions';

/** Request configuration that sends `If-Match` (a 412 problem means someone changed it first). */
export function ifMatch<T extends object = object>(etagOrEntity: string | { odataEtag?: string | null } | undefined | null): RequestConfiguration<T> {
  const etag = typeof etagOrEntity === 'string' ? etagOrEntity : etagOrEntity?.odataEtag;
  return etag ? { headers: { 'If-Match': [etag] } } as RequestConfiguration<T> : {};
}
