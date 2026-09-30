import type { BuiltInWorkflowResponse } from '@paperdotnet/client';
import { fields as jsonObject, fieldsOf, ifMatch } from '@paperdotnet/client';
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
import {
  type ParameterField,
  type ParameterInput,
  parameterFields,
  parameterInputs,
  parameterValues,
} from './built-ins';
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
  // By key: the dialogs use the latest state (its ETag) after a refresh.
  const [settingKey, setSettingKey] = useState<string>();
  const [copyingKey, setCopyingKey] = useState<string>();
  const setting = builtIns?.find((b) => b.key === settingKey);
  const copying = builtIns?.find((b) => b.key === copyingKey);
  const invalidate = () =>
    Promise.all([
      queryClient.invalidateQueries({ queryKey: builtInWorkflowsQuery(workspaceId).queryKey }),
      queryClient.invalidateQueries({ queryKey: workflowsQuery(workspaceId).queryKey }),
    ]);
  const turnOff = useMutation({
    mutationFn: (builtIn: BuiltInWorkflowResponse) =>
      workspaceBuilder(workspaceId).workflows.builtIns.byKey(builtIn.key!).put({ enabled: false }, ifMatch(builtIn)),
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
                {/* Turning off always works, also when the server no longer has what it needs. */}
                {canManage && builtIn.enabled && (
                  <Button size="sm" disabled={turnOff.isPending} onClick={() => turnOff.mutate(builtIn)}>
                    Turn off
                  </Button>
                )}
                {canManage && builtIn.available && (
                  <>
                    <Button size="sm" onClick={() => setSettingKey(builtIn.key!)} aria-label={`Set up ${builtIn.name}`}>
                      <Settings2 /> {builtIn.enabled ? 'Settings' : 'Turn on'}
                    </Button>
                    <Button
                      variant="ghost"
                      size="icon"
                      aria-label={`Copy ${builtIn.name}`}
                      onClick={() => setCopyingKey(builtIn.key!)}
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
          onClose={() => setSettingKey(undefined)}
          onSaved={invalidate}
        />
      )}
      {copying && (
        <CopyDialog
          key={copying.key}
          workspaceId={workspaceId}
          builtIn={copying}
          onClose={() => setCopyingKey(undefined)}
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
        .put({ enabled: true, parameters: jsonObject(parameterValues(fields, inputs)) }, ifMatch(builtIn)),
    onSuccess: async () => {
      toast.success(`${builtIn.name} is on.`);
      await onSaved();
      onClose();
    },
    // E.g. changed by someone else meanwhile (412): the next try uses the current state.
    onError: onSaved,
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
            <ParameterFields fields={fields} inputs={inputs} onChange={setInputs} />
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
  const fields = parameterFields(plain(builtIn.parameters));
  const [inputs, setInputs] = useState<Record<string, ParameterInput>>(() =>
    parameterInputs(fields, plain(builtIn.values)),
  );
  const copy = useMutation({
    meta: { silent: true },
    mutationFn: () =>
      workspaceBuilder(workspaceId)
        .workflows.builtIns.byKey(builtIn.key!)
        .copy.post({ name: name.trim(), parameters: jsonObject(parameterValues(fields, inputs)) }),
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
          className="flex max-h-[76vh] flex-col"
          onSubmit={(e) => {
            e.preventDefault();
            copy.mutate();
          }}
        >
          <DialogHeader>
            <DialogTitle>Copy “{builtIn.name}”</DialogTitle>
            <DialogDescription>
              The copy is a workflow of this workspace that you can change, made with these settings.
            </DialogDescription>
          </DialogHeader>
          <div className="min-h-0 space-y-3 overflow-y-auto px-5 pb-4">
            <div className="space-y-1">
              <Label htmlFor="copy-name">Name</Label>
              <Input id="copy-name" value={name} required maxLength={200} onChange={(e) => setName(e.target.value)} />
            </div>
            <ParameterFields fields={fields} inputs={inputs} onChange={setInputs} idPrefix="copy" />
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

/** The form fields of a built-in workflow's parameters. */
function ParameterFields({
  fields,
  inputs,
  onChange,
  idPrefix = 'builtin',
}: {
  fields: ParameterField[];
  inputs: Record<string, ParameterInput>;
  onChange: (inputs: Record<string, ParameterInput>) => void;
  idPrefix?: string;
}) {
  return fields.map((field) => {
    const id = `${idPrefix}-${field.name}`;
    return field.type === 'boolean' ? (
      <label key={field.name} className="flex items-center gap-2 text-[13px]">
        <Checkbox
          checked={inputs[field.name] === true}
          onChange={(e) => onChange({ ...inputs, [field.name]: e.target.checked })}
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
          onChange={(e) => onChange({ ...inputs, [field.name]: e.target.value })}
        />
        {(field.description || field.type === 'array') && (
          <p className="text-xs text-muted">
            {field.description}
            {field.type === 'array' && ' Separate several with commas.'}
          </p>
        )}
      </div>
    );
  });
}
