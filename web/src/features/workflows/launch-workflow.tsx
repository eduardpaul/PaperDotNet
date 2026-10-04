import type { WorkflowResponse } from '@paperdotnet/client';
import { fields, fieldsOf } from '@paperdotnet/client';
import type { RJSFSchema } from '@rjsf/utils';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Play } from 'lucide-react';
import { useState } from 'react';
import { toast } from 'sonner';
import { Button } from '@/components/ui/button';
import { Alert } from '@/components/ui/feedback';
import { Label } from '@/components/ui/input';
import { Select } from '@/components/ui/select';
import { Sheet, SheetClose, SheetContent, SheetDescription, SheetTitle } from '@/components/ui/sheet';
import { workspaceBuilder } from '@/features/workspaces/queries';
import { problemMessage } from '@/lib/errors';
import { workflowsQuery } from './queries';
import { manualWorkflows } from './model';

import { SchemaForm } from './schema-form';

export function LaunchWorkflowButton({
  workspaceId,
  list,
  itemIds,
  workflow,
}: {
  workspaceId: string;
  list?: { id?: string | null; name?: string | null };
  itemIds?: string[];
  workflow?: WorkflowResponse;
}) {
  const [open, setOpen] = useState(false);
  const { data } = useQuery(workflowsQuery(workspaceId));
  const candidates = manualWorkflows(workflow ? [workflow] : (data ?? []), list);
  if (!candidates.length) return null;
  return (
    <>
      <Button size="sm" disabled={!!list && (!itemIds?.length || itemIds.length > 100)} onClick={() => setOpen(true)}>
        <Play /> {workflow ? 'Launch' : 'Run workflow'}
      </Button>
      {open && (
        <LaunchWorkflow
          workspaceId={workspaceId}
          workflows={candidates}
          listId={list?.id ?? undefined}
          itemIds={itemIds}
          onClose={() => setOpen(false)}
        />
      )}
    </>
  );
}

function LaunchWorkflow({
  workspaceId,
  workflows,
  listId,
  itemIds,
  onClose,
}: {
  workspaceId: string;
  workflows: WorkflowResponse[];
  listId?: string;
  itemIds?: string[];
  onClose: () => void;
}) {
  const queryClient = useQueryClient();
  const [id, setId] = useState(workflows[0]!.id!);
  const selected = workflows.find((workflow) => workflow.id === id) ?? workflows[0]!;
  const manual = (selected.triggers ?? (selected.trigger ? [selected.trigger] : [])).find((t) => t.type === 'manual');
  const schema = selected.inputSchema ?? manual?.inputs;
  const launch = useMutation({
    meta: { silent: true },
    mutationFn: (input: Record<string, unknown>) =>
      workspaceBuilder(workspaceId)
        .workflows.byId(selected.id!)
        .runs.post({
          listId,
          itemIds,
          inputs: fields(input),
        }),
    onSuccess: async (runs) => {
      toast.success(
        runs?.length
          ? `${runs.length} workflow ${runs.length === 1 ? 'run' : 'runs'} started.`
          : 'No runs started: a run is already active.',
      );
      await queryClient.invalidateQueries({ queryKey: ['workspaces', workspaceId] });
      onClose();
    },
  });
  return (
    <Sheet open onOpenChange={(open) => !open && onClose()}>
      <SheetContent className="flex flex-col overflow-y-auto sm:max-w-lg">
        <div className="flex flex-col gap-4 p-5">
          <SheetTitle>Run workflow</SheetTitle>
          <SheetDescription>
            {listId
              ? `Run once for each of the ${itemIds?.length ?? 0} selected items.`
              : 'Run once in this workspace.'}
          </SheetDescription>
          <SheetClose className="absolute top-3 right-3" />
          <Label htmlFor="launch-workflow">Workflow</Label>
          <Select
            id="launch-workflow"
            value={selected.id!}
            disabled={launch.isPending}
            onChange={(event) => {
              setId(event.target.value);
              launch.reset();
            }}
          >
            {workflows.map((workflow) => (
              <option key={workflow.id} value={workflow.id!}>
                {workflow.name}
              </option>
            ))}
          </Select>
          {selected.description && <p className="text-sm text-muted">{selected.description}</p>}
          <SchemaForm
            key={selected.id}
            schema={(schema ? fieldsOf({ fields: schema }) : { type: 'object', properties: {} }) as RJSFSchema}
            disabled={launch.isPending}
            onSubmit={(data) => launch.mutate(data)}
          >
            {launch.isError && <Alert>{problemMessage(launch.error)}</Alert>}
            <div className="flex justify-end gap-2">
              <Button type="button" onClick={onClose}>
                Cancel
              </Button>
              <Button type="submit" variant="primary" disabled={launch.isPending}>
                Launch workflow
              </Button>
            </div>
          </SchemaForm>
        </div>
      </SheetContent>
    </Sheet>
  );
}
