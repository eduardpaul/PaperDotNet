import {
  contentClient,
  type ContentClient,
  type ContentCollection,
  type ContentMigration,
  type ContentResource,
  type ContentSnapshot,
  type ContentValues,
} from '@paperdotnet/client';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useNavigate } from '@tanstack/react-router';
import { Archive, FileText, Folder, FolderOpen, Plus, Settings, Upload } from 'lucide-react';
import { useState } from 'react';
import { toast } from 'sonner';
import { client } from '@/api/client';
import { workspacesQuery } from '@/api/queries';
import { keys } from '@/api/keys';
import { Page, PageHeader } from '@/components/page';
import { Button } from '@/components/ui/button';
import { Card } from '@/components/ui/card';
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from '@/components/ui/dialog';
import { Alert, EmptyState, Spinner } from '@/components/ui/feedback';
import { Input, Label } from '@/components/ui/input';
import { Checkbox, Select } from '@/components/ui/select';
import { problemMessage } from '@/lib/errors';
import { useFormat } from '@/lib/preferences';
import { Route, type ContentSearch } from '@/routes/_app/content';
import { ContentEditor } from './editor';
import { CollectionSettings } from './settings';

export const contentApi = contentClient(client);
export type RunContentAction = (action: () => Promise<unknown>, done?: () => void) => Promise<void>;

export function ContentScreen() {
  const search = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const cache = useQueryClient();
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const [dialog, setDialog] = useState<
    'workspace' | 'collection' | 'item' | 'folder' | 'settings' | 'migration' | undefined
  >();
  const workspaces = useQuery({ queryKey: keys.contentWorkspaces, queryFn: () => contentApi.workspaces() });
  const workspace = search.workspace ?? workspaces.data?.[0]?.id;
  const collections = useQuery({
    queryKey: keys.contentCollections(workspace),
    queryFn: () => contentApi.collections(workspace!),
    enabled: !!workspace,
  });
  const collectionId = search.collection ?? collections.data?.[0]?.id;
  const collection = useQuery({
    queryKey: keys.contentCollection(collectionId),
    queryFn: () => contentApi.collection(collectionId!),
    enabled: !!collectionId && collectionId !== 'all',
  });
  const surface = search.surface ?? 'published';
  const options = {
    surface,
    archive: search.archive ?? 'active',
    search: search.q,
    parentId: search.folder ?? collectionId,
    limit: 100,
  };
  const [continuation, setContinuation] = useState<string>();
  const items = useQuery({
    queryKey: [...keys.contentItems(collectionId), options, continuation],
    queryFn: () =>
      collectionId === 'all'
        ? contentApi.globalItems({
            surface,
            archive: options.archive,
            search: options.search,
            limit: 100,
            afterId: continuation,
          })
        : contentApi.items(collectionId!, { ...options, afterId: continuation }),
    enabled: !!collectionId,
  });
  const selected = useQuery({
    queryKey: [...keys.contentItem(search.item), surface],
    queryFn: () => contentApi.item(search.item!, surface),
    enabled: !!search.item,
  });
  const setSearch = (patch: Partial<ContentSearch>) => {
    setContinuation(undefined);
    void navigate({ search: (c) => ({ ...c, ...patch }) });
  };
  const run: RunContentAction = async (action, done) => {
    setBusy(true);
    setError('');
    try {
      await action();
      await cache.invalidateQueries({ queryKey: keys.content });
      done?.();
    } catch (e) {
      setError(problemMessage(e));
      toast.error(problemMessage(e));
      await cache.invalidateQueries({ queryKey: keys.content });
    } finally {
      setBusy(false);
    }
  };
  const failure =
    error ||
    (workspaces.error || collections.error || collection.error || items.error || selected.error
      ? problemMessage(workspaces.error ?? collections.error ?? collection.error ?? items.error ?? selected.error)
      : '');
  const current: ContentCollection | undefined =
    collectionId === 'all'
      ? {
          resource: {
            id: 'all',
            workspaceId: '',
            name: 'All collections',
            kind: 'list',
            version: 1,
            publishingEnabled: false,
            versioning: 'off',
            maxVersions: 50,
          },
          canReadDraft: true,
          canWrite: false,
          canManage: false,
          canPublish: false,
        }
      : collection.data;
  return (
    <Page wide className="max-w-[1600px]">
      <PageHeader
        title="Content"
        icon={FolderOpen}
        description="Lists and documents with drafts, publishing, schema revisions and archive."
        actions={
          <>
            <Button onClick={() => setDialog('migration')}>Import workspace</Button>
            <Button onClick={() => setDialog('workspace')}>
              <Plus />
              Workspace
            </Button>
          </>
        }
      />
      {failure && (
        <Alert className="mb-4">
          {failure}
          <Button
            variant="link"
            onClick={() => {
              setError('');
              void cache.invalidateQueries({ queryKey: keys.content });
            }}
          >
            Reload
          </Button>
        </Alert>
      )}
      <div className="mb-4 grid gap-3 sm:grid-cols-2 lg:grid-cols-4">
        <div>
          <Label htmlFor="content-workspace">Workspace</Label>
          <Select
            id="content-workspace"
            value={workspace ?? ''}
            onChange={(e) =>
              setSearch({ workspace: e.target.value, collection: undefined, folder: undefined, item: undefined })
            }
          >
            {!workspaces.data?.length && <option value="">Create a workspace to begin</option>}
            {workspaces.data?.map((w) => (
              <option key={w.id} value={w.id}>
                {w.name}
              </option>
            ))}
          </Select>
        </div>
        <div>
          <Label htmlFor="content-collection">Collection</Label>
          <div className="flex gap-2">
            <Select
              className="min-w-0 flex-1"
              id="content-collection"
              value={collectionId ?? ''}
              onChange={(e) => setSearch({ collection: e.target.value, folder: undefined, item: undefined })}
            >
              {!collections.data?.length && <option value="">Create a list or library</option>}
              <option value="all">All collections</option>
              {collections.data?.map((c) => (
                <option key={c.id} value={c.id}>
                  {c.name}
                </option>
              ))}
            </Select>
            <Button aria-label="New collection" disabled={!workspace} onClick={() => setDialog('collection')}>
              <Plus />
            </Button>
          </div>
        </div>
        <div>
          <Label htmlFor="content-surface">Content surface</Label>
          <Select
            id="content-surface"
            value={surface}
            onChange={(e) => setSearch({ surface: e.target.value as 'head' | 'published', item: undefined })}
          >
            <option value="published">Published</option>
            {(!current?.resource.publishingEnabled || current.canReadDraft) && (
              <option value="head">Drafts / latest</option>
            )}
          </Select>
        </div>
        <div>
          <Label htmlFor="content-archive">Archive</Label>
          <Select
            id="content-archive"
            value={search.archive ?? 'active'}
            onChange={(e) => setSearch({ archive: e.target.value as ContentSearch['archive'], item: undefined })}
          >
            <option value="active">Active</option>
            <option value="archived">Archived</option>
            <option value="all">All</option>
          </Select>
        </div>
      </div>
      {current && (
        <>
          <div className="mb-4 flex flex-wrap items-center gap-2">
            <h2 className="mr-auto text-lg font-semibold">{current.resource.name}</h2>
            <Input
              className="max-w-xs"
              aria-label="Search content"
              value={search.q ?? ''}
              onChange={(e) => setSearch({ q: e.target.value || undefined, item: undefined })}
              placeholder="Search names and values"
            />
            {current.canManage && (
              <Button onClick={() => setDialog('settings')}>
                <Settings />
                Settings
              </Button>
            )}
            {current.canWrite && (
              <>
                <Button onClick={() => setDialog('folder')}>
                  <Folder />
                  New folder
                </Button>
                <Button variant="primary" onClick={() => setDialog('item')}>
                  <Plus />
                  New item
                </Button>
              </>
            )}
          </div>
          {search.folder && (
            <Button className="mb-3" onClick={() => setSearch({ folder: undefined, item: undefined })}>
              Back to collection
            </Button>
          )}
          <div className="grid items-start gap-4 xl:grid-cols-[minmax(0,1fr)_minmax(340px,440px)]">
            <Card className="overflow-hidden">
              {items.isPending ? (
                <div className="p-8">
                  <Spinner />
                </div>
              ) : !items.data?.length ? (
                <EmptyState icon={surface === 'head' ? FileText : Archive} title="No items in this view">
                  {current.resource.publishingEnabled && surface === 'published'
                    ? 'Publish an item from Drafts / latest to make it visible here.'
                    : 'Create an item or choose another archive filter.'}
                </EmptyState>
              ) : (
                <ul className="divide-y">
                  {items.data.map((item) => (
                    <li key={item.resource.id} className="flex items-center gap-3 p-3 hover:bg-surface-muted">
                      {item.resource.kind === 'folder' ? (
                        <Folder className="size-5 shrink-0 text-muted" />
                      ) : (
                        <FileText className="size-5 shrink-0 text-muted" />
                      )}
                      <button
                        className="min-w-0 flex-1 text-left"
                        onClick={() =>
                          setSearch(
                            collectionId === 'all'
                              ? {
                                  workspace: item.resource.workspaceId,
                                  collection: item.resource.containerId,
                                  folder: item.resource.kind === 'folder' ? item.resource.id : undefined,
                                  item: item.resource.kind === 'folder' ? undefined : item.resource.id,
                                }
                              : item.resource.kind === 'folder'
                                ? { folder: item.resource.id, item: undefined }
                                : { item: item.resource.id },
                          )
                        }
                      >
                        <span className="block truncate font-medium">{item.resource.name}</span>
                        <span className="text-xs text-muted">
                          Revision {item.revisionNumber}
                          {item.isDraft ? ' · Draft' : ''}
                          {item.resource.archivedAt ? ' · Archived' : ''}
                          {item.filename ? ` · ${item.filename}` : ''}
                        </span>
                      </button>
                      {item.resource.kind === 'folder' && (
                        <Button size="sm" onClick={() => setSearch({ item: item.resource.id })}>
                          Details
                        </Button>
                      )}
                    </li>
                  ))}
                </ul>
              )}
              <div className="flex justify-end gap-2 border-t p-2">
                <Button size="sm" disabled={!continuation} onClick={() => setContinuation(undefined)}>
                  First page
                </Button>
                <Button
                  size="sm"
                  disabled={(items.data?.length ?? 0) < 100}
                  onClick={() => setContinuation(items.data?.at(-1)?.resource.id)}
                >
                  Next page
                </Button>
              </div>
            </Card>
            {selected.data ? (
              <ItemDetails
                key={`${selected.data.revisionId}-${selected.data.resource.version}`}
                item={selected.data}
                collection={current}
                surface={surface}
                api={contentApi}
                busy={busy}
                run={run}
              />
            ) : (
              <Card className="p-6 text-sm text-muted">
                Select an item to view its saved schema, details and history.
              </Card>
            )}
          </div>
        </>
      )}
      {!current && !collection.isPending && (
        <EmptyState icon={FolderOpen} title="Create your first collection">
          Choose a workspace, then add a list or document library.
        </EmptyState>
      )}
      <Dialog
        open={dialog === 'workspace' || dialog === 'collection'}
        onOpenChange={(open) => !open && setDialog(undefined)}
      >
        <DialogContent>
          <DialogHeader>
            <DialogTitle>{dialog === 'workspace' ? 'Create workspace' : 'Create collection'}</DialogTitle>
            <DialogDescription>Organize your lists and document libraries.</DialogDescription>
          </DialogHeader>
          <CreateContainer
            key={dialog}
            collection={dialog === 'collection'}
            busy={busy}
            onSave={(name, library, publishing) =>
              run(
                async () => {
                  if (dialog === 'workspace') {
                    const row = await contentApi.createWorkspace(name);
                    setSearch({ workspace: row.id, collection: undefined, folder: undefined, item: undefined });
                  } else {
                    const row = await contentApi.createCollection(workspace!, name, library, publishing);
                    setSearch({
                      collection: row.id,
                      folder: undefined,
                      item: undefined,
                      surface: publishing ? 'head' : 'published',
                    });
                  }
                },
                () => setDialog(undefined),
              )
            }
          />
        </DialogContent>
      </Dialog>
      <Dialog open={dialog === 'item' || dialog === 'folder'} onOpenChange={(open) => !open && setDialog(undefined)}>
        <DialogContent className="max-h-[78vh] overflow-y-auto">
          <DialogHeader>
            <DialogTitle>{dialog === 'folder' ? 'New folder' : 'New item'}</DialogTitle>
            <DialogDescription>
              {current?.resource.publishingEnabled
                ? 'Save a draft, then publish when it is ready.'
                : 'Your changes will be immediately visible.'}
            </DialogDescription>
          </DialogHeader>
          {current?.schema && (
            <ContentEditor
              key={`${dialog}-${collectionId}`}
              schema={current.schema}
              folder={dialog === 'folder'}
              busy={busy}
              onSave={(body) =>
                run(
                  async () => {
                    const item = await contentApi.create(search.folder ?? collectionId!, body, dialog === 'folder');
                    setSearch({
                      item: item.resource.id,
                      surface: current.resource.publishingEnabled ? 'head' : 'published',
                    });
                  },
                  () => setDialog(undefined),
                )
              }
            />
          )}
        </DialogContent>
      </Dialog>
      {current && (
        <CollectionSettings
          open={dialog === 'settings'}
          onClose={() => setDialog(undefined)}
          collection={current}
          api={contentApi}
          run={run}
          busy={busy}
        />
      )}
      <MigrationDialog
        open={dialog === 'migration'}
        close={() => setDialog(undefined)}
        api={contentApi}
        run={run}
        busy={busy}
        imported={(row) => {
          void cache.invalidateQueries({ queryKey: keys.workspaces });
          setSearch({ workspace: row.id, collection: undefined, folder: undefined, item: undefined });
        }}
      />
    </Page>
  );
}

function CreateContainer({
  collection,
  busy,
  onSave,
}: {
  collection: boolean;
  busy: boolean;
  onSave: (name: string, library: boolean, publishing: boolean) => Promise<void>;
}) {
  const [name, setName] = useState('');
  const [library, setLibrary] = useState(false);
  const [publishing, setPublishing] = useState(false);
  return (
    <form
      onSubmit={(e) => {
        e.preventDefault();
        void onSave(name, library, publishing);
      }}
    >
      <div className="space-y-4 px-5 pb-5">
        <div>
          <Label htmlFor="content-new-name">Name</Label>
          <Input id="content-new-name" required value={name} onChange={(e) => setName(e.target.value)} />
        </div>
        {collection && (
          <>
            <Label className="flex items-center gap-2">
              <Checkbox checked={library} onChange={(e) => setLibrary(e.target.checked)} />
              Document library
            </Label>
            <Label className="flex items-center gap-2">
              <Checkbox checked={publishing} onChange={(e) => setPublishing(e.target.checked)} />
              Require publishing
            </Label>
          </>
        )}
      </div>
      <DialogFooter>
        <Button type="submit" variant="primary" disabled={busy}>
          {busy && <Spinner />}Create
        </Button>
      </DialogFooter>
    </form>
  );
}

function ItemDetails({
  item,
  collection,
  surface,
  api,
  busy,
  run,
}: {
  item: ContentSnapshot;
  collection: ContentCollection;
  surface: string;
  api: ContentClient;
  busy: boolean;
  run: RunContentAction;
}) {
  const format = useFormat();
  const [edit, setEdit] = useState(false);
  const [showHistory, setHistory] = useState(false);
  const history = useQuery({
    queryKey: [...keys.contentItem(item.resource.id), 'history'],
    queryFn: () => api.history(item.resource.id),
    enabled: showHistory && item.resource.kind === 'item',
  });
  async function download() {
    const blob = await api.download(item.resource.id, surface);
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url;
    a.download = item.filename || item.resource.name;
    a.click();
    URL.revokeObjectURL(url);
  }
  return (
    <Card className="space-y-4 p-5">
      <div>
        <h3 className="font-semibold">{item.resource.name}</h3>
        <p className="mt-1 text-xs text-muted">
          Revision {item.revisionNumber} · {item.isDraft ? 'Draft' : 'Published'}
          {item.resource.archivedAt ? ` · Archived ${format.date(item.resource.archivedAt)}` : ''}
        </p>
      </div>
      <dl className="space-y-2 text-sm">
        {item.schema.fields.map((field) => (
          <div key={field.id}>
            <dt className="text-muted">{field.label}</dt>
            <dd className="break-words">{display(item.values[field.id])}</dd>
          </div>
        ))}
      </dl>
      <details className="text-xs text-muted">
        <summary className="cursor-pointer">Saved schema</summary>
        <p className="mt-2 break-all">{item.schemaRevisionId}</p>
        {item.schema.fields.map((f) => (
          <p key={f.id}>
            {f.label}: {f.type}
            {f.required ? ' (required)' : ''}
          </p>
        ))}
      </details>
      <div className="flex flex-wrap gap-2">
        {item.hasFile && (
          <Button disabled={busy} onClick={() => void run(download)}>
            Download {item.filename}
          </Button>
        )}
        {collection.canWrite && (
          <>
            <Button onClick={() => setEdit(true)}>Edit details</Button>
            <Button
              disabled={busy}
              onClick={() => void run(() => api.archive(item.resource, !item.resource.archivedAt))}
            >
              {item.resource.archivedAt ? 'Unarchive' : 'Archive'}
            </Button>
          </>
        )}
        {collection.resource.publishingEnabled && collection.canPublish && item.resource.kind === 'item' && (
          <>
            <Button
              disabled={busy || !item.isDraft || surface !== 'head'}
              onClick={() => void run(() => api.publish(item.resource))}
            >
              Publish
            </Button>
            <Button disabled={busy} onClick={() => void run(() => api.unpublish(item.resource))}>
              Unpublish
            </Button>
          </>
        )}
        {item.resource.kind === 'item' && <Button onClick={() => setHistory(!showHistory)}>History</Button>}
      </div>
      {collection.canWrite && collection.resource.kind === 'library' && item.resource.kind === 'item' && (
        <div>
          <Label className="flex items-center gap-2" htmlFor="content-file">
            <Upload className="size-4" />
            Upload or replace file
          </Label>
          <Input
            id="content-file"
            type="file"
            accept="application/pdf,image/tiff,image/jpeg,image/png,image/webp"
            disabled={busy}
            onChange={(e) => {
              const file = e.target.files?.[0];
              if (file) void run(() => api.upload(item.resource, file));
            }}
          />
        </div>
      )}
      {showHistory && (
        <section aria-label="Content history">
          <h4 className="mb-2 text-sm font-medium">History</h4>
          {history.error && <Alert>{problemMessage(history.error)}</Alert>}
          {history.isPending ? (
            <Spinner />
          ) : !history.data?.length ? (
            <p className="text-xs text-muted">
              No retained history. Enable history in collection settings to retain future edits.
            </p>
          ) : (
            <ul className="space-y-3">
              {history.data.map((h) => (
                <li key={h.number} className="rounded border p-3 text-xs">
                  <p className="font-medium">
                    Version {h.number}: {h.revision.name}
                  </p>
                  <p className="text-muted">
                    {format.date(h.revision.createdAt)} · {h.changedFields.join(', ')}
                  </p>
                  <p className="mt-1">
                    {Object.entries(JSON.parse(h.revision.payload) as Record<string, unknown>)
                      .map(([key, value]) => `${key}: ${display(value)}`)
                      .join(' · ')}
                  </p>
                  {collection.canWrite && (
                    <Button
                      size="sm"
                      className="mt-2"
                      disabled={busy}
                      onClick={() => void run(() => api.restore(item.resource, h.number))}
                    >
                      Restore version {h.number}
                    </Button>
                  )}
                </li>
              ))}
            </ul>
          )}
        </section>
      )}
      <Dialog open={edit} onOpenChange={setEdit}>
        <DialogContent className="max-h-[78vh] overflow-y-auto">
          <DialogHeader>
            <DialogTitle>Edit details</DialogTitle>
            <DialogDescription>Saving creates a new immutable content revision.</DialogDescription>
          </DialogHeader>
          {collection.schema && (
            <ContentEditor
              schema={collection.schema}
              item={item}
              folder={item.resource.kind === 'folder'}
              busy={busy}
              onSave={(body: ContentValues) =>
                run(
                  () => api.edit(item.resource, body),
                  () => setEdit(false),
                )
              }
            />
          )}
        </DialogContent>
      </Dialog>
    </Card>
  );
}
function display(value: unknown): string {
  return value === null || value === undefined || value === ''
    ? '—'
    : Array.isArray(value)
      ? value.map(display).join(', ')
      : typeof value === 'boolean'
        ? value
          ? 'Yes'
          : 'No'
        : String(value);
}

function MigrationDialog({
  open,
  close,
  api: content,
  run,
  busy,
  imported,
}: {
  open: boolean;
  close: () => void;
  api: ContentClient;
  run: RunContentAction;
  busy: boolean;
  imported: (workspace: ContentResource) => void;
}) {
  const workspaces = useQuery({ ...workspacesQuery, enabled: open });
  const [source, setSource] = useState('');
  const [preview, setPreview] = useState<ContentMigration>();
  return (
    <Dialog open={open} onOpenChange={(v) => !v && close()}>
      <DialogContent>
        <DialogHeader>
          <DialogTitle>Import existing workspace</DialogTitle>
          <DialogDescription>
            Review the copy before switching content storage. After import, use Content to read and edit this workspace.
          </DialogDescription>
        </DialogHeader>
        <div className="space-y-4 px-5 pb-5">
          <div>
            <Label htmlFor="content-import-source">Source workspace</Label>
            <Select
              id="content-import-source"
              value={source}
              onChange={(e) => {
                setSource(e.target.value);
                setPreview(undefined);
              }}
            >
              <option value="">Choose a workspace</option>
              {workspaces.data?.map((w) => (
                <option key={w.id} value={w.id!}>
                  {w.name}
                </option>
              ))}
            </Select>
          </div>
          <Button
            disabled={!source || busy}
            onClick={() => void run(async () => setPreview(await content.preview(source)))}
          >
            Preview import
          </Button>
          {preview && (
            <div className="space-y-2 text-sm">
              <p>
                {preview.collections} collections · {preview.items} items · {preview.historyEntries} history entries ·{' '}
                {preview.files} files
              </p>
              <p className="text-xs text-muted">
                Older metadata uses the available schema because the original store did not retain historical schemas.
                Comments, workflow runs, tasks and calendars remain in their original storage. Their existing screens
                are not connected to imported content.
              </p>
              {preview.alreadyImported && <Alert tone="warning">This workspace has already been imported.</Alert>}
              {preview.blockers.map((b) => (
                <Alert key={b}>{b}</Alert>
              ))}
            </div>
          )}
        </div>
        <DialogFooter>
          <Button onClick={close}>Cancel</Button>
          <Button
            variant="primary"
            disabled={!preview || preview.alreadyImported || preview.blockers.length > 0 || busy}
            onClick={() => preview && void run(async () => imported(await content.migrate(preview)), close)}
          >
            Import workspace
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}
