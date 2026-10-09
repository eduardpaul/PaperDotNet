// Names for ids in field values: people (the user directory) and terms (managed metadata and keywords).
import type { TermResponse, UserResponse } from '@paperdotnet/client';
import { all, toArray } from '@paperdotnet/client';
import { queryOptions, useQuery } from '@tanstack/react-query';
import { api } from '@/api/client';
import { keys } from '@/api/keys';

/** Everyone in the organization (members may read the directory); cached for the session. */
export const usersQuery = queryOptions({
  queryKey: keys.users,
  queryFn: () => toArray(all(api.v10.users, { queryParameters: { top: 200 } })),
  staleTime: 5 * 60_000,
});

/** The organization's groups; empty without the directory scope (groups can still be typed by name). */
export const groupsQuery = queryOptions({
  queryKey: keys.groups,
  queryFn: async () => {
    try {
      return await toArray(all(api.v10.groups, { queryParameters: { top: 200 } }));
    } catch {
      return [];
    }
  },
  staleTime: 5 * 60_000,
});

/**
 * People that can be picked (GET /users?assignable=true: enabled users that are not service accounts), or with a group
 * its effective members among them (GET /groups/{id}/members?transitive=true, groups inside it included).
 */
export const assignablePeopleQuery = (memberOf?: string) =>
  queryOptions({
    queryKey: keys.assignablePeople(memberOf),
    queryFn: async (): Promise<UserResponse[]> =>
      memberOf
        ? ((await api.v10.groups
            .byId(memberOf)
            .members.get({ queryParameters: { transitive: true, assignable: true } })) ?? [])
        : toArray(all(api.v10.users, { queryParameters: { assignable: true, top: 200 } })),
    staleTime: 5 * 60_000,
  });

/** Users by id (GET /users?ids=, at most 200), e.g. the names of selected values; ids that are not users are left out. */
export const usersByIdQuery = (ids: string[]) => {
  const sorted = [...new Set(ids)].sort().slice(0, 200);
  return queryOptions({
    queryKey: keys.usersById(sorted),
    queryFn: async () =>
      sorted.length
        ? ((await api.v10.users.get({ queryParameters: { ids: sorted.join(','), top: 200 } }))?.value ?? [])
        : [],
    staleTime: 5 * 60_000,
    enabled: sorted.length > 0,
  });
};

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
