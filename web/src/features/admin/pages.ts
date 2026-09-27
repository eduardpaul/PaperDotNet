// Administration pages. The sidebar, account menu and command palette share this list.
import type { LucideIcon } from 'lucide-react';
import { Building2, KeyRound, ScrollText, Tags, Users } from 'lucide-react';

export interface AdminPage {
  to: '/admin' | '/admin/terms' | '/admin/organization' | '/admin/applications' | '/admin/maintenance';
  label: string;
  icon: LucideIcon;
  keywords?: string;
  exact?: boolean;
}

export const adminPages: AdminPage[] = [
  { to: '/admin', label: 'People', icon: Users, keywords: 'users groups roles accounts', exact: true },
  { to: '/admin/terms', label: 'Term store', icon: Tags, keywords: 'taxonomy keywords csv import' },
  { to: '/admin/organization', label: 'Organization', icon: Building2, keywords: 'defaults language time zone' },
  { to: '/admin/applications', label: 'Applications', icon: KeyRound, keywords: 'oauth clients' },
  {
    to: '/admin/maintenance',
    label: 'Maintenance',
    icon: ScrollText,
    keywords: 'audit reindex export import provisioning extensions',
  },
];

/** Pages the caller may open. Plain directory reads do not count. */
export function visibleAdminPages(has: (scope: string) => boolean): AdminPage[] {
  return adminPages.filter((page) => {
    if (page.to === '/admin') return has('user.manage') || has('group.manage') || has('role.manage');
    if (page.to === '/admin/terms') return has('taxonomy.manage');
    if (page.to === '/admin/organization') return has('organization.manage');
    if (page.to === '/admin/applications') return has('application.manage');
    return has('audit.read') || has('search.manage') || has('extension.manage') || has('template.read');
  });
}
