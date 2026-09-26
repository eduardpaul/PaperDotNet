import { Check, ExternalLink, Minus } from 'lucide-react';
import { Avatar } from '@/components/ui/avatar';
import { useFormat } from '@/lib/preferences';
import { cn } from '@/lib/utils';
import { userName } from './directory';
import { termLabel, useValueNames } from './lookups';
import { choiceLabel, idsOf, isEmpty, type FieldDefinition } from './values';

/** A field value for reading (tables, cards, details), by field type. */
export function FieldValue({
  field,
  value,
  className,
}: {
  field: FieldDefinition;
  value: unknown;
  className?: string;
}) {
  const format = useFormat();
  const names = useValueNames();
  if (isEmpty(value)) return <span className={cn('text-muted/60', className)}>—</span>;

  switch (field.type) {
    case 'boolean':
      return value ? (
        <Check aria-label="Yes" className={cn('size-4 text-success', className)} />
      ) : (
        <Minus aria-label="No" className={cn('size-4 text-muted', className)} />
      );
    case 'number':
      return <span className={cn('tabular-nums', className)}>{format.number(Number(value))}</span>;
    case 'currency':
      return (
        <span className={cn('tabular-nums', className)}>
          {format.currency(Number(value), field.currencyCode ?? 'EUR')}
        </span>
      );
    case 'date':
      return <span className={className}>{format.date(String(value))}</span>;
    case 'dateTime':
      return <span className={className}>{format.dateTime(String(value))}</span>;
    case 'url':
      return (
        <a
          href={String(value)}
          target="_blank"
          rel="noreferrer noopener"
          className={cn('inline-flex items-center gap-1 text-accent hover:underline', className)}
          onClick={(e) => e.stopPropagation()}
        >
          <span className="truncate">{String(value).replace(/^https?:\/\//, '')}</span>
          <ExternalLink className="size-3 shrink-0" />
        </a>
      );
    case 'email':
      return (
        <a
          href={`mailto:${String(value)}`}
          className={cn('text-accent hover:underline', className)}
          onClick={(e) => e.stopPropagation()}
        >
          {String(value)}
        </a>
      );
    case 'choice':
      return (
        <span className={cn('flex flex-wrap gap-1', className)}>
          {(Array.isArray(value) ? value : [value]).map((choice) => (
            <span key={String(choice)} className="rounded bg-surface-muted px-1.5 py-0.5 text-xs font-medium">
              {choiceLabel(String(choice))}
            </span>
          ))}
        </span>
      );
    case 'person':
      return (
        <span className={cn('flex flex-wrap items-center gap-x-2 gap-y-1', className)}>
          {idsOf(value).map((id) => {
            const name = userName(names.users.get(id), id);
            return (
              <span key={id} className="inline-flex items-center gap-1.5">
                <Avatar name={name} className="size-5 text-[9px]" />
                <span className="truncate">{name}</span>
              </span>
            );
          })}
        </span>
      );
    case 'lookup':
      return (
        <span className={cn('flex flex-wrap gap-1', className)}>
          {idsOf(value).map((id) => (
            <span key={id} className="rounded bg-accent-soft px-1.5 py-0.5 text-xs font-medium text-accent">
              {names.items.get(id) ?? '…'}
            </span>
          ))}
        </span>
      );
    case 'managedMetadata':
    case 'keywords':
      return (
        <span className={cn('flex flex-wrap gap-1', className)}>
          {idsOf(value).map((id) => {
            const term = names.terms.get(id);
            return (
              <span
                key={id}
                className="inline-flex items-center gap-1 rounded-full bg-surface-muted px-2 py-0.5 text-xs font-medium"
              >
                {term?.color && <span className="size-2 rounded-full" style={{ background: term.color }} />}
                {termLabel(term, format.preferences.language) ?? '…'}
              </span>
            );
          })}
        </span>
      );
    case 'note':
      return <span className={cn('line-clamp-2 whitespace-pre-line', className)}>{String(value)}</span>;
    default:
      return (
        <span className={cn('truncate', className)}>
          {typeof value === 'object' ? JSON.stringify(value) : String(value)}
        </span>
      );
  }
}
