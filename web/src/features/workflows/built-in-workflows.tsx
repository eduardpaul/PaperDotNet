import type { BuiltInWorkflowResponse } from '@paperdotnet/client';
import { fields as jsonObject, fieldsOf } from '@paperdotnet/client';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Copy, Settings2 } from 'lucide-react';
import { useState } from 'react';
import { toast } from 'sonner';
import { Badge } from '@/components/ui/badge';
import { Button } from '@/components/ui/button';
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from '@/components/ui/dialog';
import { Alert, Skeleton, Spinner } from '@/components/ui/feedback';
import { Input, Label } from '@/components/ui/input';
import { Checkbox } from '@/components/ui/select';
import { SettingsSection } from '@/features/settings/section';
import { problemMessage } from '@/lib/errors';
import { type ParameterInput, parameterFields, parameterInputs, parameterValues } from './built-ins';
import { builtInWorkflowsQuery, workflowsQuery } from './queries';
import { workspaceBuilder } from '@/features/workspaces/queries';

const plain = (value: BuiltInWorkflowResponse['parameters']) =>
  value ? { ...fieldsOf({ fields: value }) } : undefined;

/**
 * The product's own workflows (EVT-12): turned on per workspace with a few settings, or copied into a workflow of the
 * workspace to change them. Once on, they are listed with the workspace's workflows (read-only).
 */
export function BuiltInWorkflows({
  workspaceId,
  canManage,
  onCopied,
}: {
  workspaceId: string;
  canManage: boolean;
  onCopied: (workflowId: string) => void;
}) {
  const queryClient = useQueryClient();
  const { data: builtIns, isPending } = useQuery(builtInWorkflowsQuery(workspaceId));
  const [setting, setSetting] = useState<BuiltInWorkflowResponse>();
  const [copying, setCopying] = useState<BuiltInWorkflowResponse>();
  const invalidate = () =>
    Promise.all([
      queryClient.invalidateQueries({ queryKey: builtInWorkflowsQuery(workspaceId).queryKey }),
      queryClient.invalidateQueries({ queryKey: workflowsQuery(workspaceId).queryKey }),
    ]);
  const turnOff = useMutation({
    mutationFn: (builtIn: BuiltInWorkflowResponse) =>
      workspaceBuilder(workspaceId).workflows.builtIns.byKey(builtIn.key!).put({ enabled: false }),
    onSuccess: () => toast.success('Turned off.'),
    onSettled: invalidate,
  });

  return (
    <>
      <SettingsSection
        title="Built-in workflows"
        description="Ready-made workflows: turn them on with a few settings, or copy one to change it."
        className="px-0 pb-0"
      >
        {isPending ? (
          <Skeleton className="mx-5 mb-5 h-16" />
        ) : (
          <ul className="divide-y border-t">
            {builtIns?.map((builtIn) => (
              <li key={builtIn.key} className="flex items-center gap-3 px-5 py-3">
                <div className="min-w-0 flex-1">
                  <span className="flex items-center gap-2">
                    <span className="truncate text-[13px] font-medium">{builtIn.name}</span>
                    {builtIn.enabled ? <Badge tone="success">On</Badge> : <Badge>Off</Badge>}
                    {!builtIn.available && (
                      <Badge tone="warning">Needs {builtIn.requires === 'ai' ? 'AI' : builtIn.requires}</Badge>
                    )}
                  </span>
                  <span className="block truncate text-xs text-muted">{builtIn.description}</span>
                </div>
                {canManage && builtIn.available && (
                  <>
                    {builtIn.enabled ? (
                      <Button size="sm" disabled={turnOff.isPending} onClick={() => turnOff.mutate(builtIn)}>
                        Turn off
                      </Button>
                    ) : null}
                    <Button size="sm" onClick={() => setSetting(builtIn)} aria-label={`Set up ${builtIn.name}`}>
                      <Settings2 /> {builtIn.enabled ? 'Settings' : 'Turn on'}
                    </Button>
                    <Button
                      variant="ghost"
                      size="icon"
                      aria-label={`Copy ${builtIn.name}`}
                      onClick={() => setCopying(builtIn)}
                    >
                      <Copy />
                    </Button>
                  </>
                )}
              </li>
            ))}
          </ul>
        )}
      </SettingsSection>
      {setting && (
        <ParametersDialog
          key={setting.key}
          workspaceId={workspaceId}
          builtIn={setting}
          onClose={() => setSetting(undefined)}
          onSaved={invalidate}
        />
      )}
      {copying && (
        <CopyDialog
          key={copying.key}
          workspaceId={workspaceId}
          builtIn={copying}
          onClose={() => setCopying(undefined)}
          onCopied={async (id) => {
            await invalidate();
            onCopied(id);
          }}
        />
      )}
    </>
  );
}

/** Turns a built-in workflow on with its parameters (a form from their schema). */
function ParametersDialog({
  workspaceId,
  builtIn,
  onClose,
  onSaved,
}: {
  workspaceId: string;
  builtIn: BuiltInWorkflowResponse;
  onClose: () => void;
  onSaved: () => Promise<unknown>;
}) {
  const fields = parameterFields(plain(builtIn.parameters));
  const [inputs, setInputs] = useState<Record<string, ParameterInput>>(() =>
    parameterInputs(fields, plain(builtIn.values)),
  );
  const save = useMutation({
    meta: { silent: true },
    mutationFn: () =>
      workspaceBuilder(workspaceId)
        .workflows.builtIns.byKey(builtIn.key!)
        .put({ enabled: true, parameters: jsonObject(parameterValues(fields, inputs)) }),
    onSuccess: async () => {
      toast.success(`${builtIn.name} is on.`);
      await onSaved();
      onClose();
    },
  });

  return (
    <Dialog open onOpenChange={(open) => !open && onClose()}>
      <DialogContent>
        <form
          className="flex max-h-[76vh] flex-col"
          onSubmit={(e) => {
            e.preventDefault();
            save.mutate();
          }}
        >
          <DialogHeader>
            <DialogTitle>{builtIn.name}</DialogTitle>
            <DialogDescription>{builtIn.description}</DialogDescription>
          </DialogHeader>
          <div className="min-h-0 space-y-3 overflow-y-auto px-5 pb-4">
            {fields.map((field) => {
              const id = `builtin-${field.name}`;
              return field.type === 'boolean' ? (
                <label key={field.name} className="flex items-center gap-2 text-[13px]">
                  <Checkbox
                    checked={inputs[field.name] === true}
                    onChange={(e) => setInputs({ ...inputs, [field.name]: e.target.checked })}
                  />
                  {field.name}
                </label>
              ) : (
                <div key={field.name} className="space-y-1">
                  <Label htmlFor={id}>
                    {field.name}
                    {field.required && ' *'}
                  </Label>
                  <Input
                    id={id}
                    inputMode={field.type === 'number' || field.type === 'integer' ? 'decimal' : undefined}
                    value={String(inputs[field.name] ?? '')}
                    required={field.required}
                    onChange={(e) => setInputs({ ...inputs, [field.name]: e.target.value })}
                  />
                  {(field.description || field.type === 'array') && (
                    <p className="text-xs text-muted">
                      {field.description}
                      {field.type === 'array' && ' Separate several with commas.'}
                    </p>
                  )}
                </div>
              );
            })}
          </div>
          {save.isError && <Alert className="mx-5 mb-3">{problemMessage(save.error)}</Alert>}
          <DialogFooter>
            <Button onClick={onClose}>Cancel</Button>
            <Button type="submit" variant="primary" disabled={save.isPending}>
              {save.isPending && <Spinner />}
              Turn on
            </Button>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  );
}

/** Copies a built-in workflow into a workflow of the workspace (with its current settings) to change it. */
function CopyDialog({
  workspaceId,
  builtIn,
  onClose,
  onCopied,
}: {
  workspaceId: string;
  builtIn: BuiltInWorkflowResponse;
  onClose: () => void;
  onCopied: (workflowId: string) => Promise<unknown>;
}) {
  const [name, setName] = useState(`${builtIn.name} (copy)`);
  const copy = useMutation({
    meta: { silent: true },
    mutationFn: () =>
      workspaceBuilder(workspaceId).workflows.builtIns.byKey(builtIn.key!).copy.post({ name: name.trim() }),
    onSuccess: async (created) => {
      toast.success('Copied. The built-in workflow is off here now.');
      onClose();
      if (created?.id) await onCopied(created.id);
    },
  });

  return (
    <Dialog open onOpenChange={(open) => !open && onClose()}>
      <DialogContent>
        <form
          onSubmit={(e) => {
            e.preventDefault();
            copy.mutate();
          }}
        >
          <DialogHeader>
            <DialogTitle>Copy “{builtIn.name}”</DialogTitle>
            <DialogDescription>
              The copy is a workflow of this workspace that you can change. It uses the settings the built-in workflow
              has here.
            </DialogDescription>
          </DialogHeader>
          <div className="space-y-1 px-5 pb-4">
            <Label htmlFor="copy-name">Name</Label>
            <Input id="copy-name" value={name} required maxLength={200} onChange={(e) => setName(e.target.value)} />
          </div>
          {copy.isError && <Alert className="mx-5 mb-3">{problemMessage(copy.error)}</Alert>}
          <DialogFooter>
            <Button onClick={onClose}>Cancel</Button>
            <Button type="submit" variant="primary" disabled={copy.isPending || !name.trim()}>
              {copy.isPending && <Spinner />}
              Copy
            </Button>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  );
}
