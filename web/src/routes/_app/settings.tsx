import { createFileRoute, Link, Outlet } from '@tanstack/react-router';
import { Settings } from 'lucide-react';
import { Page, PageHeader } from '@/components/page';
import { settingsPages } from '@/extensibility/settings';

export const Route = createFileRoute('/_app/settings')({ component: SettingsLayout });

/** The user's own settings (IAM-01, IAM-03, IAM-14, PLT-17, NTF-03…05, CAL-04): a page per topic. */
function SettingsLayout() {
  return (
    <Page className="max-w-5xl">
      <PageHeader icon={Settings} title="Settings" description="Your account, preferences and connections." />
      <div className="flex flex-col gap-6 md:flex-row md:items-start">
        <nav aria-label="Settings" className="-mx-4 overflow-x-auto px-4 md:mx-0 md:w-52 md:shrink-0 md:px-0">
          <ul className="flex gap-1 md:flex-col md:gap-0.5">
            {settingsPages.map((page) => (
              <li key={page.to}>
                <Link
                  to={page.to}
                  activeOptions={{ exact: true }}
                  className="flex h-8 items-center gap-2.5 rounded-md px-2.5 text-[13px] whitespace-nowrap text-foreground/80 hover:bg-surface-muted hover:text-foreground [&_svg]:size-4 [&_svg]:text-muted"
                  activeProps={{ className: 'bg-accent-soft! text-accent! font-medium [&_svg]:text-accent!' }}
                >
                  <page.icon />
                  {page.label}
                </Link>
              </li>
            ))}
          </ul>
        </nav>
        <div className="flex min-w-0 flex-1 flex-col gap-5">
          <Outlet />
        </div>
      </div>
    </Page>
  );
}
