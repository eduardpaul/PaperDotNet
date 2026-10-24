import type { ContentClient, ContentCollection, ContentField, ContentPermission } from '@paperdotnet/client';
import { useQuery } from '@tanstack/react-query';
import { useState } from 'react';
import { api } from '@/api/client';
import { keys } from '@/api/keys';
import { Button } from '@/components/ui/button';
import { Dialog, DialogContent, DialogDescription, DialogHeader, DialogTitle } from '@/components/ui/dialog';
import { Alert } from '@/components/ui/feedback';
import { Input, Label, Textarea } from '@/components/ui/input';
import { Checkbox, Select } from '@/components/ui/select';
import { problemMessage } from '@/lib/errors';
import type { RunContentAction } from './content-screen';

export function CollectionSettings({
  open,
  onClose,
  collection,
  api: content,
  run,
  busy,
}: {
  open: boolean;
  onClose: () => void;
  collection: ContentCollection;
  api: ContentClient;
  run: RunContentAction;
  busy: boolean;
}) {
  return (
    <Dialog open={open} onOpenChange={(v) => !v && onClose()}>
      <DialogContent className="max-h-[78vh] max-w-2xl overflow-y-auto">
        <DialogHeader>
          <DialogTitle>Collection settings</DialogTitle>
          <DialogDescription>
            Existing content keeps its saved schema. Schema changes apply to future revisions.
          </DialogDescription>
        </DialogHeader>
        <div className="space-y-6 px-5 pb-5">
          <LifecycleSettings
            key={collection.resource.version}
            collection={collection}
            api={content}
            run={run}
            busy={busy}
          />
          <SchemaSettings collection={collection} api={content} run={run} busy={busy} />
          <Permissions
            key={`permissions-${collection.resource.version}`}
            collection={collection}
            api={content}
            run={run}
            busy={busy}
          />
        </div>
      </DialogContent>
    </Dialog>
  );
}
function LifecycleSettings({
  collection,
  api,
  run,
  busy,
}: {
  collection: ContentCollection;
  api: ContentClient;
  run: RunContentAction;
  busy: boolean;
}) {
  const [publishing, setPublishing] = useState(collection.resource.publishingEnabled);
  const [history, setHistory] = useState(collection.resource.versioning === 'major');
  const [max, setMax] = useState(collection.resource.maxVersions);
  return (
    <form
      className="space-y-3"
      onSubmit={(e) => {
        e.preventDefault();
        void run(() => api.settings(collection.resource, publishing, history ? 'major' : 'off', max));
      }}
    >
      <h3 className="font-medium">Publishing and history</h3>
      <Label className="flex items-center gap-2">
        <Checkbox checked={publishing} onChange={(e) => setPublishing(e.target.checked)} />
        Require publishing
      </Label>
      <Label className="flex items-center gap-2">
        <Checkbox checked={history} onChange={(e) => setHistory(e.target.checked)} />
        Keep metadata history
      </Label>
      {history && (
        <div>
          <Label htmlFor="content-history-limit">Versions to retain</Label>
          <Input
            id="content-history-limit"
            type="number"
            min={1}
            max={500}
            value={max}
            onChange={(e) => setMax(Number(e.target.value))}
          />
        </div>
      )}
      <Button type="submit" disabled={busy}>
        Save lifecycle settings
      </Button>
    </form>
  );
}
function SchemaSettings({
  collection,
  api,
  run,
  busy,
}: {
  collection: ContentCollection;
  api: ContentClient;
  run: RunContentAction;
  busy: boolean;
}) {
  const [label, setLabel] = useState('');
  const [type, setType] = useState('text');
  const [required, setRequired] = useState(false);
  const [indexed, setIndexed] = useState(false);
  const [unique, setUnique] = useState(false);
  const [choices, setChoices] = useState('');
  const [scale, setScale] = useState(2);
  const schema = collection.schema;
  if (!schema) return null;
  const fieldKey = label
    .trim()
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, '_')
    .replace(/^_|_$/g, '');
  const valid = /^[a-z][a-z0-9_]{0,63}$/.test(fieldKey) && !schema.fields.some((f) => f.id === fieldKey);
  return (
    <form
      className="space-y-3 border-t pt-5"
      onSubmit={(e) => {
        e.preventDefault();
        const field: ContentField = {
          id: fieldKey,
          label: label.trim(),
          type,
          required,
          indexed,
          definition_id: crypto.randomUUID(),
          scale: type === 'decimal' ? scale : 0,
          choices: type === 'choice' ? choices.split('\n').filter(Boolean) : [],
          options: { unique },
        };
        void run(
          () =>
            api.schema(collection.resource, {
              ...schema,
              fields: [...schema.fields, field],
              content_types: schema.content_types.map((t) => ({ ...t, field_keys: [...t.field_keys, fieldKey] })),
            }),
          () => setLabel(''),
        );
      }}
    >
      <h3 className="font-medium">Fields</h3>
      <ul className="space-y-1 text-sm text-muted">
        {schema.fields.map((f) => (
          <li key={f.id}>
            {f.label} · {f.type}
            {f.required ? ' · Required' : ''}
            {f.options?.unique ? ' · Unique' : ''}
          </li>
        ))}
      </ul>
      <div className="grid gap-3 sm:grid-cols-2">
        <div>
          <Label htmlFor="content-field-label">Field label</Label>
          <Input id="content-field-label" required value={label} onChange={(e) => setLabel(e.target.value)} />
        </div>
        <div>
          <Label htmlFor="content-field-type">Field type</Label>
          <Select id="content-field-type" value={type} onChange={(e) => setType(e.target.value)}>
            {[
              'text',
              'note',
              'integer',
              'decimal',
              'number',
              'currency',
              'boolean',
              'date',
              'datetime',
              'email',
              'url',
              'choice',
            ].map((t) => (
              <option key={t}>{t}</option>
            ))}
          </Select>
        </div>
      </div>
      {type === 'choice' && (
        <div>
          <Label htmlFor="content-field-choices">Choices (one per line)</Label>
          <Textarea id="content-field-choices" required value={choices} onChange={(e) => setChoices(e.target.value)} />
        </div>
      )}
      {type === 'decimal' && (
        <div>
          <Label htmlFor="content-field-scale">Decimal places</Label>
          <Input
            id="content-field-scale"
            type="number"
            min={0}
            max={9}
            value={scale}
            onChange={(e) => setScale(Number(e.target.value))}
          />
        </div>
      )}
      <div className="flex flex-wrap gap-4">
        <Label className="flex items-center gap-2">
          <Checkbox checked={required} onChange={(e) => setRequired(e.target.checked)} />
          Required
        </Label>
        <Label className="flex items-center gap-2">
          <Checkbox checked={indexed} onChange={(e) => setIndexed(e.target.checked)} />
          Indexed
        </Label>
        <Label className="flex items-center gap-2">
          <Checkbox checked={unique} onChange={(e) => setUnique(e.target.checked)} />
          Unique
        </Label>
      </div>
      <Button disabled={busy || !valid} type="submit">
        Add field
      </Button>
    </form>
  );
}
function Permissions({
  collection,
  api: content,
  run,
  busy,
}: {
  collection: ContentCollection;
  api: ContentClient;
  run: RunContentAction;
  busy: boolean;
}) {
  const grants = useQuery({
    queryKey: [...keys.contentCollection(collection.resource.id), 'grants'],
    queryFn: () => content.grants(collection.resource.id),
  });
  const users = useQuery({ queryKey: ['users', 'content-grants'], queryFn: () => api.v10.users.get() });
  const groups = useQuery({ queryKey: ['groups', 'content-grants'], queryFn: () => api.v10.groups.get() });
  const [draft, setDraft] = useState<ContentPermission[]>();
  const [principal, setPrincipal] = useState('');
  const [action, setAction] = useState('read');
  const rows = draft ?? grants.data?.grants ?? [];
  const principals = [
    ...(users.data?.value ?? []).map((u) => ({ key: `user:${u.id}`, name: u.displayName ?? u.userName ?? u.id })),
    ...(groups.data?.value ?? []).map((g) => ({ key: `group:${g.id}`, name: g.name })),
  ];
  return (
    <section className="space-y-3 border-t pt-5">
      <h3 className="font-medium">Permissions</h3>
      {grants.error && <Alert>{problemMessage(grants.error)}</Alert>}
      <p className="text-xs text-muted">
        Read, read drafts, edit and publish are independent permissions. Workspace owners retain management rights.
      </p>
      {grants.data?.inherit && (
        <p className="text-sm text-muted">
          Currently inherits workspace permissions. Saving grants creates unique permissions.
        </p>
      )}
      <ul className="space-y-2">
        {rows.map((g, i) => (
          <li
            key={`${g.principalType}-${g.principalId}-${g.action}`}
            className="flex items-center justify-between gap-2 text-sm"
          >
            <span>
              {principals.find((p) => p.key === `${g.principalType}:${g.principalId}`)?.name ?? g.principalId} ·{' '}
              {g.action}
            </span>
            <Button size="sm" variant="ghost" onClick={() => setDraft(rows.filter((_, n) => n !== i))}>
              Remove
            </Button>
          </li>
        ))}
      </ul>
      <div className="grid gap-2 sm:grid-cols-2">
        <Select aria-label="Permission user or group" value={principal} onChange={(e) => setPrincipal(e.target.value)}>
          <option value="">Choose user or group</option>
          {principals.map((p) => (
            <option key={p.key} value={p.key}>
              {p.name}
            </option>
          ))}
        </Select>
        <Select aria-label="Permission action" value={action} onChange={(e) => setAction(e.target.value)}>
          {['read', 'read_draft', 'write', 'publish', 'manage'].map((a) => (
            <option key={a}>{a}</option>
          ))}
        </Select>
      </div>
      <div className="flex flex-wrap gap-2">
        <Button
          disabled={!principal}
          onClick={() => {
            const [principalType, principalId] = principal.split(':');
            if (
              !rows.some(
                (g) => g.principalId === principalId && g.principalType === principalType && g.action === action,
              )
            )
              setDraft([...rows, { principalType, principalId, action }]);
          }}
        >
          Add grant
        </Button>
        <Button
          disabled={busy || draft === undefined}
          onClick={() =>
            void run(
              () => content.setGrants(collection.resource, rows, false),
              () => setDraft(undefined),
            )
          }
        >
          Save permissions
        </Button>
        <Button
          disabled={busy}
          onClick={() =>
            void run(
              () => content.setGrants(collection.resource, [], true),
              () => setDraft(undefined),
            )
          }
        >
          Inherit workspace permissions
        </Button>
      </div>
    </section>
  );
}
