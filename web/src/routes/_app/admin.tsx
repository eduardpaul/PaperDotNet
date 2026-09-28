import { createFileRoute, Outlet } from '@tanstack/react-router';
import { ShieldCheck } from 'lucide-react';
import { Page, PageHeader } from '@/components/page';
import { SubNav, SubNavLayout, SubNavLink } from '@/components/sub-nav';
import { Card } from '@/components/ui/card';
import { EmptyState } from '@/components/ui/feedback';
import { useAdminPages } from '@/extensibility/admin';

export const Route = createFileRoute('/_app/admin')({ component: Administration });

/** Organization administration (IAM, TAX, EXT, PLT): one page per area the user may manage. */
function Administration() {
  const pages = useAdminPages();
  return (
    <Page className="max-w-6xl">
      <PageHeader icon={ShieldCheck} title="Administration" description="Settings of the whole organization." />
      {pages.length ? (
        <SubNavLayout
          nav={
            <SubNav label="Administration">
              {pages.map((page) => (
                <SubNavLink key={page.to} to={page.to} activeOptions={{ exact: true }}>
                  <page.icon />
                  {page.label}
                </SubNavLink>
              ))}
            </SubNav>
          }
        >
          <Outlet />
        </SubNavLayout>
      ) : (
        <Card>
          <EmptyState icon={ShieldCheck} title="Nothing to administer">
            Administrators give access to these settings with roles.
          </EmptyState>
        </Card>
      )}
    </Page>
  );
}
