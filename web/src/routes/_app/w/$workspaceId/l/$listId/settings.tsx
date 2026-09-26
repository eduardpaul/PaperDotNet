import { useQuery } from '@tanstack/react-query';
import { createFileRoute, Link, Outlet } from '@tanstack/react-router';
import { ArrowLeft, Columns3, FileCog, LayoutList, Settings2, ShieldCheck } from 'lucide-react';
import { Page, PageHeader } from '@/components/page';
import { SubNav, SubNavLayout, SubNavLink } from '@/components/sub-nav';
import { Alert } from '@/components/ui/feedback';
import { listPermissionsQuery } from '@/features/list-settings/queries';
import { ListIcon } from '@/features/lists/list-icon';
import { listQuery } from '@/features/lists/queries';

export const Route = createFileRoute('/_app/w/$workspaceId/l/$listId/settings')({
  loader: ({ context, params }) => context.queryClient.ensureQueryData(listQuery(params.workspaceId, params.listId)),
  component: ListSettings,
});

/** A list's settings (LST-02, LST-03, LST-09, LST-11, DOC-07, DOC-10, IAM-07). */
function ListSettings() {
  const params = Route.useParams();
  const { data: list } = useQuery(listQuery(params.workspaceId, params.listId));
  const { data: permissions } = useQuery(listPermissionsQuery(params.workspaceId, params.listId));
  return (
    <Page className="max-w-5xl">
      <Link
        to="/w/$workspaceId/l/$listId"
        params={params}
        className="mb-2 inline-flex items-center gap-1 text-xs text-muted hover:text-foreground"
      >
        <ArrowLeft className="size-3.5" /> {list?.name}
      </Link>
      <PageHeader
        title={
          <span className="flex items-center gap-2">
            {list && <ListIcon list={list} className="size-5 text-accent" />}
            {list?.kind === 'library' ? 'Library settings' : 'List settings'}
          </span>
        }
        description={list?.name}
      />
      <SubNavLayout
        nav={
          <SubNav label="List settings">
            <SubNavLink to="/w/$workspaceId/l/$listId/settings" params={params} activeOptions={{ exact: true }}>
              <Settings2 /> General
            </SubNavLink>
            <SubNavLink to="/w/$workspaceId/l/$listId/settings/columns" params={params}>
              <Columns3 /> Columns
            </SubNavLink>
            <SubNavLink to="/w/$workspaceId/l/$listId/settings/views" params={params}>
              <LayoutList /> Views
            </SubNavLink>
            {list?.kind === 'library' && (
              <SubNavLink to="/w/$workspaceId/l/$listId/settings/documents" params={params}>
                <FileCog /> Documents
              </SubNavLink>
            )}
            <SubNavLink to="/w/$workspaceId/l/$listId/settings/permissions" params={params}>
              <ShieldCheck /> Permissions
            </SubNavLink>
          </SubNav>
        }
      >
        {permissions && permissions.effectiveLevel !== 'manage' && (
          <Alert tone="warning">Only people who manage this list change its settings. You can look at them.</Alert>
        )}
        <Outlet />
      </SubNavLayout>
    </Page>
  );
}
