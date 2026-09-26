// Uploads (DOC-01) from anywhere in the app: a queue with a tray that shows each file's state, duplicate warnings
// (DOC-10) and a link to the new document. fetch cannot report progress, so an upload shows as "uploading" until done.
import type { DocumentResponse } from '@paperdotnet/client';
import { uploadBody } from '@paperdotnet/client';
import { useQueryClient } from '@tanstack/react-query';
import { Link } from '@tanstack/react-router';
import { AlertTriangle, CheckCircle2, Copy, X, XCircle } from 'lucide-react';
import { createContext, useCallback, useContext, useMemo, useRef, useState, type ReactNode } from 'react';
import { api } from '@/api/client';
import { keys } from '@/api/keys';
import { Spinner } from '@/components/ui/feedback';
import { problemMessage } from '@/lib/errors';
import { useFormat } from '@/lib/preferences';
import { cn } from '@/lib/utils';

export type UploadTarget =
  | { kind: 'library'; workspaceId: string; listId: string; folderId?: string; name: string }
  | { kind: 'inbox'; name: string }
  | { kind: 'group'; groupId: string; name: string };

export interface Upload {
  id: number;
  file: File;
  target: UploadTarget;
  status: 'uploading' | 'done' | 'failed';
  result?: DocumentResponse;
  error?: string;
}

const UploadsContext = createContext<{ uploads: Upload[]; upload: (files: File[], target: UploadTarget) => void }>({
  uploads: [],
  upload: () => {},
});

export function useUploads() {
  return useContext(UploadsContext);
}

async function send(file: File, target: UploadTarget): Promise<DocumentResponse | undefined> {
  switch (target.kind) {
    case 'library':
      return api.v10.workspaces
        .byWorkspaceId(target.workspaceId)
        .lists.byListId(target.listId)
        .documents.post(await uploadBody({ file, folderId: target.folderId }));
    case 'inbox':
      return api.v10.me.inbox.documents.post(await uploadBody({ file }));
    case 'group':
      return api.v10.groups.byId(target.groupId).inbox.documents.post(await uploadBody({ file }));
  }
}

export function UploadsProvider({ children }: { children: ReactNode }) {
  const queryClient = useQueryClient();
  const [uploads, setUploads] = useState<Upload[]>([]);
  const next = useRef(1);
  const update = (id: number, patch: Partial<Upload>) =>
    setUploads((current) => current.map((u) => (u.id === id ? { ...u, ...patch } : u)));

  const upload = useCallback(
    (files: File[], target: UploadTarget) => {
      const added = files.map((file) => ({ id: next.current++, file, target, status: 'uploading' as const }));
      setUploads((current) => [...added, ...current].slice(0, 30));
      // One at a time: large scans should not compete for the connection.
      void (async () => {
        for (const entry of added) {
          try {
            const result = await send(entry.file, target);
            update(entry.id, { status: 'done', result });
            if (result?.workspaceId && result.listId) {
              await queryClient.invalidateQueries({ queryKey: keys.items(result.workspaceId, result.listId) });
            }
          } catch (error) {
            update(entry.id, { status: 'failed', error: problemMessage(error) });
          }
        }
      })();
    },
    [queryClient],
  );

  const value = useMemo(() => ({ uploads, upload }), [uploads, upload]);
  return (
    <UploadsContext.Provider value={value}>
      {children}
      <UploadTray
        uploads={uploads}
        onDismiss={(id) => setUploads((c) => c.filter((u) => u.id !== id))}
        onClear={() => setUploads((c) => c.filter((u) => u.status === 'uploading'))}
      />
    </UploadsContext.Provider>
  );
}

function UploadTray({
  uploads,
  onDismiss,
  onClear,
}: {
  uploads: Upload[];
  onDismiss: (id: number) => void;
  onClear: () => void;
}) {
  const format = useFormat();
  if (uploads.length === 0) return null;
  const busy = uploads.filter((u) => u.status === 'uploading').length;

  return (
    <section
      aria-label="Uploads"
      className="fixed right-4 bottom-4 z-40 flex max-h-[50vh] w-[min(92vw,380px)] flex-col overflow-hidden rounded-xl border bg-surface shadow-xl"
    >
      <header className="flex items-center gap-2 border-b px-4 py-2.5">
        <h2 className="flex-1 text-[13px] font-semibold">
          {busy ? `Uploading ${busy} of ${uploads.length}…` : 'Uploads'}
        </h2>
        {!busy && (
          <button type="button" onClick={onClear} className="text-xs text-muted hover:text-foreground">
            Clear
          </button>
        )}
      </header>
      <ul className="divide-y overflow-y-auto">
        {uploads.map((u) => {
          const duplicates = u.result?.duplicates ?? [];
          return (
            <li key={u.id} className="flex items-start gap-3 px-4 py-2.5 text-[13px]">
              <span className="mt-0.5">
                {u.status === 'uploading' ? (
                  <Spinner />
                ) : u.status === 'failed' ? (
                  <XCircle className="size-4 text-danger" />
                ) : duplicates.length ? (
                  <AlertTriangle className="size-4 text-warning" />
                ) : (
                  <CheckCircle2 className="size-4 text-success" />
                )}
              </span>
              <div className="min-w-0 flex-1">
                {u.status === 'done' && u.result?.workspaceId ? (
                  <Link
                    to="/w/$workspaceId/l/$listId"
                    params={{ workspaceId: u.result.workspaceId, listId: u.result.listId! }}
                    search={{ item: u.result.itemId!, tab: 'preview' }}
                    className="block truncate font-medium hover:underline"
                  >
                    {u.file.name}
                  </Link>
                ) : (
                  <p className="truncate font-medium">{u.file.name}</p>
                )}
                <p className={cn('text-xs text-muted', u.status === 'failed' && 'text-danger')}>
                  {u.status === 'failed' ? u.error : `${format.fileSize(u.file.size)} → ${u.target.name}`}
                </p>
                {duplicates.map((d) => (
                  <Link
                    key={d.itemId}
                    to="/w/$workspaceId/l/$listId"
                    params={{ workspaceId: d.workspaceId!, listId: d.listId! }}
                    search={{ item: d.itemId! }}
                    className="mt-0.5 flex items-center gap-1 text-xs text-warning hover:underline"
                  >
                    <Copy className="size-3" /> Same file as “{d.title}”
                  </Link>
                ))}
              </div>
              {u.status !== 'uploading' && (
                <button
                  type="button"
                  aria-label="Dismiss"
                  onClick={() => onDismiss(u.id)}
                  className="text-muted hover:text-foreground"
                >
                  <X className="size-3.5" />
                </button>
              )}
            </li>
          );
        })}
      </ul>
    </section>
  );
}
