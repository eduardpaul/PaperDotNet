import { fieldsOf } from '@paperdotnet/client';
import { useEffect, useRef, useState } from 'react';
import type { ApprovalReviewProps } from '@/extensibility/approval-reviews';
import { Button } from '@/components/ui/button';
import { Alert, Spinner } from '@/components/ui/feedback';
import { Checkbox } from '@/components/ui/select';
import { useAuthedImage } from '@/lib/authed-image';
import { useFormat } from '@/lib/preferences';

/** Native pixels are displayed directly; list previews and their JPEG recompression are never used here. */
export function ImageComparison({ approvalId, review, onReady }: ApprovalReviewProps) {
  const data = fieldsOf({ fields: review.data });
  const source = useAuthedImage(`/v1.0/me/approvals/${approvalId}/review/content/source`);
  const candidate = useAuthedImage(`/v1.0/me/approvals/${approvalId}/review/content/candidate`);
  const [loaded, setLoaded] = useState({ source: false, candidate: false });
  const [failed, setFailed] = useState(false);
  const [linked, setLinked] = useState(false);
  const panes = useRef<(HTMLDivElement | null)[]>([null, null]);
  const format = useFormat();
  const ready = loaded.source && loaded.candidate && !source.failed && !candidate.failed && !failed;
  useEffect(() => {
    onReady(ready);
    return () => onReady(false);
  }, [ready, onReady]);

  const sync = (index: number) => {
    if (!linked) return;
    const from = panes.current[index];
    const to = panes.current[1 - index];
    if (!from || !to) return;
    const x = from.scrollLeft / Math.max(1, from.scrollWidth - from.clientWidth);
    const y = from.scrollTop / Math.max(1, from.scrollHeight - from.clientHeight);
    const left = x * Math.max(0, to.scrollWidth - to.clientWidth);
    const top = y * Math.max(0, to.scrollHeight - to.clientHeight);
    if (Math.abs(to.scrollLeft - left) > 1 || Math.abs(to.scrollTop - top) > 1) to.scrollTo(left, top);
  };

  return (
    <div className="space-y-3">
      <p className="text-sm">
        {format.fileSize(Number(data.sourceBytes))} → {format.fileSize(Number(data.bytes))}
        {' · '}
        {format.number(Number(data.savingsPercent), { maximumFractionDigits: 1 })}% smaller
        {' · '}text {format.number(Number(data.smallestTextHeight))}px
        {' · '}scale {format.number(Number(data.scale), { maximumFractionDigits: 3 })}
      </p>
      <label className="flex items-center gap-2 text-sm">
        <Checkbox checked={linked} onChange={(event) => setLinked(event.target.checked)} />
        Link pan positions
      </label>
      {(source.failed || candidate.failed || failed) && (
        <Alert tone="danger">Review images are unavailable. Refresh the review before deciding.</Alert>
      )}
      <div className="grid min-w-0 gap-3 md:grid-cols-2">
        <ImagePane
          title="Original"
          name={String(data.sourceName)}
          mediaType={String(data.sourceMediaType)}
          width={Number(data.sourceWidth)}
          height={Number(data.sourceHeight)}
          url={source.url}
          paneRef={(element) => {
            panes.current[0] = element;
          }}
          onScroll={() => sync(0)}
          onLoad={() => setLoaded((state) => ({ ...state, source: true }))}
          onError={() => setFailed(true)}
        />
        <ImagePane
          title="Optimized"
          name={String(data.name)}
          mediaType={String(data.mediaType)}
          width={Number(data.width)}
          height={Number(data.height)}
          url={candidate.url}
          paneRef={(element) => {
            panes.current[1] = element;
          }}
          onScroll={() => sync(1)}
          onLoad={() => setLoaded((state) => ({ ...state, candidate: true }))}
          onError={() => setFailed(true)}
        />
      </div>
    </div>
  );
}

function ImagePane({
  title,
  name,
  mediaType,
  width,
  height,
  url,
  paneRef,
  onScroll,
  onLoad,
  onError,
}: {
  title: string;
  name: string;
  mediaType: string;
  width: number;
  height: number;
  url?: string;
  paneRef: (element: HTMLDivElement | null) => void;
  onScroll: () => void;
  onLoad: () => void;
  onError: () => void;
}) {
  const [fit, setFit] = useState(false);
  const [size, setSize] = useState({ width: 1, height: 1 });
  const pane = useRef<HTMLDivElement>(null);
  const drag = useRef<{ x: number; y: number; left: number; top: number } | null>(null);
  useEffect(() => {
    const element = pane.current;
    if (!element) return;
    const observer = new ResizeObserver(() => setSize({ width: element.clientWidth, height: element.clientHeight }));
    observer.observe(element);
    return () => observer.disconnect();
  }, []);
  const scale = fit ? Math.min(1, size.width / width, size.height / height) : 1;
  return (
    <section className="min-w-0 space-y-2" aria-label={title}>
      <div className="flex items-center gap-2">
        <h3 className="flex-1 font-semibold">{title}</h3>
        <Button size="sm" onClick={() => setFit(true)} aria-pressed={fit}>
          Fit {title.toLowerCase()}
        </Button>
        <Button size="sm" onClick={() => setFit(false)} aria-pressed={!fit}>
          100% {title.toLowerCase()}
        </Button>
      </div>
      <p className="truncate text-xs text-muted" title={name}>
        {name} · {width} × {height} · {mediaType}
      </p>
      <div
        ref={(element) => {
          pane.current = element;
          paneRef(element);
        }}
        tabIndex={0}
        aria-label={`Pan ${title.toLowerCase()}`}
        onScroll={onScroll}
        className="h-[55vh] cursor-grab overflow-auto rounded border bg-surface-muted active:cursor-grabbing"
        onPointerDown={(event) => {
          if (event.button !== 0) return;
          const element = event.currentTarget;
          drag.current = { x: event.clientX, y: event.clientY, left: element.scrollLeft, top: element.scrollTop };
          element.setPointerCapture(event.pointerId);
        }}
        onPointerMove={(event) => {
          if (!drag.current) return;
          event.currentTarget.scrollTo(
            drag.current.left - event.clientX + drag.current.x,
            drag.current.top - event.clientY + drag.current.y,
          );
        }}
        onPointerUp={() => {
          drag.current = null;
        }}
        onPointerCancel={() => {
          drag.current = null;
        }}
      >
        {url ? (
          <img
            alt={`${title} document`}
            src={url}
            draggable={false}
            onLoad={onLoad}
            onError={onError}
            className="block max-w-none"
            style={{ width: width * scale, height: height * scale }}
          />
        ) : (
          <Spinner className="m-4" />
        )}
      </div>
    </section>
  );
}
