import type { NoteLinkResponse } from '@paperdotnet/client';
import { Link } from '@tanstack/react-router';
import ReactMarkdown, { defaultUrlTransform } from 'react-markdown';
import remarkGfm from 'remark-gfm';

const WikiLink = /(!?)\[\[([^\]|#]+)(#[^\]|]*)?(\|[^\]]*)?\]\]/g;

/** [[Title#Heading|alias]] as Markdown links to wiki:Title (code spans and blocks are left alone). */
export function wikiToMarkdown(text: string): string {
  return text
    .split(/(```[\s\S]*?```|`[^`\n]*`)/g)
    .map((part, i) =>
      i % 2 === 1
        ? part
        : part.replace(WikiLink, (_, embed: string, target: string, heading?: string, alias?: string) => {
            const label = alias ? alias.slice(1) : `${target}${heading ?? ''}`;
            return `${embed ? '↳ ' : ''}[${label.replaceAll(']', '\\]')}](wiki:${encodeURIComponent(target.trim())})`;
          }),
    )
    .join('');
}

/**
 * A note's Markdown (GFM) as HTML. Raw HTML in the text is not rendered (react-markdown escapes it), links open in a
 * new tab, and [[wiki links]] go to the linked note when it exists (LST-18).
 */
export function MarkdownView({ text, links }: { text: string; links?: NoteLinkResponse[] }) {
  const byTarget = new Map((links ?? []).filter((l) => l.note).map((l) => [l.target?.toLowerCase(), l.note!]));
  return (
    <div className="prose-note text-[14px] leading-relaxed">
      <ReactMarkdown
        remarkPlugins={[remarkGfm]}
        urlTransform={(url) => (url.startsWith('wiki:') ? url : defaultUrlTransform(url))}
        components={{
          a: ({ href, children }) => {
            if (href?.startsWith('wiki:')) {
              const target = decodeURIComponent(href.slice(5));
              const note = byTarget.get(target.toLowerCase());
              return note ? (
                <Link
                  to="/w/$workspaceId/l/$listId"
                  params={{ workspaceId: note.workspaceId!, listId: note.listId! }}
                  search={{ item: note.itemId! }}
                  className="font-medium text-accent hover:underline"
                >
                  {children}
                </Link>
              ) : (
                <span className="border-b border-dashed border-muted/60 text-muted" title="No note has this title yet">
                  {children}
                </span>
              );
            }
            return (
              <a href={href} target="_blank" rel="noreferrer noopener" className="text-accent hover:underline">
                {children}
              </a>
            );
          },
        }}
      >
        {wikiToMarkdown(text)}
      </ReactMarkdown>
    </div>
  );
}
