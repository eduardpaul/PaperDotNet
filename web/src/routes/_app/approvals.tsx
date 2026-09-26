import type { ApprovalResponse } from '@paperdotnet/client';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute, Link, useNavigate } from '@tanstack/react-router';
import { Check, Stamp, X } from 'lucide-react';
import { useState } from 'react';
import { toast } from 'sonner';
import { api } from '@/api/client';
import { Page, PageHeader } from '@/components/page';
import { Badge } from '@/components/ui/badge';
import { Button } from '@/components/ui/button';
import { Card } from '@/components/ui/card';
import { EmptyState, Skeleton } from '@/components/ui/feedback';
import { Textarea } from '@/components/ui/input';
import { userName, useUsers } from '@/features/fields/directory';
import { itemLink } from '@/features/lists/item-link';
import { useFormat } from '@/lib/preferences';
import { cn } from '@/lib/utils';

type Status = 'pending' | 'approved' | 'rejected';
const tabs: { key: Status; label: string }[] = [
  { key: 'pending', label: 'Waiting for me' },
  { key: 'approved', label: 'Approved' },
  { key: 'rejected', label: 'Rejected' },
];

export const Route = createFileRoute('/_app/approvals')({
  validateSearch: (search: Record<string, unknown>): { status?: Status } => ({
    status:
      tabs.some((t) => t.key === search.status) && search.status !== 'pending' ? (search.status as Status) : undefined,
  }),
  component: Approvals,
});

/** Approval steps of automations (EVT-08) assigned to the user, and the ones they decided. */
function Approvals() {
  const { status = 'pending' } = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const { data, isPending } = useQuery({
    queryKey: ['me', 'approvals', status],
    queryFn: async () => (await api.v10.me.approvals.get({ queryParameters: { status, top: 100 } }))?.value ?? [],
  });

  return (
    <Page className="max-w-4xl">
      <PageHeader icon={Stamp} title="Approvals" description="Decisions automations are waiting for." />
      <div role="tablist" aria-label="Approvals" className="mb-4 inline-flex rounded-md bg-surface-muted p-0.5">
        {tabs.map((t) => (
          <button
            key={t.key}
            role="tab"
            type="button"
            aria-selected={status === t.key}
            onClick={() => void navigate({ search: { status: t.key === 'pending' ? undefined : t.key } })}
            className={cn(
              'rounded px-2.5 py-1 text-xs font-medium text-muted',
              status === t.key && 'bg-surface text-foreground shadow-xs',
            )}
          >
            {t.label}
          </button>
        ))}
      </div>
      {isPending ? (
        <Skeleton className="h-40" />
      ) : data?.length ? (
        <Card>
          <ul className="divide-y">
            {data.map((a) => (
              <ApprovalRow key={a.id} approval={a} />
            ))}
          </ul>
        </Card>
      ) : (
        <Card>
          <EmptyState icon={Stamp} title={status === 'pending' ? 'Nothing waits for you' : 'None yet'} />
        </Card>
      )}
    </Page>
  );
}

function ApprovalRow({ approval }: { approval: ApprovalResponse }) {
  const format = useFormat();
  const users = useUsers();
  const queryClient = useQueryClient();
  const [comment, setComment] = useState('');
  const decide = useMutation({
    mutationFn: (outcome: 'approved' | 'rejected') =>
      api.v10.me.approvals.byId(approval.id!).decision.post({ outcome, comment: comment.trim() || undefined }),
    onSuccess: async (_, outcome) => {
      toast.success(outcome === 'approved' ? 'Approved.' : 'Rejected.');
      await queryClient.invalidateQueries({ queryKey: ['me', 'approvals'] });
    },
  });
  const link = itemLink(approval);
  const pending = approval.status === 'pending';

  return (
    <li className="flex flex-col gap-2 px-4 py-3">
      <div className="flex flex-wrap items-center gap-2">
        {link ? (
          <Link {...link} className="min-w-0 flex-1 truncate text-[13px] font-medium hover:underline">
            {approval.title}
          </Link>
        ) : (
          <span className="min-w-0 flex-1 truncate text-[13px] font-medium">{approval.title}</span>
        )}
        {approval.escalated && <Badge tone="warning">Escalated</Badge>}
        {!pending && (
          <Badge tone={approval.status === 'approved' ? 'success' : 'danger'}>
            {approval.status === 'approved' ? 'Approved' : 'Rejected'}
          </Badge>
        )}
      </div>
      <p className="text-xs text-muted">
        {approval.stepName} · asked {format.relative(approval.createdAt)}
        {approval.dueAt && pending && <> · due {format.relative(approval.dueAt)}</>}
        {approval.decidedAt && (
          <>
            {' '}
            · decided {format.relative(approval.decidedAt)} by{' '}
            {userName(users.get(approval.decidedBy ?? ''), approval.decidedBy ?? '')}
          </>
        )}
      </p>
      {approval.comment && <p className="rounded bg-surface-muted px-2 py-1 text-[13px]">“{approval.comment}”</p>}
      {pending && (
        <div className="flex flex-wrap items-end gap-2">
          <Textarea
            aria-label="Comment"
            rows={1}
            placeholder="Comment (optional)"
            className="min-h-9 flex-1"
            value={comment}
            onChange={(e) => setComment(e.target.value)}
          />
          <Button disabled={decide.isPending} onClick={() => decide.mutate('rejected')}>
            <X /> Reject
          </Button>
          <Button variant="primary" disabled={decide.isPending} onClick={() => decide.mutate('approved')}>
            <Check /> Approve
          </Button>
        </div>
      )}
    </li>
  );
}
