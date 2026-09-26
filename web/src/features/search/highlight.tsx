import { Fragment } from 'react';

/** The words of a query (no operators or quotes), for highlighting. */
export function queryWords(query: string | undefined): string[] {
  return (query ?? '')
    .replace(/["()]/g, ' ')
    .split(/\s+/)
    .filter((w) => w && !w.startsWith('-') && !/^(OR|AND|NOT)$/.test(w))
    .map((w) => w.replace(/\*$/, ''))
    .filter((w) => w.length > 1);
}

/** Text with the query's words marked (prefix match, so "invoice" marks "invoices"). */
export function Highlight({ text, words }: { text: string; words: string[] }) {
  if (!words.length) return <>{text}</>;
  const pattern = new RegExp(`(${words.map((w) => w.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')).join('|')})`, 'gi');
  return (
    <>
      {text.split(pattern).map((part, i) =>
        i % 2 === 1 ? (
          <mark key={i} className="rounded-sm bg-warning-soft px-0.5 text-foreground">
            {part}
          </mark>
        ) : (
          <Fragment key={i}>{part}</Fragment>
        ),
      )}
    </>
  );
}
