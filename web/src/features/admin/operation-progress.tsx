import { jsonOf } from '@paperdotnet/client';
import { useQuery } from '@tanstack/react-query';
import { api } from '@/api/client';
import { Badge } from '@/components/ui/badge';

/** A long-running operation (reindex, import): its progress until it succeeds or fails. */
export function OperationProgress({ id, label }: { id: string; label: string }) {
  const { data } = useQuery({
    queryKey: ['operations', id],
    queryFn: async () => (await api.v10.operations.byId(id).get())!,
    refetchInterval: (query) =>
      query.state.data?.status === 'succeeded' || query.state.data?.status === 'failed' ? false : 1000,
  });
  const status = data?.status ?? 'notStarted';
  const percent = Math.round(data?.percentComplete ?? (status === 'succeeded' ? 100 : 0));
  const result = jsonOf(data?.result);
  return (
    <div role="status" aria-label={label} className="flex flex-col gap-1.5 rounded-md border p-3 text-[13px]">
      <div className="flex items-center gap-2">
        <span className="flex-1 font-medium">{label}</span>
        <Badge tone={status === 'succeeded' ? 'success' : status === 'failed' ? 'danger' : 'neutral'}>
          {status === 'notStarted' ? 'waiting' : status}
        </Badge>
      </div>
      <div className="h-1.5 overflow-hidden rounded-full bg-surface-muted">
        <div className="h-full bg-accent transition-all" style={{ width: `${percent}%` }} />
      </div>
      {data?.errorEscaped && <p className="text-xs text-danger">{data.errorEscaped}</p>}
      {result !== undefined && result !== null && (
        <pre className="max-h-40 overflow-auto rounded bg-surface-muted p-2 text-xs">
          {JSON.stringify(result, null, 2)}
        </pre>
      )}
    </div>
  );
}
