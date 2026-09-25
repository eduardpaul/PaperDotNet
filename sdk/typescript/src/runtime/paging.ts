// Paged lists return `value` and `@odata.nextLink` (odataNextLink); these helpers follow the links.

/** A page of a list endpoint as generated (e.g. ItemPage, PageOfWorkspaceResponse). */
export interface PageLike<T> {
  value?: T[] | null;
  odataNextLink?: string | null;
}

/** A generated request builder of a paged list: `get` with its configuration and `withUrl` for the next pages. */
export interface PagedRequestBuilder<TPage, TConfig> {
  get(requestConfiguration?: TConfig): Promise<TPage | undefined>;
  withUrl(rawUrl: string): { get(requestConfiguration?: TConfig): Promise<TPage | undefined> };
}

/** Pages one after another: `for await (const page of pages(api.v10.workspaces)) …`. */
export async function* pages<T, TPage extends PageLike<T>, TConfig>(
  builder: PagedRequestBuilder<TPage, TConfig>, requestConfiguration?: TConfig): AsyncGenerator<TPage> {
  let page = await builder.get(requestConfiguration);
  while (page) {
    yield page;
    page = page.odataNextLink ? await builder.withUrl(page.odataNextLink).get() : undefined;
  }
}

/** Every entry of all pages: `for await (const item of all(builder, { queryParameters: { filter } })) …`. */
export async function* all<T, TPage extends PageLike<T>, TConfig>(
  builder: PagedRequestBuilder<TPage, TConfig>, requestConfiguration?: TConfig): AsyncGenerator<T> {
  for await (const page of pages<T, TPage, TConfig>(builder, requestConfiguration)) {
    yield* page.value ?? [];
  }
}

/** Collects every entry into an array (for small lists). */
export async function toArray<T>(entries: AsyncIterable<T>, max = 10_000): Promise<T[]> {
  const result: T[] = [];
  for await (const entry of entries) {
    result.push(entry);
    if (result.length >= max) {
      break;
    }
  }

  return result;
}
