import { createFileRoute, Outlet } from '@tanstack/react-router';
import { Shield } from 'lucide-react';
import { Page, PageHeader } from '@/components/page';
import { SubNav, SubNavLayout, SubNavLink } from '@/components/sub-nav';
import { useAdminAccess } from '@/features/admin/access';
import { visibleAdminPages } from '@/features/admin/pages';

export const Route = createFileRoute('/_app/admin')({ component: AdminLayout });

function AdminLayout() {
  const admin = useAdminAccess();
  const visible = visibleAdminPages(admin.has);

  return (
    <Page className="max-w-5xl">
      <PageHeader
        icon={Shield}
        title="Administration"
        description="People, vocabulary and the organization. What you see follows your roles."
      />
      {!admin.any ? (
        <p className="text-sm text-muted">You do not administer this organization.</p>
      ) : (
        <SubNavLayout
          nav={
            <SubNav label="Administration">
              {visible.map((page) => (
                <SubNavLink key={page.to} to={page.to} activeOptions={{ exact: 'exact' in page && page.exact }}>
                  <page.icon />
                  {page.label}
                </SubNavLink>
              ))}
            </SubNav>
          }
        >
          <Outlet />
        </SubNavLayout>
      )}
    </Page>
  );
}
