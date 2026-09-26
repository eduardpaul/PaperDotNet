import type { SearchResponse } from '@paperdotnet/client';
import { infiniteQueryOptions, queryOptions } from '@tanstack/react-query';
import { api } from '@/api/client';

export interface SearchParams {
  q?: string;
  mode?: 'keyword' | 'semantic' | 'hybrid';
  workspace?: string;
  list?: string;
  contentType?: string;
  term?: string;
}

/** Search results (SRC-01…09), following @odata.nextLink for more. */
export const searchQuery = (params: SearchParams, top = 25) =>
  infiniteQueryOptions({
    queryKey: ['search', params, top],
    enabled: !!(params.q?.trim() || params.workspace || params.list || params.contentType || params.term),
    initialPageParam: undefined as string | undefined,
    queryFn: async ({ pageParam }): Promise<SearchResponse> =>
      (pageParam
        ? await api.v10.search.withUrl(pageParam).get()
        : await api.v10.search.get({
            queryParameters: {
              q: params.q?.trim() || undefined,
              mode: params.mode,
              workspaceId: params.workspace,
              containerId: params.list,
              contentTypeId: params.contentType,
              termId: params.term,
              top,
            },
          })) ?? { value: [] },
    getNextPageParam: (last) => last.odataNextLink ?? undefined,
  });

/** Every content type (names for facets). */
export const contentTypesQuery = queryOptions({
  queryKey: ['contentTypes'],
  queryFn: async () => (await api.v10.contentTypes.get()) ?? [],
  staleTime: 5 * 60_000,
});
