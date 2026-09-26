import { fieldsOf } from '@paperdotnet/client';
import { useQuery } from '@tanstack/react-query';
import { ChevronRight, Home } from 'lucide-react';
import { Fragment } from 'react';
import { itemQuery } from './queries';

/** The path to the current folder: list root › Folder › Subfolder. */
export function FolderBreadcrumb({
  workspaceId,
  listId,
  listName,
  folderId,
  onNavigate,
}: {
  workspaceId: string;
  listId: string;
  listName: string;
  folderId: string;
  onNavigate: (folderId: string | undefined) => void;
}) {
  return (
    <nav aria-label="Folder" className="mb-3 flex flex-wrap items-center gap-1 text-[13px] text-muted">
      <button
        type="button"
        className="inline-flex items-center gap-1 hover:text-foreground"
        onClick={() => onNavigate(undefined)}
      >
        <Home className="size-3.5" /> {listName}
      </button>
      <Crumbs workspaceId={workspaceId} listId={listId} folderId={folderId} current onNavigate={onNavigate} />
    </nav>
  );
}

function Crumbs({
  workspaceId,
  listId,
  folderId,
  current,
  onNavigate,
}: {
  workspaceId: string;
  listId: string;
  folderId: string;
  current?: boolean;
  onNavigate: (folderId: string | undefined) => void;
}) {
  const { data: folder } = useQuery(itemQuery(workspaceId, listId, folderId));
  return (
    <Fragment>
      {folder?.parentId && (
        <Crumbs workspaceId={workspaceId} listId={listId} folderId={folder.parentId} onNavigate={onNavigate} />
      )}
      <ChevronRight className="size-3.5" />
      {current ? (
        <span className="font-medium text-foreground" aria-current="page">
          {String(fieldsOf(folder).title ?? '…')}
        </span>
      ) : (
        <button type="button" className="hover:text-foreground" onClick={() => onNavigate(folderId)}>
          {String(fieldsOf(folder).title ?? '…')}
        </button>
      )}
    </Fragment>
  );
}
