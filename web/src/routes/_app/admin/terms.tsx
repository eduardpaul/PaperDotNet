import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute } from '@tanstack/react-router';
import { useState, type FormEvent } from 'react';
import { toast } from 'sonner';
import { api } from '@/api/client';
import { all, toArray } from '@paperdotnet/client';
import { Button } from '@/components/ui/button';
import { Alert, EmptyState, Skeleton } from '@/components/ui/feedback';
import { Input, Label } from '@/components/ui/input';
import { Checkbox, Select } from '@/components/ui/select';
import { termSetsQuery } from '@/features/list-settings/queries';
import { SettingsSection } from '@/features/settings/section';
import { problemMessage } from '@/lib/errors';

export const Route = createFileRoute('/_app/admin/terms')({ component: Terms });

const groupsQuery = {
  queryKey: ['termStore', 'groups'],
  queryFn: async () => (await api.v10.termStore.groups.get())?.value ?? [],
};

function Terms() {
  const queryClient = useQueryClient();
  const groups = useQuery(groupsQuery);
  const sets = useQuery(termSetsQuery);
  const [groupName, setGroupName] = useState('');
  const [groupId, setGroupId] = useState('');
  const [setName, setSetName] = useState('');
  const [openSet, setOpenSet] = useState(false);
  const [setId, setSetId] = useState('');
  const [termName, setTermName] = useState('');
  const refresh = async () => {
    await queryClient.invalidateQueries({ queryKey: groupsQuery.queryKey });
    await queryClient.invalidateQueries({ queryKey: termSetsQuery.queryKey });
  };
  const createGroup = useMutation({
    meta: { silent: true },
    mutationFn: () => api.v10.termStore.groups.post({ name: groupName.trim() }),
    onSuccess: async () => {
      setGroupName('');
      toast.success('Term group created.');
      await refresh();
    },
  });
  const createSet = useMutation({
    meta: { silent: true },
    mutationFn: () => api.v10.termStore.sets.post({ groupId, name: setName.trim(), isOpen: openSet }),
    onSuccess: async () => {
      setSetName('');
      toast.success('Term set created. Content types and smart folders can use it.');
      await refresh();
    },
  });
  const terms = useQuery({
    queryKey: ['termStore', 'sets', setId, 'terms'],
    enabled: !!setId,
    queryFn: () => toArray(all(api.v10.termStore.sets.bySetId(setId).terms, { queryParameters: { top: 200 } })),
  });
  const createTerm = useMutation({
    meta: { silent: true },
    mutationFn: () => api.v10.termStore.sets.bySetId(setId).terms.post({ name: termName.trim() }),
    onSuccess: async () => {
      setTermName('');
      toast.success('Term added.');
      await queryClient.invalidateQueries({ queryKey: ['termStore', 'sets', setId, 'terms'] });
    },
  });

  return (
    <>
      <SettingsSection
        title="Term store"
        description="Groups hold term sets. A set is the vocabulary a column or a smart folder uses."
      >
        {groups.isPending || sets.isPending ? (
          <Skeleton className="h-16" />
        ) : (
          <ul className="divide-y text-[13px]">
            {(groups.data ?? []).map((group) => (
              <li key={group.id} className="py-2">
                <p className="font-medium">{group.name}</p>
                <ul className="mt-1 text-muted">
                  {(sets.data ?? [])
                    .filter((set) => set.groupId === group.id)
                    .map((set) => (
                      <li key={set.id}>
                        {set.name}
                        {set.isOpen && ' · open'}
                        {set.isKeywords && ' · keywords'}
                      </li>
                    ))}
                </ul>
              </li>
            ))}
            {!groups.data?.length && <EmptyState title="No term groups yet" />}
          </ul>
        )}
      </SettingsSection>
      <form
        onSubmit={(event: FormEvent) => {
          event.preventDefault();
          createGroup.mutate();
        }}
      >
        <SettingsSection
          title="New group"
          actions={
            <Button type="submit" variant="primary" disabled={!groupName.trim() || createGroup.isPending}>
              Create group
            </Button>
          }
        >
          {createGroup.isError && <Alert>{problemMessage(createGroup.error)}</Alert>}
          <Labeled id="term-group-name" label="Name" value={groupName} onChange={setGroupName} />
        </SettingsSection>
      </form>
      <form
        onSubmit={(event: FormEvent) => {
          event.preventDefault();
          createSet.mutate();
        }}
      >
        <SettingsSection
          title="New term set"
          actions={
            <Button type="submit" variant="primary" disabled={!groupId || !setName.trim() || createSet.isPending}>
              Create term set
            </Button>
          }
        >
          {createSet.isError && <Alert>{problemMessage(createSet.error)}</Alert>}
          <div className="grid gap-3 sm:grid-cols-2">
            <div className="flex flex-col gap-1.5">
              <Label htmlFor="term-group">Group</Label>
              <Select id="term-group" value={groupId} onChange={(e) => setGroupId(e.target.value)}>
                <option value="">Choose a group…</option>
                {(groups.data ?? [])
                  .filter((group) => !group.isSystem)
                  .map((group) => (
                    <option key={group.id} value={group.id!}>
                      {group.name}
                    </option>
                  ))}
              </Select>
            </div>
            <Labeled id="term-set-name" label="Name" value={setName} onChange={setSetName} />
          </div>
          <label className="mt-3 flex items-center gap-2 text-[13px]">
            <Checkbox checked={openSet} onChange={(e) => setOpenSet(e.target.checked)} />
            Open: people can add terms while tagging
          </label>
        </SettingsSection>
      </form>
      <form
        onSubmit={(event: FormEvent) => {
          event.preventDefault();
          createTerm.mutate();
        }}
      >
        <SettingsSection
          title="Add a term"
          actions={
            <Button type="submit" variant="primary" disabled={!setId || !termName.trim() || createTerm.isPending}>
              Add term
            </Button>
          }
        >
          {createTerm.isError && <Alert>{problemMessage(createTerm.error)}</Alert>}
          <div className="grid gap-3 sm:grid-cols-2">
            <div className="flex flex-col gap-1.5">
              <Label htmlFor="term-set">Term set</Label>
              <Select id="term-set" value={setId} onChange={(e) => setSetId(e.target.value)}>
                <option value="">Choose a term set…</option>
                {(sets.data ?? [])
                  .filter((set) => !set.isKeywords)
                  .map((set) => (
                    <option key={set.id} value={set.id!}>
                      {set.name}
                    </option>
                  ))}
              </Select>
            </div>
            <Labeled id="term-name" label="Term" value={termName} onChange={setTermName} />
          </div>
          {!!terms.data?.length && (
            <p className="mt-2 text-xs text-muted">{terms.data.map((term) => term.name).join(', ')}</p>
          )}
        </SettingsSection>
      </form>
      <Keywords sets={sets.data ?? []} />
      <ImportCsv groups={groups.data ?? []} onImported={refresh} />
    </>
  );
}

function Keywords({ sets }: { sets: { id?: string | null; name?: string | null; isKeywords?: boolean | null }[] }) {
  const queryClient = useQueryClient();
  const popular = useQuery({
    queryKey: ['termStore', 'keywords', 'popular'],
    queryFn: async () => (await api.v10.termStore.keywords.popular.get({ queryParameters: { top: 50 } }))?.value ?? [],
  });
  const [termSetId, setTermSetId] = useState('');
  const promote = useMutation({
    meta: { silent: true },
    mutationFn: (id: string) => api.v10.termStore.keywords.byTermId(id).promote.post({ termSetId }),
    onSuccess: async (result) => {
      toast.success(result?.merged ? `Merged “${result.name}” into the term set.` : `Promoted “${result?.name}”.`);
      await queryClient.invalidateQueries({ queryKey: ['termStore', 'keywords', 'popular'] });
      await queryClient.invalidateQueries({ queryKey: ['termStore', 'sets'] });
    },
  });
  const targets = sets.filter((set) => !set.isKeywords);

  return (
    <SettingsSection
      title="Popular keywords"
      description="Promote a free tag into a term set. Items already tagged keep the same id, or merge when that name already exists."
    >
      {promote.isError && <Alert>{problemMessage(promote.error)}</Alert>}
      <div className="mb-3 flex max-w-sm flex-col gap-1.5">
        <Label htmlFor="promote-set">Promote into</Label>
        <Select id="promote-set" value={termSetId} onChange={(e) => setTermSetId(e.target.value)}>
          <option value="">Choose a term set…</option>
          {targets.map((set) => (
            <option key={set.id} value={set.id!}>
              {set.name}
            </option>
          ))}
        </Select>
      </div>
      {popular.isPending ? (
        <Skeleton className="h-16" />
      ) : popular.data?.length ? (
        <ul className="divide-y text-[13px]">
          {popular.data.map((keyword) => (
            <li key={keyword.id} className="flex items-center gap-2 py-2">
              <span className="min-w-0 flex-1">
                <span className="font-medium">{keyword.name}</span>
                <span className="ml-2 text-muted">
                  {keyword.usage ?? 0} {(keyword.usage ?? 0) === 1 ? 'item' : 'items'}
                </span>
              </span>
              <Button
                size="sm"
                aria-label={`Promote ${keyword.name}`}
                disabled={!termSetId || promote.isPending}
                onClick={() => promote.mutate(keyword.id!)}
              >
                Promote
              </Button>
            </li>
          ))}
        </ul>
      ) : (
        <EmptyState title="No keywords yet" />
      )}
    </SettingsSection>
  );
}

function ImportCsv({
  groups,
  onImported,
}: {
  groups: { id?: string | null; name?: string | null; isSystem?: boolean | null }[];
  onImported: () => Promise<void>;
}) {
  const [groupId, setGroupId] = useState('');
  const upload = useMutation({
    meta: { silent: true },
    mutationFn: async (file: File) =>
      api.v10.termStore.groups.byGroupId(groupId).importEscaped.post(await file.arrayBuffer()),
    onSuccess: async (result) => {
      toast.success(
        result?.created
          ? `Term set imported with ${result.termsCreated ?? 0} terms.`
          : `Added ${result?.termsCreated ?? 0} terms to the existing term set.`,
      );
      await onImported();
    },
  });

  return (
    <SettingsSection
      title="Import a term set"
      description="SharePoint CSV: Term Set Name, Term Set Description, then Level 1 Term through Level 7 Term. Existing terms are kept."
    >
      {upload.isError && <Alert>{problemMessage(upload.error)}</Alert>}
      <div className="flex max-w-sm flex-col gap-1.5">
        <Label htmlFor="import-group">Import into</Label>
        <Select id="import-group" value={groupId} onChange={(e) => setGroupId(e.target.value)}>
          <option value="">Choose a group…</option>
          {groups
            .filter((group) => !group.isSystem)
            .map((group) => (
              <option key={group.id} value={group.id!}>
                {group.name}
              </option>
            ))}
        </Select>
      </div>
      <label className="mt-3 inline-flex cursor-pointer items-center gap-2 text-[13px]">
        <input
          type="file"
          accept=".csv,text/csv"
          aria-label="Term set CSV"
          className="text-xs"
          disabled={!groupId || upload.isPending}
          onChange={(e) => {
            const file = e.target.files?.[0];
            if (file) upload.mutate(file);
            e.target.value = '';
          }}
        />
        {upload.isPending ? 'Importing…' : 'Choose a CSV'}
      </label>
    </SettingsSection>
  );
}

function Labeled({
  id,
  label,
  value,
  onChange,
}: {
  id?: string;
  label: string;
  value: string;
  onChange: (value: string) => void;
}) {
  const fieldId = id ?? label.toLowerCase().replace(/\W+/g, '-');
  return (
    <div className="flex flex-col gap-1.5">
      <Label htmlFor={fieldId}>{label}</Label>
      <Input id={fieldId} required value={value} onChange={(e) => onChange(e.target.value)} />
    </div>
  );
}
