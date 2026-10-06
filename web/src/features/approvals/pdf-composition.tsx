import { fieldsOf } from '@paperdotnet/client';
import { useEffect, useState } from 'react';
import type { ApprovalReviewProps } from '@/extensibility/approval-reviews';
import { Button } from '@/components/ui/button';
import { Alert, Spinner } from '@/components/ui/feedback';
import { Select } from '@/components/ui/select';
import { useAuthedImage } from '@/lib/authed-image';

function ReviewPage({
  approvalId,
  page,
  count,
  zoom,
  onReady,
}: {
  approvalId: string;
  page: number;
  count: number;
  zoom: number;
  onReady: (value: boolean) => void;
}) {
  const root = `/v1.0/me/approvals/${approvalId}/review/content`;
  const original = useAuthedImage(`${root}/source-${page - 1}`);
  const candidate = useAuthedImage(`${root}/page-${page}`);
  const [loaded, setLoaded] = useState({ original: false, candidate: false });
  const [failed, setFailed] = useState(false);
  const ready = loaded.original && loaded.candidate && !original.failed && !candidate.failed && !failed;
  useEffect(() => {
    onReady(ready);
    return () => onReady(false);
  }, [ready, onReady]);
  return (
    <>
      {(original.failed || candidate.failed || failed) && (
        <Alert>This page could not be loaded. Refresh the review before deciding.</Alert>
      )}
      {(!original.url || !candidate.url) && <Spinner />}
      <div className="grid gap-4 md:grid-cols-2">
        <div className="overflow-auto">
          <p>
            Original page {page} of {count}
          </p>
          {original.url && (
            <img
              src={original.url}
              alt={`Original page ${page}`}
              style={{ width: `${zoom}%`, maxWidth: 'none' }}
              onLoad={() => setLoaded((value) => ({ ...value, original: true }))}
              onError={() => setFailed(true)}
            />
          )}
        </div>
        <div className="overflow-auto">
          <p>
            PDF page {page} of {count}
          </p>
          {candidate.url && (
            <img
              src={candidate.url}
              alt={`PDF page ${page}`}
              style={{ width: `${zoom}%`, maxWidth: 'none' }}
              onLoad={() => setLoaded((value) => ({ ...value, candidate: true }))}
              onError={() => setFailed(true)}
            />
          )}
        </div>
      </div>
    </>
  );
}

export function PdfComposition({ approvalId, review, onReady }: ApprovalReviewProps) {
  const data = fieldsOf({ fields: review.data });
  const count = Number(data.pageCount);
  const [page, setPage] = useState(1);
  const [zoom, setZoom] = useState(100);
  const pdf = useAuthedImage(`/v1.0/me/approvals/${approvalId}/review/content/candidate`);
  return (
    <div className="space-y-3">
      <p>
        {typeof data.description === 'string'
          ? data.description
          : `${String(data.name)} (${count} pages), replacing ${String(data.primaryName)}.`}
      </p>
      <div className="flex items-center gap-2">
        <Button disabled={page <= 1} onClick={() => setPage(page - 1)}>
          Previous page
        </Button>
        <span>
          Page {page} of {count}
        </span>
        <Button disabled={page >= count} onClick={() => setPage(page + 1)}>
          Next page
        </Button>
        <Select aria-label="Review zoom" value={zoom} onChange={(event) => setZoom(Number(event.target.value))}>
          {[50, 100, 150, 200].map((value) => (
            <option key={value} value={value}>
              {value}%
            </option>
          ))}
        </Select>
        {pdf.url && (
          <a href={pdf.url} download={String(data.name)}>
            Download PDF
          </a>
        )}
      </div>
      <ReviewPage key={page} approvalId={approvalId} page={page} count={count} zoom={zoom} onReady={onReady} />
    </div>
  );
}
