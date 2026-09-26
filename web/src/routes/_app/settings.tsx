import { createFileRoute, Outlet } from '@tanstack/react-router';
import { Settings } from 'lucide-react';
import { Page, PageHeader } from '@/components/page';
import { SubNav, SubNavLayout, SubNavLink } from '@/components/sub-nav';
import { settingsPages } from '@/extensibility/settings';

export const Route = createFileRoute('/_app/settings')({ component: SettingsLayout });

/** The user's own settings (IAM-01, IAM-03, IAM-14, PLT-17, NTF-03…05, CAL-04): a page per topic. */
function SettingsLayout() {
  return (
    <Page className="max-w-5xl">
      <PageHeader icon={Settings} title="Settings" description="Your account, preferences and connections." />
      <SubNavLayout
        nav={
          <SubNav label="Settings">
            {settingsPages.map((page) => (
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
    </Page>
  );
}
