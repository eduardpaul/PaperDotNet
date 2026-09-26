import type { FileVersionResponse } from '@paperdotnet/client';
import { downloadFile, uploadBody } from '@paperdotnet/client';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useNavigate, useSearch } from '@tanstack/react-router';

import {
  ArrowLeft,
  ArrowRight,
  Download,
  FileStack,
  RefreshCw,
  RotateCcw,
  RotateCw,
  ScanText,
  Scissors,
  Trash2,
  Upload,
  X,
} from 'lucide-react';
import { useState } from 'react';
import { toast } from 'sonner';
import { client } from '@/api/client';
import { keys } from '@/api/keys';
import { Badge } from '@/components/ui/badge';
import { Button } from '@/components/ui/button';
import { Alert, EmptyState, Skeleton, Spinner } from '@/components/ui/feedback';
import { Dialog, DialogContent, DialogTitle } from '@/components/ui/dialog';
import { Input, Label } from '@/components/ui/input';
import { Popover, PopoverContent, PopoverTrigger } from '@/components/ui/popover';
import { Checkbox } from '@/components/ui/select';
import type { ItemPanelContext } from '@/extensibility/item-panels';
import { userName, useUsers } from '@/features/fields/directory';
import { listBuilder } from '@/features/lists/queries';
import { useAuthedImage } from '@/lib/authed-image';
import { problemMessage } from '@/lib/errors';
import { useFormat } from '@/lib/preferences';
import { cn } from '@/lib/utils';
import { FilePickerButton } from './drop-zone';
import { acceptedTypes, filePath, pageImagePath } from './paths';
import { fileVersionsQuery } from './queries';

interface PageState {
  /** The page number in the current file. */
  page: number;
  rotate: number;
}

/** The file of a document: status, downloads, pages with tools (DOC-05/06) and its versions (DOC-03). */
export function PreviewTab({ workspaceId, list, item }: ItemPanelContext) {
  const { data: versions, isPending } = useQuery(fileVersionsQuery(workspaceId, list.id!, item.id!));
  const current = versions?.find((v) => v.isCurrent) ?? versions?.[0];

  if (isPending)
    return (
      <div className="space-y-3 p-5">
        <Skeleton className="h-16" />
        <Skeleton className="h-48" />
      </div>
    );
  if (!current) return <EmptyState icon={FileStack} title="This item has no file" />;

  return (
    <div className="flex flex-col gap-5 p-5">
      <FileHeader workspaceId={workspaceId} listId={list.id!} itemId={item.id!} file={current} />
      <Pages
        key={`${current.number}-${current.pageCount}`}
        workspaceId={workspaceId}
        listId={list.id!}
        itemId={item.id!}
        file={current}
      />
      <FileHistory workspaceId={workspaceId} listId={list.id!} itemId={item.id!} versions={versions ?? []} />
    </div>
  );
}

function StatusBadge({ file }: { file: FileVersionResponse }) {
  switch (file.processingStatus) {
    case 'scheduled':
    case 'running':
      return (
        <Badge tone="accent">
          <Spinner className="size-3 text-current" />{' '}
          {file.processingStatus === 'running' ? 'Processing…' : 'Waiting to process'}
        </Badge>
      );
    case 'failed':
      return <Badge tone="danger">Processing failed</Badge>;
    case 'succeeded':
      return (
        <Badge tone="success">
          <ScanText className="size-3" /> Searchable
        </Badge>
      );
    default:
      return <Badge>Not processed</Badge>;
  }
}

function FileHeader({
  workspaceId,
  listId,
  itemId,
  file,
}: {
  workspaceId: string;
  listId: string;
  itemId: string;
  file: FileVersionResponse;
}) {
  const format = useFormat();
  const queryClient = useQueryClient();
  const refresh = () => queryClient.invalidateQueries({ queryKey: keys.item(workspaceId, listId, itemId) });
  const download = useMutation({
    mutationFn: async () => {
      const { blob, fileName } = await downloadFile(client, filePath(workspaceId, listId, itemId));
      const url = URL.createObjectURL(blob);
      const link = Object.assign(document.createElement('a'), {
        href: url,
        download: fileName ?? file.fileName ?? 'document',
      });
      link.click();
      setTimeout(() => URL.revokeObjectURL(url), 10_000);
    },
  });
  const replace = useMutation({
    mutationFn: async (upload: File) =>
      listBuilder(workspaceId, listId)
        .items.byItemId(itemId)
        .file.put(await uploadBody({ file: upload })),
    onSuccess: async () => {
      toast.success('New file version uploaded.');
      await refresh();
    },
  });
  const [languages, setLanguages] = useState(file.languages ?? '');
  const [ocrOpen, setOcrOpen] = useState(false);
  const reprocess = useMutation({
    mutationFn: () =>
      listBuilder(workspaceId, listId)
        .items.byItemId(itemId)
        .file.process.post({ forceOcr: true, languages: languages.trim() || undefined }),
    onSuccess: async () => {
      setOcrOpen(false);
      toast.success('Processing started. The text will be searchable when it is done.');
      await refresh();
    },
  });

  return (
    <section className="flex flex-col gap-3">
      <div className="flex flex-wrap items-center gap-x-3 gap-y-1">
        <h3 className="min-w-0 flex-1 truncate text-[13px] font-semibold">{file.fileName}</h3>
        <StatusBadge file={file} />
      </div>
      <p className="text-xs text-muted">
        {format.fileSize(file.size)} · {file.pageCount ?? '?'} {file.pageCount === 1 ? 'page' : 'pages'} ·{' '}
        {file.mediaType}
        {file.languages && <> · OCR {file.languages}</>}
        {file.textLanguage && <> · text in {file.textLanguage}</>}
      </p>
      {file.processingStatus === 'failed' && file.processingError && <Alert>{file.processingError}</Alert>}
      <div className="flex flex-wrap gap-2">
        <Button size="sm" disabled={download.isPending} onClick={() => download.mutate()}>
          {download.isPending ? <Spinner /> : <Download />} Download
        </Button>
        <FilePickerButton
          size="sm"
          accept={acceptedTypes}
          multiple={false}
          disabled={replace.isPending}
          onFiles={([f]) => replace.mutate(f!)}
        >
          {replace.isPending ? <Spinner /> : <Upload />} Replace file
        </FilePickerButton>
        <Popover open={ocrOpen} onOpenChange={setOcrOpen}>
          <PopoverTrigger asChild>
            <Button size="sm" disabled={file.processingStatus === 'running' || file.processingStatus === 'scheduled'}>
              <RefreshCw /> Run OCR again
            </Button>
          </PopoverTrigger>
          <PopoverContent align="start" className="w-72 p-4">
            <form
              className="flex flex-col gap-3"
              onSubmit={(e) => {
                e.preventDefault();
                reprocess.mutate();
              }}
            >
              <div className="flex flex-col gap-1.5">
                <Label htmlFor="ocr-languages">Languages of this file</Label>
                <Input
                  id="ocr-languages"
                  placeholder="e.g. deu+eng"
                  value={languages}
                  onChange={(e) => setLanguages(e.target.value)}
                />
                <p className="text-xs text-muted">
                  Tesseract codes joined with +; kept for this file (DOC-17). Empty: the library’s.
                </p>
              </div>
              {reprocess.isError && <Alert>{problemMessage(reprocess.error)}</Alert>}
              <Button type="submit" size="sm" variant="primary" disabled={reprocess.isPending}>
                {reprocess.isPending && <Spinner className="text-current" />} Run OCR
              </Button>
            </form>
          </PopoverContent>
        </Popover>
      </div>
    </section>
  );
}

function PageImage({
  workspaceId,
  listId,
  itemId,
  page,
  version,
  rotate,
}: {
  workspaceId: string;
  listId: string;
  itemId: string;
  page: number;
  version: number;
  rotate: number;
}) {
  const { url, failed } = useAuthedImage(pageImagePath(workspaceId, listId, itemId, page), version);
  return (
    <span className="flex aspect-[1/1.3] items-center justify-center overflow-hidden rounded border bg-surface-muted">
      {url ? (
        <img
          src={url}
          alt={`Page ${page}`}
          className="max-h-full max-w-full transition-transform"
          style={{ transform: `rotate(${rotate}deg)` }}
        />
      ) : failed ? (
        <span className="p-2 text-center text-xs text-muted">No preview yet</span>
      ) : (
        <Spinner />
      )}
    </span>
  );
}

/** Page thumbnails; selecting pages enables rotate, delete, reorder and extract. Edits are saved as one new version. */
function Pages({
  workspaceId,
  listId,
  itemId,
  file,
}: {
  workspaceId: string;
  listId: string;
  itemId: string;
  file: FileVersionResponse;
}) {
  const queryClient = useQueryClient();
  const navigate = useNavigate();
  const original = Array.from({ length: file.pageCount ?? 0 }, (_, i) => ({ page: i + 1, rotate: 0 }));
  const [pages, setPages] = useState<PageState[]>(original);
  const [selected, setSelected] = useState<Set<number>>(new Set());
  // A search hit on a page opens it large (SRC-09).
  const { page: hitPage } = useSearch({ strict: false }) as { page?: number };
  const [large, setLarge] = useState<number | undefined>(
    hitPage && hitPage <= (file.pageCount ?? 0) ? hitPage : undefined,
  );
  const changed = JSON.stringify(pages) !== JSON.stringify(original);
  const editable = file.mediaType === 'application/pdf';
  const refresh = () => queryClient.invalidateQueries({ queryKey: keys.item(workspaceId, listId, itemId) });

  const save = useMutation({
    mutationFn: () =>
      listBuilder(workspaceId, listId)
        .items.byItemId(itemId)
        .file.pages.put({ pages: pages.map((p) => ({ page: p.page, rotate: p.rotate || undefined })) }),
    onSuccess: async () => {
      toast.success('Pages saved as a new file version.');
      await refresh();
    },
  });
  const extract = useMutation({
    mutationFn: (remove: boolean) =>
      listBuilder(workspaceId, listId)
        .items.byItemId(itemId)
        .file.pages.extract.post({ pages: [...selected].sort((a, b) => a - b), remove }),
    onSuccess: async (result, remove) => {
      const created = result?.documents?.[0];
      toast.success(
        `${selected.size} ${selected.size === 1 ? 'page' : 'pages'} ${remove ? 'moved' : 'copied'} into a new document.`,
        {
          action: created
            ? {
                label: 'Open',
                onClick: () =>
                  void navigate({
                    to: '/w/$workspaceId/l/$listId',
                    params: { workspaceId: created.workspaceId!, listId: created.listId! },
                    search: { item: created.itemId!, tab: 'preview' },
                  }),
              }
            : undefined,
        },
      );
      setSelected(new Set());
      await Promise.all([refresh(), queryClient.invalidateQueries({ queryKey: keys.items(workspaceId, listId) })]);
    },
  });

  const toggle = (page: number) =>
    setSelected((current) => {
      const next = new Set(current);
      if (next.has(page)) next.delete(page);
      else next.add(page);
      return next;
    });
  const rotate = (by: number) =>
    setPages((current) =>
      current.map((p) => (selected.has(p.page) ? { ...p, rotate: (p.rotate + by + 360) % 360 } : p)),
    );
  const remove = () => {
    setPages((current) => current.filter((p) => !selected.has(p.page)));
    setSelected(new Set());
  };
  const shift = (by: number) =>
    setPages((current) => {
      const next = [...current];
      const indexes = next.map((p, i) => (selected.has(p.page) ? i : -1)).filter((i) => i >= 0);
      if (indexes.length !== 1) return current;
      const from = indexes[0]!;
      const to = from + by;
      if (to < 0 || to >= next.length) return current;
      [next[from], next[to]] = [next[to]!, next[from]!];
      return next;
    });

  if (!file.pageCount) return null;

  return (
    <section className="flex flex-col gap-3">
      <div className="flex flex-wrap items-center gap-2">
        <h3 className="flex-1 text-[13px] font-semibold">Pages</h3>
        {editable && selected.size > 0 && (
          <div className="flex flex-wrap items-center gap-1" role="toolbar" aria-label="Page tools">
            <span className="mr-1 text-xs text-muted">{selected.size} selected</span>
            <Button size="icon" variant="ghost" aria-label="Rotate left" onClick={() => rotate(-90)}>
              <RotateCcw />
            </Button>
            <Button size="icon" variant="ghost" aria-label="Rotate right" onClick={() => rotate(90)}>
              <RotateCw />
            </Button>
            <Button
              size="icon"
              variant="ghost"
              aria-label="Move earlier"
              disabled={selected.size !== 1}
              onClick={() => shift(-1)}
            >
              <ArrowLeft />
            </Button>
            <Button
              size="icon"
              variant="ghost"
              aria-label="Move later"
              disabled={selected.size !== 1}
              onClick={() => shift(1)}
            >
              <ArrowRight />
            </Button>
            <Button
              size="icon"
              variant="ghost"
              aria-label="Delete pages"
              disabled={selected.size === pages.length}
              onClick={remove}
            >
              <Trash2 />
            </Button>
            <Button
              size="sm"
              disabled={changed || extract.isPending}
              onClick={() => extract.mutate(true)}
              title="Move the pages into a new document"
            >
              <Scissors /> Split off
            </Button>
            <Button size="icon" variant="ghost" aria-label="Clear selection" onClick={() => setSelected(new Set())}>
              <X />
            </Button>
          </div>
        )}
      </div>
      {!editable && (
        <p className="text-xs text-muted">Page tools work on PDFs; images get a PDF when they are processed.</p>
      )}
      {save.isError && <Alert>{problemMessage(save.error)}</Alert>}
      {extract.isError && <Alert>{problemMessage(extract.error)}</Alert>}
      <ol className="grid grid-cols-3 gap-3 sm:grid-cols-4">
        {pages.map((p, index) => (
          <li key={p.page} className="flex flex-col gap-1">
            <button
              type="button"
              aria-pressed={selected.has(p.page)}
              aria-label={`Page ${index + 1}${p.page !== index + 1 ? ` (was ${p.page})` : ''}`}
              onClick={() => (editable ? toggle(p.page) : setLarge(p.page))}
              onDoubleClick={() => setLarge(p.page)}
              className={cn('rounded-md p-0.5 outline-offset-2', selected.has(p.page) && 'ring-2 ring-accent')}
            >
              <PageImage
                workspaceId={workspaceId}
                listId={listId}
                itemId={itemId}
                page={p.page}
                version={file.number ?? 0}
                rotate={p.rotate}
              />
            </button>
            <span className="flex items-center justify-between text-xs text-muted">
              <span>{index + 1}</span>
              {editable && (
                <Checkbox
                  aria-label={`Select page ${index + 1}`}
                  checked={selected.has(p.page)}
                  onChange={() => toggle(p.page)}
                />
              )}
            </span>
          </li>
        ))}
      </ol>
      {changed && (
        <div className="flex items-center gap-2 rounded-lg bg-accent-soft px-3 py-2 text-[13px]">
          <span className="flex-1">Unsaved page changes</span>
          <Button size="sm" onClick={() => setPages(original)}>
            Discard
          </Button>
          <Button size="sm" variant="primary" disabled={save.isPending} onClick={() => save.mutate()}>
            {save.isPending && <Spinner className="text-current" />} Save pages
          </Button>
        </div>
      )}
      <Dialog open={large !== undefined} onOpenChange={(open) => !open && setLarge(undefined)}>
        <DialogContent className="top-[4vh] max-w-3xl p-3" aria-describedby={undefined}>
          <DialogTitle className="sr-only">Page {large}</DialogTitle>
          {large !== undefined && (
            <PageImage
              workspaceId={workspaceId}
              listId={listId}
              itemId={itemId}
              page={large}
              version={file.number ?? 0}
              rotate={0}
            />
          )}
        </DialogContent>
      </Dialog>
    </section>
  );
}

function FileHistory({
  workspaceId,
  listId,
  itemId,
  versions,
}: {
  workspaceId: string;
  listId: string;
  itemId: string;
  versions: FileVersionResponse[];
}) {
  const format = useFormat();
  const users = useUsers();
  const queryClient = useQueryClient();
  const restore = useMutation({
    mutationFn: (number: number) =>
      listBuilder(workspaceId, listId).items.byItemId(itemId).file.versions.byNumber(number).restore.post(),
    onSuccess: async (_, number) => {
      toast.success(`File version ${number} restored as a new version.`);
      await queryClient.invalidateQueries({ queryKey: keys.item(workspaceId, listId, itemId) });
    },
  });
  if (versions.length < 2) return null;

  return (
    <section>
      <h3 className="mb-2 text-[13px] font-semibold">File versions</h3>
      <ol className="divide-y rounded-lg border">
        {versions.map((v) => (
          <li key={v.number} className="flex flex-wrap items-center gap-2 px-3 py-2 text-[13px]">
            <span className="font-medium">Version {v.number}</span>
            {v.isCurrent && <Badge tone="accent">Current</Badge>}
            <span className="min-w-0 flex-1 truncate text-xs text-muted">
              {v.source} · {userName(users.get(v.createdBy ?? ''), v.createdBy ?? '')} · {format.relative(v.createdAt)}
            </span>
            {!v.isCurrent && (
              <Button size="sm" variant="ghost" disabled={restore.isPending} onClick={() => restore.mutate(v.number!)}>
                <RotateCcw /> Restore
              </Button>
            )}
          </li>
        ))}
      </ol>
    </section>
  );
}
