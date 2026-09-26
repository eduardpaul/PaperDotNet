import type { InboxResponse } from '@paperdotnet/client';
import { useInfiniteQuery, useQuery } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { Inbox as InboxIcon, Upload, Users } from 'lucide-react';
import { inboxesQuery } from '@/api/queries';
import { Page, PageHeader } from '@/components/page';
import { Card } from '@/components/ui/card';
import { EmptyState, Skeleton, Spinner } from '@/components/ui/feedback';
import { Button } from '@/components/ui/button';
import { DocumentGrid } from '@/features/documents/document-grid';
import { DropZone, FilePickerButton } from '@/features/documents/drop-zone';
import { acceptedTypes } from '@/features/documents/paths';
import { useUploads, type UploadTarget } from '@/features/documents/uploads';
import { ItemPanel } from '@/features/lists/item-panel';
import { itemsQuery, listQuery } from '@/features/lists/queries';
import { cn } from '@/lib/utils';

interface InboxSearch {
  /** The inbox library; the personal inbox when omitted. */
  inbox?: string;
  item?: string;
  tab?: string;
}

const text = (value: unknown) => (typeof value === 'string' && value ? value : undefined);

export const Route = createFileRoute('/_app/inbox')({
  validateSearch: (search: Record<string, unknown>): InboxSearch => ({
    inbox: text(search.inbox),
    item: text(search.item),
    tab: text(search.tab),
  }),
  component: Inbox,
});

function targetOf(inbox: InboxResponse): UploadTarget {
  return inbox.kind === 'group' && inbox.groupId
    ? { kind: 'group', groupId: inbox.groupId, name: `${inbox.groupName} inbox` }
    : { kind: 'inbox', name: 'your Inbox' };
}

/** The triage queue (LST-07, DOC-16): new documents land here to be checked, classified and filed. */
function Inbox() {
  const search = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const { upload } = useUploads();
  const { data: inboxes, isPending } = useQuery(inboxesQuery);
  const inbox =
    inboxes?.find((i) => i.listId === search.inbox) ?? inboxes?.find((i) => i.kind !== 'group') ?? inboxes?.[0];
  const setSearch = (patch: Partial<InboxSearch>, replace = false) =>
    void navigate({ search: (c) => ({ ...c, ...patch }), replace });

  return (
    <Page wide className="max-w-[1600px]">
      <PageHeader
        icon={InboxIcon}
        title="Inbox"
        description="New documents land here. Check them, add details and move them where they belong."
        actions={
          inbox && (
            <FilePickerButton
              variant="primary"
              accept={acceptedTypes}
              onFiles={(files) => upload(files, targetOf(inbox))}
            >
              <Upload /> Upload
            </FilePickerButton>
          )
        }
      />
      {(inboxes?.length ?? 0) > 1 && (
        <div role="tablist" aria-label="Inboxes" className="mb-4 inline-flex rounded-md bg-surface-muted p-0.5">
          {inboxes!.map((i) => (
            <button
              key={i.listId}
              role="tab"
              type="button"
              aria-selected={i.listId === inbox?.listId}
              onClick={() => setSearch({ inbox: i.kind === 'group' ? i.listId! : undefined, item: undefined })}
              className={cn(
                'inline-flex items-center gap-1.5 rounded px-2.5 py-1 text-xs font-medium text-muted',
                i.listId === inbox?.listId && 'bg-surface text-foreground shadow-xs',
              )}
            >
              {i.kind === 'group' && <Users className="size-3.5" />}
              {i.kind === 'group' ? i.groupName : 'Mine'}
            </button>
          ))}
        </div>
      )}
      {isPending ? (
        <Skeleton className="h-64" />
      ) : inbox ? (
        <InboxDocuments
          inbox={inbox}
          search={search}
          setSearch={setSearch}
          onFiles={(files) => upload(files, targetOf(inbox))}
        />
      ) : (
        <Card>
          <EmptyState icon={InboxIcon} title="No inbox" />
        </Card>
      )}
    </Page>
  );
}

function InboxDocuments({
  inbox,
  search,
  setSearch,
  onFiles,
}: {
  inbox: InboxResponse;
  search: InboxSearch;
  setSearch: (patch: Partial<InboxSearch>, replace?: boolean) => void;
  onFiles: (files: File[]) => void;
}) {
  const workspaceId = inbox.workspaceId!;
  const listId = inbox.listId!;
  const { data: list } = useQuery(listQuery(workspaceId, listId));
  const items = useInfiniteQuery(
    itemsQuery(workspaceId, listId, { filter: 'isFolder eq false', orderby: 'createdAt desc' }),
  );
  const rows = items.data?.pages.flatMap((p) => p.value ?? []) ?? [];

  return (
    <DropZone
      onFiles={onFiles}
      label={`Drop to upload to ${inbox.kind === 'group' ? `the ${inbox.groupName} inbox` : 'your Inbox'}`}
      className="min-h-64"
    >
      {items.isPending ? (
        <Skeleton className="h-64" />
      ) : rows.length ? (
        <>
          <DocumentGrid
            workspaceId={workspaceId}
            listId={listId}
            items={rows}
            onOpen={(item) => setSearch({ item: item.id!, tab: 'preview' })}
          />
          {items.hasNextPage && (
            <Button
              size="sm"
              className="mt-3"
              disabled={items.isFetchingNextPage}
              onClick={() => void items.fetchNextPage()}
            >
              {items.isFetchingNextPage && <Spinner />} Load more
            </Button>
          )}
        </>
      ) : (
        <Card className="border-dashed">
          <EmptyState icon={Upload} title="Your inbox is empty" className="py-16">
            Drop scans and PDFs here, or use Upload. They are made searchable automatically.
          </EmptyState>
        </Card>
      )}
      {list && search.item && (
        <ItemPanel
          workspaceId={workspaceId}
          list={list}
          itemId={search.item}
          tab={search.tab ?? 'preview'}
          onTab={(tab) => setSearch({ tab }, true)}
          onClose={() => setSearch({ item: undefined, tab: undefined })}
          onCreated={(id) => setSearch({ item: id }, true)}
        />
      )}
    </DropZone>
  );
}
