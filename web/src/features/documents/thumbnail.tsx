import { FileText } from 'lucide-react';
import { useAuthedImage } from '@/lib/authed-image';
import { cn } from '@/lib/utils';
import { thumbnailPath } from './paths';

/** A document's thumbnail (DOC-04), or a file icon until it has one. */
export function Thumbnail({
  workspaceId,
  listId,
  itemId,
  version,
  className,
}: {
  workspaceId: string;
  listId: string;
  itemId: string;
  /** Changes when the file changes (e.g. the item's ETag), so the image is fetched again. */
  version?: string | null;
  className?: string;
}) {
  const { url } = useAuthedImage(thumbnailPath(workspaceId, listId, itemId), version ?? undefined);
  return (
    <span className={cn('flex items-center justify-center overflow-hidden rounded border bg-surface-muted', className)}>
      {url ? (
        <img src={url} alt="" className="h-full w-full object-cover object-top" />
      ) : (
        <FileText className="size-1/2 max-h-8 max-w-8 text-muted/70" />
      )}
    </span>
  );
}
