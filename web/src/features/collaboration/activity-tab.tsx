import type { ActivityResponse, CommentResponse } from '@paperdotnet/client';
import { all, ifMatch, toArray } from '@paperdotnet/client';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { History, MessageSquare, Pencil, Reply, Trash2 } from 'lucide-react';
import { Fragment, useState } from 'react';
import { keys } from '@/api/keys';
import { meQuery } from '@/api/queries';
import { Avatar } from '@/components/ui/avatar';
import { Button } from '@/components/ui/button';
import { Alert, Skeleton } from '@/components/ui/feedback';
import type { ItemPanelContext } from '@/extensibility/item-panels';
import { userName, useUsers } from '@/features/fields/directory';
import { listBuilder } from '@/features/lists/queries';
import { fieldLabel, listFields } from '@/features/lists/schema';
import { problemMessage } from '@/lib/errors';
import { useFormat } from '@/lib/preferences';
import { MentionTextarea } from './mention-textarea';

/** Comments with replies and @mentions, and what happened to the item (LST-17). */
export function ActivityTab({ workspaceId, list, item }: ItemPanelContext) {
  const builder = listBuilder(workspaceId, list.id!).items.byItemId(item.id!);
  const key = keys.item(workspaceId, list.id!, item.id!);
  const comments = useQuery({
    queryKey: [...key, 'comments'],
    queryFn: () => toArray(all(builder.comments, { queryParameters: { top: 100 } })),
  });
  const activity = useQuery({
    queryKey: [...key, 'activity'],
    queryFn: () => toArray(all(builder.activity, { queryParameters: { top: 100 } }), 200),
  });
  const topLevel = (comments.data ?? []).filter((c) => !c.parentId);
  const replies = (id: string) => (comments.data ?? []).filter((c) => c.parentId === id);

  return (
    <div className="flex flex-col gap-6 p-5">
      <section className="flex flex-col gap-3">
        <h3 className="flex items-center gap-2 text-[13px] font-semibold">
          <MessageSquare className="size-4 text-muted" /> Comments
        </h3>
        {comments.isPending ? (
          <Skeleton className="h-16" />
        ) : (
          <ol className="flex flex-col gap-4">
            {topLevel.map((comment) => (
              <li key={comment.id} className="flex flex-col gap-3">
                <CommentView context={{ workspaceId, list, item }} comment={comment} />
                {replies(comment.id!).length > 0 && (
                  <ol className="ml-9 flex flex-col gap-3 border-l pl-3">
                    {replies(comment.id!).map((reply) => (
                      <li key={reply.id}>
                        <CommentView context={{ workspaceId, list, item }} comment={reply} isReply />
                      </li>
                    ))}
                  </ol>
                )}
              </li>
            ))}
          </ol>
        )}
        <Composer context={{ workspaceId, list, item }} />
      </section>
      <section className="flex flex-col gap-2">
        <h3 className="flex items-center gap-2 text-[13px] font-semibold">
          <History className="size-4 text-muted" /> History
        </h3>
        {activity.isPending ? <Skeleton className="h-16" /> : <Timeline entries={activity.data ?? []} list={list} />}
      </section>
    </div>
  );
}

function Timeline({ entries, list }: { entries: ActivityResponse[]; list: ItemPanelContext['list'] }) {
  const format = useFormat();
  const users = useUsers();
  const fields = listFields(list);
  // History is written in the background, so a new item can show none for a moment.
  if (!entries.length) return <p className="text-xs text-muted">Nothing recorded yet.</p>;
  return (
    <ol className="flex flex-col gap-2">
      {entries.map((entry) => (
        <li key={entry.id} className="flex gap-2 text-xs text-muted">
          <span className="mt-1.5 size-1.5 shrink-0 rounded-full bg-border" />
          <span>
            <span className="font-medium text-foreground">
              {userName(users.get(entry.actorId ?? ''), entry.actorId ?? '')}
            </span>{' '}
            {entry.summary}
            {!!entry.changedFields?.length && (
              <>
                {' '}
                (
                {entry.changedFields
                  .map((name) => fieldLabel(fields.find((f) => f.name === name) ?? { name }))
                  .join(', ')}
                )
              </>
            )}{' '}
            · <span title={format.dateTime(entry.at)}>{format.relative(entry.at)}</span>
          </span>
        </li>
      ))}
    </ol>
  );
}

/** Comment text with @mentions highlighted. */
function CommentText({ text, mentions }: { text: string; mentions: string[] }) {
  const users = useUsers();
  const names = mentions
    .map((id) => userName(users.get(id), id))
    .filter(Boolean)
    .sort((a, b) => b.length - a.length);
  if (!names.length) return <>{text}</>;
  const pattern = new RegExp(`(@(?:${names.map((n) => n.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')).join('|')}))`, 'g');
  return (
    <>
      {text.split(pattern).map((part, i) =>
        i % 2 === 1 ? (
          <span key={i} className="font-medium text-accent">
            {part}
          </span>
        ) : (
          <Fragment key={i}>{part}</Fragment>
        ),
      )}
    </>
  );
}

function CommentView({
  context,
  comment,
  isReply,
}: {
  context: ItemPanelContext;
  comment: CommentResponse;
  isReply?: boolean;
}) {
  const format = useFormat();
  const users = useUsers();
  const queryClient = useQueryClient();
  const { data: me } = useQuery(meQuery);
  const [editing, setEditing] = useState(false);
  const [replying, setReplying] = useState(false);
  const [text, setText] = useState(comment.text ?? '');
  const [mentions, setMentions] = useState<string[]>(comment.mentions ?? []);
  const builder = listBuilder(context.workspaceId, context.list.id!)
    .items.byItemId(context.item.id!)
    .comments.byCommentId(comment.id!);
  const invalidate = () =>
    queryClient.invalidateQueries({
      queryKey: [...keys.item(context.workspaceId, context.list.id!, context.item.id!), 'comments'],
    });
  const update = useMutation({
    mutationFn: () => builder.patch({ text: text.trim(), mentions }, ifMatch(comment)),
    onSuccess: async () => {
      setEditing(false);
      await invalidate();
    },
  });
  const remove = useMutation({ mutationFn: () => builder.delete(ifMatch(comment)), onSuccess: invalidate });
  const author = userName(users.get(comment.createdBy ?? ''), comment.createdBy ?? '');
  const mine = comment.createdBy === me?.id;

  return (
    <div className="flex flex-col gap-2">
      <div className="group flex gap-2.5">
        <Avatar name={author} className="size-7" />
        <div className="min-w-0 flex-1">
          <p className="text-xs">
            <span className="font-semibold">{author}</span>{' '}
            <span className="text-muted" title={format.dateTime(comment.createdAt)}>
              {format.relative(comment.createdAt)}
              {comment.updatedAt && +comment.updatedAt !== +(comment.createdAt ?? 0) && ' · edited'}
            </span>
          </p>
          {editing ? (
            <div className="mt-1 flex flex-col gap-2">
              <MentionTextarea
                aria-label="Edit comment"
                value={text}
                onChange={setText}
                mentions={mentions}
                onMentionsChange={setMentions}
                onSubmit={() => update.mutate()}
                autoFocus
              />
              {update.isError && <Alert>{problemMessage(update.error)}</Alert>}
              <div className="flex gap-2">
                <Button
                  size="sm"
                  variant="primary"
                  disabled={!text.trim() || update.isPending}
                  onClick={() => update.mutate()}
                >
                  Save
                </Button>
                <Button size="sm" onClick={() => setEditing(false)}>
                  Cancel
                </Button>
              </div>
            </div>
          ) : (
            <p className="mt-0.5 text-[13px] whitespace-pre-line">
              <CommentText text={comment.text ?? ''} mentions={comment.mentions ?? []} />
            </p>
          )}
          {!editing && (
            <div className="mt-1 flex gap-3 text-xs text-muted opacity-70 group-hover:opacity-100">
              {!isReply && (
                <button
                  type="button"
                  className="inline-flex items-center gap-1 hover:text-foreground"
                  onClick={() => setReplying(true)}
                >
                  <Reply className="size-3" /> Reply
                </button>
              )}
              {mine && (
                <>
                  <button
                    type="button"
                    className="inline-flex items-center gap-1 hover:text-foreground"
                    onClick={() => setEditing(true)}
                  >
                    <Pencil className="size-3" /> Edit
                  </button>
                  <button
                    type="button"
                    className="inline-flex items-center gap-1 hover:text-danger"
                    onClick={() => remove.mutate()}
                  >
                    <Trash2 className="size-3" /> Delete
                  </button>
                </>
              )}
            </div>
          )}
        </div>
      </div>
      {replying && (
        <div className="ml-9">
          <Composer context={context} parentId={comment.id!} onDone={() => setReplying(false)} autoFocus />
        </div>
      )}
    </div>
  );
}

function Composer({
  context,
  parentId,
  onDone,
  autoFocus,
}: {
  context: ItemPanelContext;
  parentId?: string;
  onDone?: () => void;
  autoFocus?: boolean;
}) {
  const queryClient = useQueryClient();
  const [text, setText] = useState('');
  const [mentions, setMentions] = useState<string[]>([]);
  const post = useMutation({
    meta: { silent: true },
    mutationFn: () =>
      listBuilder(context.workspaceId, context.list.id!)
        .items.byItemId(context.item.id!)
        .comments.post({ text: text.trim(), mentions: mentions.length ? mentions : undefined, parentId }),
    onSuccess: async () => {
      setText('');
      setMentions([]);
      onDone?.();
      await queryClient.invalidateQueries({
        queryKey: [...keys.item(context.workspaceId, context.list.id!, context.item.id!)],
      });
    },
  });
  return (
    <div className="flex flex-col gap-2">
      <MentionTextarea
        aria-label={parentId ? 'Reply' : 'New comment'}
        placeholder={parentId ? 'Reply…' : 'Write a comment… (@ to mention someone)'}
        value={text}
        onChange={setText}
        mentions={mentions}
        onMentionsChange={setMentions}
        onSubmit={() => text.trim() && post.mutate()}
        autoFocus={autoFocus}
      />
      {post.isError && <Alert>{problemMessage(post.error)}</Alert>}
      <div className="flex justify-end gap-2">
        {onDone && (
          <Button size="sm" onClick={onDone}>
            Cancel
          </Button>
        )}
        <Button size="sm" variant="primary" disabled={!text.trim() || post.isPending} onClick={() => post.mutate()}>
          {parentId ? 'Reply' : 'Comment'}
        </Button>
      </div>
    </div>
  );
}
