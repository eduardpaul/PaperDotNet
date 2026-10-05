import { fieldsOf, type ApprovalResponse } from '@paperdotnet/client';
import type { RJSFSchema } from '@rjsf/utils';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { api } from '@/api/client';
import { keys } from '@/api/keys';
import { Button } from '@/components/ui/button';
import { Dialog, DialogContent, DialogTitle } from '@/components/ui/dialog';
import { Alert, Spinner } from '@/components/ui/feedback';
import { Textarea } from '@/components/ui/input';
import { SchemaForm } from '@/features/workflows/schema-form';
import { approvalReviewRenderers } from '@/extensibility/approval-reviews';

export function ReviewDialog({
  approval,
  busy,
  comment,
  onComment,
  onDecide,
  error,
}: {
  approval: ApprovalResponse;
  busy: boolean;
  comment: string;
  onComment: (value: string) => void;
  onDecide: (outcome: 'approved' | 'rejected', input?: Record<string, unknown>) => void;
  error?: string;
}) {
  const [open, setOpen] = useState(false);
  const [ready, setReady] = useState(false);
  const queryClient = useQueryClient();
  const query = useQuery({
    queryKey: keys.approvalReview(approval.id!),
    enabled: open,
    queryFn: () => api.v10.me.approvals.byId(approval.id!).review.get(),
    staleTime: 0,
    retry: false,
  });
  const review = query.data;
  const renderer = review?.renderer ? approvalReviewRenderers[review.renderer] : undefined;
  const Component = renderer?.component;
  const reviewData = fieldsOf({ fields: review?.data });
  const pending = approval.status === 'pending';
  const canDecide = ready && review?.canDecide && !query.isError && !query.isFetching && !busy;
  const decisionControls = (
    <div className="sticky bottom-0 flex flex-wrap gap-2 border-t bg-surface pt-3">
      <Textarea
        aria-label="Review comment"
        rows={1}
        placeholder="Comment (optional)"
        value={comment}
        onChange={(event) => onComment(event.target.value)}
      />
      <Button
        onClick={() => {
          setReady(false);
          void queryClient.invalidateQueries({
            queryKey: ['blob', `/v1.0/me/approvals/${approval.id}/review/content/source`],
          });
          void queryClient.invalidateQueries({
            queryKey: ['blob', `/v1.0/me/approvals/${approval.id}/review/content/candidate`],
          });
          void query.refetch();
        }}
      >
        Refresh review
      </Button>
      <Button
        disabled={!canDecide}
        type={approval.inputSchema ? 'submit' : 'button'}
        value="rejected"
        onClick={approval.inputSchema ? undefined : () => onDecide('rejected')}
      >
        {typeof reviewData.rejectedLabel === 'string'
          ? reviewData.rejectedLabel
          : (renderer?.rejectedLabel ?? 'Reject')}
      </Button>
      <Button
        variant="primary"
        disabled={!canDecide}
        type={approval.inputSchema ? 'submit' : 'button'}
        value="approved"
        onClick={approval.inputSchema ? undefined : () => onDecide('approved')}
      >
        {typeof reviewData.approvedLabel === 'string'
          ? reviewData.approvedLabel
          : (renderer?.approvedLabel ?? 'Approve')}
      </Button>
    </div>
  );
  return (
    <>
      <Button onClick={() => setOpen(true)}>Review file</Button>
      <Dialog open={open} onOpenChange={setOpen}>
        <DialogContent className="top-[4vh] max-h-[92vh] w-[96vw] max-w-[96vw] space-y-3 overflow-y-auto p-5">
          <DialogTitle>{approval.title}</DialogTitle>
          {query.isPending && <Spinner />}
          {query.isError && <Alert tone="danger">This review is unavailable. Refresh to try again.</Alert>}
          {review?.reason && <Alert>{review.reason}</Alert>}
          {Component && review && (
            <Component key={query.dataUpdatedAt} approvalId={approval.id!} review={review} onReady={setReady} />
          )}
          {review && !Component && <Alert>This review type is not available in this app build.</Alert>}
          {error && <Alert tone="danger">{error}</Alert>}
          {approval.inputSchema ? (
            <SchemaForm
              schema={fieldsOf({ fields: approval.inputSchema }) as RJSFSchema}
              idPrefix={`approval-${approval.id}`}
              disabled={!pending || busy}
              formData={approval.inputs ? fieldsOf({ fields: approval.inputs }) : undefined}
              onSubmit={(input, outcome) => {
                if (canDecide && (outcome === 'approved' || outcome === 'rejected')) onDecide(outcome, input);
              }}
            >
              {pending ? decisionControls : <></>}
            </SchemaForm>
          ) : pending ? (
            decisionControls
          ) : null}
        </DialogContent>
      </Dialog>
    </>
  );
}
