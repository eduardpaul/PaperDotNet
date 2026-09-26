// Names for ids in field values: people (the user directory) and terms (managed metadata and keywords).
import type { TermResponse, UserResponse } from '@paperdotnet/client';
import { all, toArray } from '@paperdotnet/client';
import { queryOptions, useQuery } from '@tanstack/react-query';
import { api } from '@/api/client';

/** Everyone in the organization (members may read the directory); cached for the session. */
export const usersQuery = queryOptions({
  queryKey: ['users'],
  queryFn: () => toArray(all(api.v10.users, { queryParameters: { top: 200 } })),
  staleTime: 5 * 60_000,
});

export function useUsers(): Map<string, UserResponse> {
  const { data } = useQuery(usersQuery);
  const users = new Map<string, UserResponse>();
  for (const user of data ?? []) if (user.id) users.set(user.id, user);
  return users;
}

export function userName(user: UserResponse | undefined, id: string): string {
  return user?.displayName || user?.userName || `Unknown user (${id.slice(0, 8)})`;
}

/** Terms by id, in one request per set of ids (GET /termStore/terms?ids=); sorted so the key is stable. */
export const termsQuery = (ids: string[]) => {
  const sorted = [...new Set(ids)].sort();
  return queryOptions({
    queryKey: ['terms', sorted],
    queryFn: async () =>
      sorted.length
        ? ((await api.v10.termStore.terms.get({ queryParameters: { ids: sorted.slice(0, 200).join(',') } })) ?? [])
        : [],
    staleTime: 5 * 60_000,
    enabled: sorted.length > 0,
  });
};

export function useTerms(ids: string[]): Map<string, TermResponse> {
  const { data } = useQuery(termsQuery(ids));
  const terms = new Map<string, TermResponse>();
  for (const term of data ?? []) if (term.id) terms.set(term.id, term);
  return terms;
}
