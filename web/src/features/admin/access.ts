import { useQuery } from '@tanstack/react-query';
import { meQuery } from '@/api/queries';

/** Scopes that make the administration area useful. Plain directory reads do not. */
const adminScopes = [
  'user.manage',
  'group.manage',
  'role.manage',
  'organization.manage',
  'taxonomy.manage',
  'audit.read',
  'search.manage',
  'extension.manage',
  'application.manage',
  'template.read',
  'template.manage',
];

export function useAdminAccess() {
  const { data } = useQuery(meQuery);
  const held = new Set(data?.scopes ?? []);
  return {
    has: (scope: string) => held.has(scope),
    any: adminScopes.some((scope) => held.has(scope)),
  };
}
