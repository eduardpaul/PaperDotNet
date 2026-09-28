// The pages of Administration, each shown to people holding one of its scopes (ADR-0033 §5).
import { useQuery } from '@tanstack/react-query';
import type { LucideIcon } from 'lucide-react';
import { AppWindow, Building2, Puzzle, ScrollText, ShieldHalf, Tags, UserCog, Users, Wrench } from 'lucide-react';
import { meQuery } from '@/api/queries';

export interface AdminPage {
  to:
    | '/admin'
    | '/admin/users'
    | '/admin/groups'
    | '/admin/roles'
    | '/admin/terms'
    | '/admin/extensions'
    | '/admin/applications'
    | '/admin/audit'
    | '/admin/maintenance';
  label: string;
  icon: LucideIcon;
  /** The page shows to holders of any of these scopes. */
  scopes: string[];
  keywords?: string;
}

export const adminPages: AdminPage[] = [
  {
    to: '/admin',
    label: 'Organization',
    icon: Building2,
    scopes: ['organization.manage'],
    keywords: 'defaults time zone language',
  },
  { to: '/admin/users', label: 'Users', icon: UserCog, scopes: ['user.manage'], keywords: 'accounts password disable' },
  { to: '/admin/groups', label: 'Groups', icon: Users, scopes: ['group.manage'], keywords: 'teams members inbox' },
  { to: '/admin/roles', label: 'Roles', icon: ShieldHalf, scopes: ['role.manage'], keywords: 'scopes permissions' },
  {
    to: '/admin/terms',
    label: 'Term store',
    icon: Tags,
    scopes: ['taxonomy.manage'],
    keywords: 'tags keywords taxonomy',
  },
  { to: '/admin/extensions', label: 'Extensions', icon: Puzzle, scopes: ['extension.manage'], keywords: 'plugins' },
  {
    to: '/admin/applications',
    label: 'Applications',
    icon: AppWindow,
    scopes: ['application.manage'],
    keywords: 'oauth clients',
  },
  { to: '/admin/audit', label: 'Audit log', icon: ScrollText, scopes: ['audit.read'], keywords: 'history who changed' },
  {
    to: '/admin/maintenance',
    label: 'Maintenance',
    icon: Wrench,
    scopes: ['search.manage', 'template.read', 'template.manage'],
    keywords: 'reindex templates export import backup',
  },
];

/** The admin pages the signed-in user may open (empty for most people). */
export function useAdminPages(): AdminPage[] {
  const { data: me } = useQuery(meQuery);
  const scopes = new Set(me?.scopes ?? []);
  return adminPages.filter((page) => page.scopes.some((s) => scopes.has(s)));
}

/** True when the user holds a scope. */
export function useHasScope(scope: string): boolean {
  const { data: me } = useQuery(meQuery);
  return !!me?.scopes?.includes(scope);
}
