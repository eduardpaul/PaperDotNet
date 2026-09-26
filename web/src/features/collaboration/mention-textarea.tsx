import type { UserResponse } from '@paperdotnet/client';
import { useQuery } from '@tanstack/react-query';
import { useRef, useState } from 'react';
import { Avatar } from '@/components/ui/avatar';
import { Textarea } from '@/components/ui/input';
import { userName, usersQuery } from '@/features/fields/directory';
import { cn } from '@/lib/utils';

/** The @word being typed at the caret, if any. */
function mentionAt(text: string, caret: number): { start: number; query: string } | undefined {
  const match = /(^|\s)@([\p{L}\p{N}._-]*)$/u.exec(text.slice(0, caret));
  return match ? { start: caret - match[2]!.length - 1, query: match[2]!.toLowerCase() } : undefined;
}

/**
 * A textarea that suggests people after "@" (LST-17). Picking one inserts "@Name" and reports the user id, so the
 * comment is sent with its mentions.
 */
export function MentionTextarea({
  value,
  onChange,
  mentions,
  onMentionsChange,
  onSubmit,
  ...props
}: {
  value: string;
  onChange: (value: string) => void;
  mentions: string[];
  onMentionsChange: (ids: string[]) => void;
  onSubmit?: () => void;
  placeholder?: string;
  'aria-label'?: string;
  autoFocus?: boolean;
}) {
  const ref = useRef<HTMLTextAreaElement>(null);
  const { data: users } = useQuery(usersQuery);
  const [mention, setMention] = useState<{ start: number; query: string }>();
  const [active, setActive] = useState(0);
  const matches = mention
    ? (users ?? [])
        .filter(
          (u) =>
            !u.isDisabled &&
            (userName(u, u.id!).toLowerCase().includes(mention.query) ||
              u.userName?.toLowerCase().includes(mention.query)),
        )
        .slice(0, 6)
    : [];

  const pick = (user: UserResponse) => {
    if (!mention) return;
    const name = userName(user, user.id!);
    const caret = ref.current?.selectionStart ?? value.length;
    const next = `${value.slice(0, mention.start)}@${name} ${value.slice(caret)}`;
    onChange(next);
    if (!mentions.includes(user.id!)) onMentionsChange([...mentions, user.id!]);
    setMention(undefined);
    requestAnimationFrame(() => {
      const position = mention.start + name.length + 2;
      ref.current?.setSelectionRange(position, position);
      ref.current?.focus();
    });
  };

  return (
    <div className="relative">
      <Textarea
        ref={ref}
        rows={3}
        value={value}
        {...props}
        onChange={(e) => {
          onChange(e.target.value);
          setMention(mentionAt(e.target.value, e.target.selectionStart));
          setActive(0);
        }}
        onKeyDown={(e) => {
          if (matches.length) {
            if (e.key === 'ArrowDown' || e.key === 'ArrowUp') {
              e.preventDefault();
              setActive((i) => (i + (e.key === 'ArrowDown' ? 1 : matches.length - 1)) % matches.length);
              return;
            }
            if (e.key === 'Enter' || e.key === 'Tab') {
              e.preventDefault();
              pick(matches[active]!);
              return;
            }
            if (e.key === 'Escape') {
              e.stopPropagation();
              setMention(undefined);
              return;
            }
          }
          if (e.key === 'Enter' && (e.metaKey || e.ctrlKey) && onSubmit) {
            e.preventDefault();
            onSubmit();
          }
        }}
      />
      {matches.length > 0 && (
        <ul
          role="listbox"
          aria-label="People"
          className="absolute bottom-full left-0 z-10 mb-1 w-64 rounded-lg border bg-surface p-1 shadow-lg"
        >
          {matches.map((user, index) => (
            <li
              key={user.id}
              role="option"
              aria-selected={index === active}
              onMouseDown={(e) => {
                e.preventDefault();
                pick(user);
              }}
              className={cn(
                'flex cursor-default items-center gap-2 rounded px-2 py-1.5 text-[13px]',
                index === active && 'bg-surface-muted',
              )}
            >
              <Avatar name={userName(user, user.id!)} className="size-5 text-[9px]" />
              <span className="truncate">{userName(user, user.id!)}</span>
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}
