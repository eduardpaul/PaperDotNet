import type { SmartFolderResponse } from '@paperdotnet/client';
import { all, ifMatch, toArray } from '@paperdotnet/client';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useNavigate } from '@tanstack/react-router';
import { Plus, X } from 'lucide-react';
import { useDeferredValue, useState, type FormEvent } from 'react';
import { api } from '@/api/client';
import { keys } from '@/api/keys';
import { workspacesQuery } from '@/api/queries';
import { Button } from '@/components/ui/button';
import { Combobox } from '@/components/ui/combobox';
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from '@/components/ui/dialog';
import { Alert } from '@/components/ui/feedback';
import { Input, Label, Textarea } from '@/components/ui/input';
import { Select } from '@/components/ui/select';
import { useTerms } from '@/features/fields/directory';
import { termLabel } from '@/features/fields/lookups';
import { problemMessage } from '@/lib/errors';
import { useFormat } from '@/lib/preferences';
import { cn } from '@/lib/utils';

const kinds = [
  { key: 'documents', label: 'Documents' },
  { key: 'tasks', label: 'Tasks' },
  { key: 'calendar', label: 'Events' },
  { key: 'notes', label: 'Notes' },
  { key: 'contacts', label: 'Contacts' },
];

interface Level {
  field: string;
  by: string;
}

/** Creates or edits a smart folder (TAX-08): what it shows (kinds, tags, filter) and how it groups (TAX-10). */
export function SmartFolderDialog({
  folder,
  open,
  onOpenChange,
}: {
  folder?: SmartFolderResponse;
  open: boolean;
  onOpenChange: (open: boolean) => void;
}) {
  const format = useFormat();
  const queryClient = useQueryClient();
  const navigate = useNavigate();
  const { data: workspaces } = useQuery(workspacesQuery);
  const definition = folder?.definition;
  const [name, setName] = useState(folder?.name ?? '');
  const [description, setDescription] = useState(folder?.description ?? '');
  const [scope, setScope] = useState(folder ? (folder.personal ? '' : (folder.workspaceId ?? '')) : '');
  const [templates, setTemplates] = useState<string[]>(definition?.listTemplates ?? []);
  const [terms, setTerms] = useState<string[]>(definition?.terms ?? []);
  const [termMatch, setTermMatch] = useState(definition?.termMatch ?? 'all');
  const [filter, setFilter] = useState(definition?.filter ?? '');
  const [levels, setLevels] = useState<Level[]>(
    (definition?.groupBy ?? []).map((g) => ({ field: g.field ?? '', by: g.by ?? '' })),
  );
  const [setId, setSetId] = useState('');
  const [termSearch, setTermSearch] = useState('');
  const text = useDeferredValue(termSearch.trim());
  const known = useTerms(terms);
  const { data: sets } = useQuery({
    queryKey: ['termSets'],
    enabled: open,
    queryFn: () => toArray(all(api.v10.termStore.sets, { queryParameters: { top: 200 } })),
  });
  const { data: found, isFetching } = useQuery({
    queryKey: ['termSets', setId, 'terms', text],
    enabled: open && !!setId,
    queryFn: async () =>
      (
        await api.v10.termStore.sets
          .bySetId(setId)
          .terms.get({ queryParameters: { search: text || undefined, top: 50 } })
      )?.value ?? [],
  });

  const save = useMutation({
    meta: { silent: true },
    mutationFn: async () => {
      const body = {
        name: name.trim(),
        description: description.trim() || undefined,
        personal: !scope,
        workspaceId: scope || undefined,
        definition: {
          listTemplates: templates.length ? templates : undefined,
          terms: terms.length ? terms : undefined,
          termMatch: terms.length > 1 ? termMatch : undefined,
          filter: filter.trim() || undefined,
          groupBy: levels.filter((l) => l.field.trim()).map((l) => ({ field: l.field.trim(), by: l.by || undefined })),
        },
      };
      return folder
        ? await api.v10.smartFolders.byId(folder.id!).patch(body, ifMatch(folder))
        : await api.v10.smartFolders.post(body);
    },
    onSuccess: async (saved) => {
      await queryClient.invalidateQueries({ queryKey: keys.smartFolders });
      onOpenChange(false);
      if (!folder && saved?.id) await navigate({ to: '/f/$folderId', params: { folderId: saved.id } });
    },
  });

  const onSubmit = (event: FormEvent) => {
    event.preventDefault();
    save.mutate();
  };

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className="top-[6vh] max-h-[88vh] max-w-xl overflow-y-auto">
        <form onSubmit={onSubmit}>
          <DialogHeader>
            <DialogTitle>{folder ? 'Edit smart folder' : 'New smart folder'}</DialogTitle>
            <DialogDescription>
              Shows every matching item of all lists, by meaning instead of location.
            </DialogDescription>
          </DialogHeader>
          <div className="flex flex-col gap-4 px-5 pb-5">
            {save.isError && <Alert>{problemMessage(save.error)}</Alert>}
            <div className="flex flex-col gap-1.5">
              <Label htmlFor="folder-name">Name</Label>
              <Input id="folder-name" required maxLength={200} value={name} onChange={(e) => setName(e.target.value)} />
            </div>
            <div className="flex flex-col gap-1.5">
              <Label htmlFor="folder-description">Description</Label>
              <Textarea
                id="folder-description"
                rows={2}
                value={description}
                onChange={(e) => setDescription(e.target.value)}
              />
            </div>
            <div className="flex flex-col gap-1.5">
              <Label htmlFor="folder-scope">Who sees it</Label>
              <Select id="folder-scope" value={scope} onChange={(e) => setScope(e.target.value)} disabled={!!folder}>
                <option value="">Only me (all my workspaces)</option>
                {(workspaces ?? [])
                  .filter((w) => !w.isPersonal)
                  .map((w) => (
                    <option key={w.id} value={w.id!}>
                      Everyone in {w.name}
                    </option>
                  ))}
              </Select>
            </div>
            <fieldset className="flex flex-col gap-1.5">
              <legend className="mb-1.5 text-[13px] font-medium">Kinds of items</legend>
              <div className="flex flex-wrap gap-1.5">
                {kinds.map((k) => {
                  const on = templates.includes(k.key);
                  return (
                    <button
                      key={k.key}
                      type="button"
                      aria-pressed={on}
                      onClick={() => setTemplates(on ? templates.filter((t) => t !== k.key) : [...templates, k.key])}
                      className={cn(
                        'rounded-full border px-2.5 py-1 text-xs',
                        on ? 'border-accent bg-accent-soft font-medium text-accent' : 'hover:bg-surface-muted',
                      )}
                    >
                      {k.label}
                    </button>
                  );
                })}
              </div>
              <p className="text-xs text-muted">None selected: every kind.</p>
            </fieldset>
            <fieldset className="flex flex-col gap-1.5">
              <legend id="folder-tags-label" className="mb-1.5 text-[13px] font-medium">
                Tags
              </legend>
              <div className="flex gap-2">
                <Select
                  aria-label="Term set"
                  className="w-44 shrink-0"
                  value={setId}
                  onChange={(e) => setSetId(e.target.value)}
                >
                  <option value="">Term set…</option>
                  {(sets ?? []).map((s) => (
                    <option key={s.id} value={s.id!}>
                      {s.name}
                    </option>
                  ))}
                </Select>
                <div className="min-w-0 flex-1">
                  <Combobox
                    aria-labelledby="folder-tags-label"
                    multiple
                    disabled={!setId && terms.length === 0}
                    loading={isFetching}
                    placeholder={setId ? 'Search tags…' : 'Choose a term set first'}
                    onSearch={setTermSearch}
                    selected={terms.map((id) => ({
                      value: id,
                      label:
                        termLabel(known.get(id) ?? found?.find((t) => t.id === id), format.preferences.language) ?? '…',
                      color: known.get(id)?.color,
                    }))}
                    options={(found ?? []).map((t) => ({
                      value: t.id!,
                      label: termLabel(t, format.preferences.language) ?? '',
                      color: t.color,
                    }))}
                    onChange={(options) => setTerms(options.map((o) => o.value))}
                  />
                </div>
              </div>
              {terms.length > 1 && (
                <Select aria-label="Tag match" value={termMatch} onChange={(e) => setTermMatch(e.target.value)}>
                  <option value="all">Items with all of these tags</option>
                  <option value="any">Items with any of these tags</option>
                </Select>
              )}
              <p className="text-xs text-muted">A tag also matches its child tags.</p>
            </fieldset>
            <div className="flex flex-col gap-1.5">
              <Label htmlFor="folder-filter">Condition (optional)</Label>
              <Input
                id="folder-filter"
                className="font-mono text-xs"
                placeholder="fields/assignedTo eq @me and fields/dueDate le @next7Days"
                value={filter}
                onChange={(e) => setFilter(e.target.value)}
              />
            </div>
            <fieldset className="flex flex-col gap-2">
              <legend className="mb-1.5 text-[13px] font-medium">Sub-folders (optional)</legend>
              {levels.map((level, index) => (
                <div key={index} className="flex gap-2">
                  <Input
                    aria-label={`Group level ${index + 1} field`}
                    placeholder="Field name, e.g. dueDate or status"
                    value={level.field}
                    onChange={(e) =>
                      setLevels(levels.map((l, i) => (i === index ? { ...l, field: e.target.value } : l)))
                    }
                  />
                  <Select
                    aria-label={`Group level ${index + 1} by`}
                    className="w-32 shrink-0"
                    value={level.by}
                    onChange={(e) => setLevels(levels.map((l, i) => (i === index ? { ...l, by: e.target.value } : l)))}
                  >
                    <option value="">Value</option>
                    <option value="year">Year</option>
                    <option value="month">Month</option>
                  </Select>
                  <Button
                    size="icon"
                    variant="ghost"
                    aria-label="Remove level"
                    onClick={() => setLevels(levels.filter((_, i) => i !== index))}
                  >
                    <X />
                  </Button>
                </div>
              ))}
              {levels.length < 3 && (
                <Button
                  size="sm"
                  variant="ghost"
                  className="self-start"
                  onClick={() => setLevels([...levels, { field: '', by: '' }])}
                >
                  <Plus /> Add a level
                </Button>
              )}
            </fieldset>
          </div>
          <DialogFooter>
            <Button onClick={() => onOpenChange(false)}>Cancel</Button>
            <Button type="submit" variant="primary" disabled={!name.trim() || save.isPending}>
              {folder ? 'Save' : 'Create folder'}
            </Button>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  );
}
