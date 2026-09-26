import type { ApprovalResponse } from '@paperdotnet/client';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Link } from '@tanstack/react-router';
import { Check, Stamp, X } from 'lucide-react';
import { toast } from 'sonner';
import { api } from '@/api/client';
import { pendingApprovalsQuery } from '@/api/queries';
import { Badge } from '@/components/ui/badge';
import { Button } from '@/components/ui/button';
import { Card, CardHeader, CardTitle } from '@/components/ui/card';
import { itemLink } from '@/features/lists/item-link';
import { useFormat } from '@/lib/preferences';

/** Approvals waiting for the user (EVT-08); hidden when there are none. */
export function ApprovalsCard() {
  const { data } = useQuery(pendingApprovalsQuery);
  if (!data?.length) return null;

  return (
    <Card>
      <CardHeader>
        <Stamp className="size-4 text-muted" />
        <CardTitle className="flex-1">Waiting for your approval</CardTitle>
        <Badge tone="accent">{data.length}</Badge>
      </CardHeader>
      <ul className="divide-y">
        {data.map((approval) => (
          <ApprovalRow key={approval.id} approval={approval} />
        ))}
      </ul>
    </Card>
  );
}

function ApprovalRow({ approval }: { approval: ApprovalResponse }) {
  const format = useFormat();
  const queryClient = useQueryClient();
  const decide = useMutation({
    mutationFn: (outcome: 'approved' | 'rejected') =>
      api.v10.me.approvals.byId(approval.id!).decision.post({ outcome }),
    onSuccess: (_, outcome) => {
      toast.success(outcome === 'approved' ? 'Approved' : 'Rejected');
      return queryClient.invalidateQueries({ queryKey: ['me', 'approvals'] });
    },
  });

  return (
    <li className="flex flex-wrap items-center gap-3 px-4 py-3">
      <div className="min-w-0 flex-1">
        {itemLink(approval) ? (
          <Link {...itemLink(approval)!} className="block truncate text-[13px] font-medium hover:underline">
            {approval.title}
          </Link>
        ) : (
          <p className="truncate text-[13px] font-medium">{approval.title}</p>
        )}
        <p className="text-xs text-muted">
          {approval.stepName}
          {approval.dueAt && <> · due {format.relative(approval.dueAt)}</>}
          {approval.escalated && <> · escalated</>}
        </p>
      </div>
      <div className="flex gap-2">
        <Button size="sm" disabled={decide.isPending} onClick={() => decide.mutate('rejected')}>
          <X /> Reject
        </Button>
        <Button size="sm" variant="primary" disabled={decide.isPending} onClick={() => decide.mutate('approved')}>
          <Check /> Approve
        </Button>
      </div>
    </li>
  );
}
