import { isStatus } from '@paperdotnet/client';
import { useQuery } from '@tanstack/react-query';
import { useState } from 'react';
import { keys } from '@/api/keys';
import { Textarea } from '@/components/ui/input';
import { listBuilder } from '@/features/lists/queries';
import { cn } from '@/lib/utils';
import { MarkdownView } from './markdown';

/** A note's body: Markdown with a live preview; [[links]] resolve once the note is saved. */
export function NoteEditor({
  id,
  value,
  onChange,
  context,
}: {
  id: string;
  value: string;
  onChange: (value: string) => void;
  context?: { workspaceId: string; listId: string; itemId: string };
}) {
  const [mode, setMode] = useState<'write' | 'preview'>(value ? 'preview' : 'write');
  const { data: links } = useQuery({
    queryKey: context
      ? [...keys.item(context.workspaceId, context.listId, context.itemId), 'noteLinks']
      : ['noteLinks', 'none'],
    enabled: !!context,
    retry: false,
    queryFn: async () => {
      try {
        return (
          (await listBuilder(context!.workspaceId, context!.listId).items.byItemId(context!.itemId).noteLinks.get())
            ?.value ?? []
        );
      } catch (error) {
        if (isStatus(error, 404)) return [];
        throw error;
      }
    },
  });

  return (
    <div className="flex flex-col gap-2">
      <div role="tablist" aria-label="Note view" className="flex self-start rounded-md bg-surface-muted p-0.5">
        {(['write', 'preview'] as const).map((m) => (
          <button
            key={m}
            type="button"
            role="tab"
            aria-selected={mode === m}
            onClick={() => setMode(m)}
            className={cn(
              'rounded px-2.5 py-1 text-xs font-medium text-muted capitalize',
              mode === m && 'bg-surface text-foreground shadow-xs',
            )}
          >
            {m}
          </button>
        ))}
      </div>
      {mode === 'write' ? (
        <Textarea
          id={id}
          rows={16}
          className="font-mono text-[13px] leading-relaxed"
          placeholder={'# Heading\n\nWrite in Markdown. Link notes with [[Title]] and tag with #tags.'}
          value={value}
          onChange={(e) => onChange(e.target.value)}
        />
      ) : (
        <div
          id={id}
          role="document"
          tabIndex={0}
          onDoubleClick={() => setMode('write')}
          className="min-h-40 rounded-md border bg-surface px-4 py-3"
        >
          {value ? (
            <MarkdownView text={value} links={links} />
          ) : (
            <p className="text-[13px] text-muted">Nothing written yet.</p>
          )}
        </div>
      )}
    </div>
  );
}
