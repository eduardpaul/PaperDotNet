import type { LinkedNote } from '@paperdotnet/client';
import { useQuery } from '@tanstack/react-query';
import { Link } from '@tanstack/react-router';
import { ArrowDownLeft, ArrowUpRight, NotebookPen } from 'lucide-react';
import { keys } from '@/api/keys';
import { Skeleton } from '@/components/ui/feedback';
import type { ItemPanelContext } from '@/extensibility/item-panels';
import { listBuilder } from '@/features/lists/queries';

function NoteLink({ note, label }: { note: LinkedNote; label?: string }) {
  return (
    <Link
      to="/w/$workspaceId/l/$listId"
      params={{ workspaceId: note.workspaceId!, listId: note.listId! }}
      search={{ item: note.itemId! }}
      className="flex items-center gap-2 rounded-md px-2 py-1.5 text-[13px] hover:bg-surface-muted"
    >
      <NotebookPen className="size-3.5 text-muted" />
      <span className="truncate">{label ?? note.title}</span>
    </Link>
  );
}

/** A note's [[links]] and the notes that link to it (LST-18). */
export function NoteLinksTab({ workspaceId, list, item }: ItemPanelContext) {
  const builder = listBuilder(workspaceId, list.id!).items.byItemId(item.id!);
  const key = keys.item(workspaceId, list.id!, item.id!);
  const outgoing = useQuery({
    queryKey: [...key, 'noteLinks'],
    queryFn: async () => (await builder.noteLinks.get())?.value ?? [],
  });
  const backlinks = useQuery({
    queryKey: [...key, 'backlinks'],
    queryFn: async () => (await builder.backlinks.get())?.value ?? [],
  });

  return (
    <div className="flex flex-col gap-5 p-5">
      <section>
        <h3 className="mb-1 flex items-center gap-1.5 text-xs font-medium text-muted">
          <ArrowUpRight className="size-3.5" /> Links to
        </h3>
        {outgoing.isPending ? (
          <Skeleton className="h-10" />
        ) : outgoing.data?.length ? (
          <ul>
            {outgoing.data.map((l, i) => (
              <li key={i}>
                {l.note ? (
                  <NoteLink note={l.note} label={l.alias ?? undefined} />
                ) : (
                  <span
                    className="flex items-center gap-2 px-2 py-1.5 text-[13px] text-muted"
                    title="No note has this title yet"
                  >
                    <NotebookPen className="size-3.5" /> {l.target} <span className="text-xs">(not written yet)</span>
                  </span>
                )}
              </li>
            ))}
          </ul>
        ) : (
          <p className="px-2 text-[13px] text-muted">No [[links]] in this note.</p>
        )}
      </section>
      <section>
        <h3 className="mb-1 flex items-center gap-1.5 text-xs font-medium text-muted">
          <ArrowDownLeft className="size-3.5" /> Linked from
        </h3>
        {backlinks.isPending ? (
          <Skeleton className="h-10" />
        ) : backlinks.data?.length ? (
          <ul>
            {backlinks.data.map((note) => (
              <li key={note.itemId}>
                <NoteLink note={note} />
              </li>
            ))}
          </ul>
        ) : (
          <p className="px-2 text-[13px] text-muted">No other note links here yet.</p>
        )}
      </section>
    </div>
  );
}
