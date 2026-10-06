import type { BuiltInWorkflowResponse } from '@paperdotnet/client';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useNavigate } from '@tanstack/react-router';
import { toast } from 'sonner';
import { libraryWorkflowsQuery } from '@/features/documents/queries';
import { listBuilder } from '@/features/lists/queries';
import { problemMessage } from '@/lib/errors';
import { workflowsQuery } from './queries';

/**
 * Copies a list's built-in workflow that fills a process role (ADR-0047) into the list's own workflow and opens it in the
 * editor: the copy takes over the role in this list, so changing its steps changes how the list does that process.
 */
export function useCustomizeListWorkflow(workspaceId: string, listId: string) {
  const queryClient = useQueryClient();
  const navigate = useNavigate();
  return useMutation({
    mutationFn: (workflow: BuiltInWorkflowResponse) =>
      listBuilder(workspaceId, listId)
        .workflows.builtIns.byKey(workflow.key!)
        .copy.post({ name: `${workflow.name} (custom ${new Date().toISOString().slice(0, 10)})` }),
    onSuccess: async (copy) => {
      await Promise.all([
        queryClient.invalidateQueries({ queryKey: libraryWorkflowsQuery(workspaceId, listId).queryKey }),
        queryClient.invalidateQueries({ queryKey: workflowsQuery(workspaceId).queryKey }),
      ]);
      toast.success('Copied. Change its steps; it does this for the list from now on.');
      void navigate({
        to: '/w/$workspaceId/settings/workflows',
        params: { workspaceId },
        search: { edit: copy?.id ?? undefined },
      });
    },
    onError: (error) => toast.error(problemMessage(error)),
  });
}
