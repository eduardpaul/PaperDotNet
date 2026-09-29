import type { TermResponse, TermSetResponse } from '@paperdotnet/client';
import { all, toArray } from '@paperdotnet/client';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { ArrowUpRight, ChevronRight, FileUp, FolderTree, Pencil, Plus, Tag, Tags } from 'lucide-react';
import { useState, type FormEvent } from 'react';
import { toast } from 'sonner';
import { api } from '@/api/client';
import { Badge } from '@/components/ui/badge';
import { Button } from '@/components/ui/button';
import { Card } from '@/components/ui/card';
import { Dialog, DialogContent, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog';
import { Alert, EmptyState, Skeleton } from '@/components/ui/feedback';
import { Input, Textarea } from '@/components/ui/input';
import { Checkbox, Select } from '@/components/ui/select';
import { Field } from '@/features/admin/common';
import { termSetsQuery } from '@/features/list-settings/queries';
import { SettingsSection } from '@/features/settings/section';
import { problemMessage } from '@/lib/errors';
import { cn } from '@/lib/utils';

interface TermsSearch {
  set?: string;
}

export const Route = createFileRoute('/_app/admin/terms')({
  validateSearch: (search: Record<string, unknown>): TermsSearch => ({
    set: typeof search.set === 'string' ? search.set : undefined,
  }),
  component: TermStore,
});

const groupsQuery = {
  queryKey: ['termStore', 'groups'],
  queryFn: () => toArray(all(api.v10.termStore.groups, { queryParameters: { top: 200 } })),
};

const termsKey = (setId: string, parentId?: string) => ['termStore', 'sets', setId, 'terms', parentId ?? 'root'];

/** The term store (TAX-01…03, TAX-05, TAX-11): groups, term sets and their terms, CSV import, keyword promotion. */
function TermStore() {
  const { set: setId } = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const { data: groups, isPending } = useQuery(groupsQuery);
  const { data: sets } = useQuery(termSetsQuery);
  const [dialog, setDialog] = useState<
    { kind: 'group' } | { kind: 'set'; groupId: string } | { kind: 'import'; groupId: string } | undefined
  >();
  const selected = sets?.find((s) => s.id === setId);

  return (
    <>
      <div className="grid gap-5 lg:grid-cols-[18rem_1fr]">
        <Card className="self-start">
          <header className="flex items-center gap-2 border-b px-4 py-3">
            <FolderTree className="size-4 text-muted" />
            <h2 className="flex-1 text-sm font-semibold">Term sets</h2>
            <Button size="sm" variant="ghost" onClick={() => setDialog({ kind: 'group' })}>
              <Plus /> Group
            </Button>
          </header>
          {isPending ? (
            <Skeleton className="m-4 h-24" />
          ) : (
            <ul className="flex flex-col gap-3 p-3">
              {(groups ?? []).map((group) => (
                <li key={group.id}>
                  <div className="flex items-center gap-1 px-1 text-xs font-medium text-muted">
                    <span className="flex-1 truncate">{group.name}</span>
                    {!group.isSystem && (
                      <>
                        <Button
                          size="icon"
                          variant="ghost"
                          aria-label={`Import terms into ${group.name}`}
                          onClick={() => setDialog({ kind: 'import', groupId: group.id! })}
                        >
                          <FileUp />
                        </Button>
                        <Button
                          size="icon"
                          variant="ghost"
                          aria-label={`New term set in ${group.name}`}
                          onClick={() => setDialog({ kind: 'set', groupId: group.id! })}
                        >
                          <Plus />
                        </Button>
                      </>
                    )}
                  </div>
                  <ul>
                    {(sets ?? [])
                      .filter((s) => s.groupId === group.id)
                      .map((set) => (
                        <li key={set.id}>
                          <button
                            type="button"
                            aria-current={set.id === setId ? 'page' : undefined}
                            onClick={() => void navigate({ search: { set: set.id! } })}
                            className={cn(
                              'flex w-full items-center gap-2 rounded-md px-2 py-1.5 text-left text-[13px] hover:bg-surface-muted',
                              set.id === setId && 'bg-accent-soft font-medium text-accent',
                            )}
                          >
                            <Tags className="size-3.5" />
                            <span className="flex-1 truncate">{set.name}</span>
                            {set.isKeywords && <Badge>Keywords</Badge>}
                          </button>
                        </li>
                      ))}
                  </ul>
                </li>
              ))}
            </ul>
          )}
        </Card>
        <div className="flex min-w-0 flex-col gap-5">
          {selected ? (
            selected.isKeywords ? (
              <PopularKeywords sets={(sets ?? []).filter((s) => !s.isKeywords)} />
            ) : (
              <TermSetView set={selected} />
            )
          ) : (
            <Card>
              <EmptyState icon={Tags} title="Choose a term set">
                Terms are shared tags and categories: managed metadata fields pick them from a set, keywords are free
                tags people add.
              </EmptyState>
            </Card>
          )}
        </div>
      </div>
      {dialog?.kind === 'group' && (
        <NameDialog
          title="New term group"
          onClose={() => setDialog(undefined)}
          onSave={(name, description) => api.v10.termStore.groups.post({ name, description })}
          invalidate={groupsQuery.queryKey}
        />
      )}
      {dialog?.kind === 'set' && (
        <NameDialog
          title="New term set"
          withOpen
          onClose={() => setDialog(undefined)}
          onSave={(name, description, isOpen) =>
            api.v10.termStore.sets.post({ groupId: dialog.groupId, name, description, isOpen })
          }
          invalidate={termSetsQuery.queryKey}
        />
      )}
      {dialog?.kind === 'import' && <ImportDialog groupId={dialog.groupId} onClose={() => setDialog(undefined)} />}
    </>
  );
}

function TermSetView({ set }: { set: TermSetResponse }) {
  const [adding, setAdding] = useState(false);
  return (
    <SettingsSection
      title={set.name ?? ''}
      description={
        <>
          {set.description && <>{set.description} · </>}
          {set.isOpen ? 'Open: people can add terms while tagging.' : 'Closed: only term store managers add terms.'}
        </>
      }
      className="px-0 pb-0"
      actions={
        <Button variant="primary" onClick={() => setAdding(true)}>
          <Plus /> New term
        </Button>
      }
    >
      <div className="border-t py-1">
        <TermLevel setId={set.id!} depth={0} />
      </div>
      {adding && <TermDialog setId={set.id!} onClose={() => setAdding(false)} />}
    </SettingsSection>
  );
}

function TermLevel({ setId, parentId, depth }: { setId: string; parentId?: string; depth: number }) {
  const { data: terms, isPending } = useQuery({
    queryKey: termsKey(setId, parentId),
    queryFn: () =>
      toArray(
        all(api.v10.termStore.sets.bySetId(setId).terms, {
          queryParameters: { parentId, includeDeprecated: true, top: 200 },
        }),
      ),
  });
  if (isPending) return <Skeleton className="mx-5 my-2 h-6" />;
  if (!terms?.length) return depth === 0 ? <p className="px-5 py-4 text-[13px] text-muted">No terms yet.</p> : null;
  return (
    <ul>
      {terms.map((term) => (
        <TermRow key={term.id} term={term} depth={depth} />
      ))}
    </ul>
  );
}

function TermRow({ term, depth }: { term: TermResponse; depth: number }) {
  const [open, setOpen] = useState(false);
  const [editing, setEditing] = useState(false);
  const [addingChild, setAddingChild] = useState(false);
  return (
    <li>
      <div
        className="group flex items-center gap-2 py-1 pr-5 hover:bg-surface-muted/50"
        style={{ paddingLeft: `${1.25 + depth * 1.25}rem` }}
      >
        <button
          type="button"
          aria-label={open ? `Collapse ${term.name}` : `Expand ${term.name}`}
          className={cn('text-muted', !term.hasChildren && 'invisible')}
          onClick={() => setOpen(!open)}
        >
          <ChevronRight className={cn('size-4 transition-transform', open && 'rotate-90')} />
        </button>
        <span className="size-2.5 rounded-full" style={{ background: term.color ?? 'var(--color-border)' }} />
        <span className={cn('flex-1 truncate text-[13px]', term.isDeprecated && 'text-muted line-through')}>
          {term.name}
        </span>
        {!!term.synonyms?.length && <span className="truncate text-xs text-muted">{term.synonyms.join(', ')}</span>}
        {term.isDeprecated && <Badge>Deprecated</Badge>}
        <Button
          size="icon"
          variant="ghost"
          aria-label={`Add a term under ${term.name}`}
          onClick={() => setAddingChild(true)}
        >
          <Plus />
        </Button>
        <Button size="icon" variant="ghost" aria-label={`Edit ${term.name}`} onClick={() => setEditing(true)}>
          <Pencil />
        </Button>
      </div>
      {open && <TermLevel setId={term.termSetId!} parentId={term.id!} depth={depth + 1} />}
      {editing && <TermDialog setId={term.termSetId!} term={term} onClose={() => setEditing(false)} />}
      {addingChild && <TermDialog setId={term.termSetId!} parentId={term.id!} onClose={() => setAddingChild(false)} />}
    </li>
  );
}

function TermDialog({
  setId,
  term,
  parentId,
  onClose,
}: {
  setId: string;
  term?: TermResponse;
  parentId?: string;
  onClose: () => void;
}) {
  const queryClient = useQueryClient();
  const [name, setName] = useState(term?.name ?? '');
  const [description, setDescription] = useState(term?.description ?? '');
  const [color, setColor] = useState(term?.color ?? '');
  const [synonyms, setSynonyms] = useState((term?.synonyms ?? []).join(', '));
  const [deprecated, setDeprecated] = useState(!!term?.isDeprecated);
  const [mergeInto, setMergeInto] = useState('');
  const { data: others } = useQuery({
    queryKey: ['termStore', 'sets', setId, 'all'],
    queryFn: () => toArray(all(api.v10.termStore.sets.bySetId(setId).terms, { queryParameters: { top: 200 } })),
    enabled: !!term,
  });
  const done = async (message: string) => {
    toast.success(message);
    await queryClient.invalidateQueries({ queryKey: ['termStore', 'sets', setId] });
    onClose();
  };
  const list = (text: string) =>
    text
      .split(',')
      .map((s) => s.trim())
      .filter(Boolean);
  const save = useMutation({
    meta: { silent: true },
    mutationFn: () => {
      const terms = api.v10.termStore.sets.bySetId(setId).terms;
      return term
        ? terms.byTermId(term.id!).patch({
            name: name.trim(),
            description: description.trim(),
            color: color || null,
            synonyms: list(synonyms),
            isDeprecated: deprecated,
          })
        : terms.post({
            name: name.trim(),
            parentId,
            description: description.trim() || undefined,
            color: color || undefined,
            synonyms: list(synonyms),
          });
    },
    onSuccess: () => done(term ? 'Term saved.' : 'Term created.'),
  });
  const merge = useMutation({
    meta: { silent: true },
    mutationFn: () =>
      api.v10.termStore.sets.bySetId(setId).terms.byTermId(term!.id!).merge.post({ targetTermId: mergeInto }),
    onSuccess: () => done('Terms merged: items now carry the other term.'),
  });
  const onSubmit = (event: FormEvent) => {
    event.preventDefault();
    save.mutate();
  };
  return (
    <Dialog open onOpenChange={(o) => !o && onClose()}>
      <DialogContent>
        <form onSubmit={onSubmit}>
          <DialogHeader>
            <DialogTitle>{term ? `Term “${term.name}”` : parentId ? 'New term below' : 'New term'}</DialogTitle>
          </DialogHeader>
          <div className="flex flex-col gap-3 px-5 pb-4">
            <Field id="term-name" label="Name">
              <Input id="term-name" required maxLength={200} value={name} onChange={(e) => setName(e.target.value)} />
            </Field>
            <Field
              id="term-synonyms"
              label="Synonyms"
              hint="Separated by commas; search and tagging find the term by them."
            >
              <Input id="term-synonyms" value={synonyms} onChange={(e) => setSynonyms(e.target.value)} />
            </Field>
            <div className="grid grid-cols-[1fr_auto] gap-3">
              <Field id="term-description" label="Description">
                <Input id="term-description" value={description} onChange={(e) => setDescription(e.target.value)} />
              </Field>
              <Field id="term-color" label="Color">
                <Input
                  id="term-color"
                  type="color"
                  className="w-16 p-1"
                  value={color || '#8b8b8b'}
                  onChange={(e) => setColor(e.target.value)}
                />
              </Field>
            </div>
            {term && (
              <>
                <label className="flex items-center gap-2 text-[13px]">
                  <Checkbox checked={deprecated} onChange={(e) => setDeprecated(e.target.checked)} />
                  Deprecated: stays on items, cannot be chosen any more
                </label>
                <div className="flex items-end gap-2 rounded-md border p-3">
                  <Field id="term-merge" label="Merge into">
                    <Select id="term-merge" value={mergeInto} onChange={(e) => setMergeInto(e.target.value)}>
                      <option value="">Choose a term…</option>
                      {(others ?? [])
                        .filter((t) => t.id !== term.id && !t.mergedIntoId)
                        .map((t) => (
                          <option key={t.id} value={t.id!}>
                            {t.name}
                          </option>
                        ))}
                    </Select>
                  </Field>
                  <Button type="button" disabled={!mergeInto || merge.isPending} onClick={() => merge.mutate()}>
                    Merge
                  </Button>
                </div>
              </>
            )}
            {(save.isError || merge.isError) && <Alert>{problemMessage(save.error ?? merge.error)}</Alert>}
          </div>
          <DialogFooter>
            <Button type="button" onClick={onClose}>
              Cancel
            </Button>
            <Button type="submit" variant="primary" disabled={!name.trim() || save.isPending}>
              {term ? 'Save' : 'Create term'}
            </Button>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  );
}

function NameDialog({
  title,
  withOpen,
  invalidate,
  onSave,
  onClose,
}: {
  title: string;
  withOpen?: boolean;
  invalidate: readonly unknown[];
  onSave: (name: string, description: string | undefined, isOpen: boolean) => Promise<unknown>;
  onClose: () => void;
}) {
  const queryClient = useQueryClient();
  const [name, setName] = useState('');
  const [description, setDescription] = useState('');
  const [isOpen, setIsOpen] = useState(false);
  const save = useMutation({
    meta: { silent: true },
    mutationFn: () => onSave(name.trim(), description.trim() || undefined, isOpen),
    onSuccess: async () => {
      toast.success('Created.');
      await queryClient.invalidateQueries({ queryKey: invalidate });
      onClose();
    },
  });
  return (
    <Dialog open onOpenChange={(o) => !o && onClose()}>
      <DialogContent>
        <form
          onSubmit={(e) => {
            e.preventDefault();
            save.mutate();
          }}
        >
          <DialogHeader>
            <DialogTitle>{title}</DialogTitle>
          </DialogHeader>
          <div className="flex flex-col gap-3 px-5 pb-4">
            <Field id="ts-name" label="Name">
              <Input id="ts-name" required maxLength={200} value={name} onChange={(e) => setName(e.target.value)} />
            </Field>
            <Field id="ts-description" label="Description">
              <Textarea
                id="ts-description"
                rows={2}
                value={description}
                onChange={(e) => setDescription(e.target.value)}
              />
            </Field>
            {withOpen && (
              <label className="flex items-center gap-2 text-[13px]">
                <Checkbox checked={isOpen} onChange={(e) => setIsOpen(e.target.checked)} />
                Open: people may add terms while tagging
              </label>
            )}
            {save.isError && <Alert>{problemMessage(save.error)}</Alert>}
          </div>
          <DialogFooter>
            <Button type="button" onClick={onClose}>
              Cancel
            </Button>
            <Button type="submit" variant="primary" disabled={!name.trim() || save.isPending}>
              Create
            </Button>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  );
}

/** CSV with a term set per file: "Term,Parent" rows (TAX-11). */
function ImportDialog({ groupId, onClose }: { groupId: string; onClose: () => void }) {
  const queryClient = useQueryClient();
  const [csv, setCsv] = useState('');
  const importTerms = useMutation({
    meta: { silent: true },
    mutationFn: async () =>
      (await api.v10.termStore.groups
        .byGroupId(groupId)
        .importEscaped.post(new TextEncoder().encode(csv).buffer as ArrayBuffer))!,
    onSuccess: async (result) => {
      toast.success(`${result.termsCreated} terms imported${result.created ? ' into a new term set' : ''}.`);
      await queryClient.invalidateQueries({ queryKey: ['termStore'] });
      onClose();
    },
  });
  return (
    <Dialog open onOpenChange={(o) => !o && onClose()}>
      <DialogContent className="max-w-xl">
        <DialogHeader>
          <DialogTitle>Import terms from CSV</DialogTitle>
        </DialogHeader>
        <div className="flex flex-col gap-3 px-5 pb-4">
          <p className="text-xs text-muted">
            The format of SharePoint term set imports: a header row, then one term per row with its levels in columns
            (term set name first). Choose a file or paste the text.
          </p>
          <Input
            type="file"
            accept=".csv,text/csv"
            aria-label="CSV file"
            onChange={async (e) => {
              const file = e.target.files?.[0];
              if (file) setCsv(await file.text());
            }}
          />
          <Textarea
            aria-label="CSV"
            rows={8}
            className="font-mono text-xs"
            value={csv}
            onChange={(e) => setCsv(e.target.value)}
          />
          {importTerms.isError && <Alert>{problemMessage(importTerms.error)}</Alert>}
        </div>
        <DialogFooter>
          <Button onClick={onClose}>Cancel</Button>
          <Button
            variant="primary"
            disabled={!csv.trim() || importTerms.isPending}
            onClick={() => importTerms.mutate()}
          >
            Import
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}

/** The most used keywords, to promote into a managed term set (TAX-05). */
function PopularKeywords({ sets }: { sets: TermSetResponse[] }) {
  const queryClient = useQueryClient();
  const { data } = useQuery({
    queryKey: ['termStore', 'keywords', 'popular'],
    queryFn: async () => (await api.v10.termStore.keywords.popular.get({ queryParameters: { top: 50 } }))?.value ?? [],
  });
  const [target, setTarget] = useState(sets[0]?.id ?? '');
  const promote = useMutation({
    meta: { silent: true },
    mutationFn: (termId: string) => api.v10.termStore.keywords.byTermId(termId).promote.post({ termSetId: target }),
    onSuccess: async (result) => {
      toast.success(result?.merged ? `“${result.name}” merged into an existing term.` : `“${result?.name}” promoted.`);
      await queryClient.invalidateQueries({ queryKey: ['termStore'] });
    },
  });
  return (
    <SettingsSection
      title="Keywords"
      description="Free tags people added. Promote the popular ones into a term set; items keep them."
      className="px-0 pb-0"
      actions={
        <label className="flex items-center gap-2 text-[13px]">
          Promote into
          <Select aria-label="Promote into" className="w-56" value={target} onChange={(e) => setTarget(e.target.value)}>
            {sets.map((s) => (
              <option key={s.id} value={s.id!}>
                {s.name}
              </option>
            ))}
          </Select>
        </label>
      }
    >
      {data?.length ? (
        <ul className="divide-y border-t">
          {data.map((keyword) => (
            <li key={keyword.id} className="flex items-center gap-3 px-5 py-2">
              <Tag className="size-3.5 text-muted" />
              <span className="flex-1 text-[13px]">{keyword.name}</span>
              <span className="text-xs text-muted">{keyword.usage} items</span>
              <Button
                size="sm"
                variant="ghost"
                aria-label={`Promote ${keyword.name}`}
                disabled={!target || promote.isPending}
                onClick={() => promote.mutate(keyword.id!)}
              >
                <ArrowUpRight /> Promote
              </Button>
            </li>
          ))}
        </ul>
      ) : (
        <EmptyState icon={Tag} title="No keywords in use" className="border-t py-8" />
      )}
      {promote.isError && <Alert className="m-5">{problemMessage(promote.error)}</Alert>}
    </SettingsSection>
  );
}
