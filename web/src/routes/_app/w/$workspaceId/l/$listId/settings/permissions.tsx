import { useQuery } from '@tanstack/react-query';
import { createFileRoute } from '@tanstack/react-router';
import { listPermissionsQuery } from '@/features/list-settings/queries';
import { PermissionsEditor } from '@/features/list-settings/permissions-editor';
import { listBuilder, listQuery } from '@/features/lists/queries';

export const Route = createFileRoute('/_app/w/$workspaceId/l/$listId/settings/permissions')({ component: Permissions });

function Permissions() {
  const { workspaceId, listId } = Route.useParams();
  const { data: list } = useQuery(listQuery(workspaceId, listId));
  return (
    <PermissionsEditor
      queryKey={listPermissionsQuery(workspaceId, listId).queryKey}
      builder={listBuilder(workspaceId, listId).permissions}
      scope={list?.kind === 'library' ? 'library' : 'list'}
    />
  );
}
