import type { RunResponse, RunStatus } from '@paperdotnet/client';
import { jsonOf } from '@paperdotnet/client';
import { useInfiniteQuery, useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute, Link, useNavigate } from '@tanstack/react-router';
import { ChevronRight, History } from 'lucide-react';
import { useState } from 'react';
import { toast } from 'sonner';
import { keys } from '@/api/keys';
import { Badge } from '@/components/ui/badge';
import { Button } from '@/components/ui/button';
import { EmptyState, Skeleton } from '@/components/ui/feedback';
import { Select } from '@/components/ui/select';
import { automationsQuery } from '@/features/automations/queries';
import { userName, useUsers } from '@/features/fields/directory';
import { itemLink } from '@/features/lists/item-link';
import { SettingsSection } from '@/features/settings/section';
import { workspaceBuilder, workspaceQuery } from '@/features/workspaces/queries';
import { useFormat } from '@/lib/preferences';
import { cn } from '@/lib/utils';

const statuses: RunStatus[] = ['running', 'waiting', 'completed', 'failed', 'cancelled'];
const tones: Record<RunStatus, 'neutral' | 'warning' | 'success' | 'danger'> = {
  running: 'neutral',
  waiting: 'warning',
  completed: 'success',
  failed: 'danger',
  cancelled: 'neutral',
};

interface RunsSearch {
  automation?: string;
  status?: RunStatus;
}

export const Route = createFileRoute('/_app/w/$workspaceId/settings/runs')({
  validateSearch: (search: Record<string, unknown>): RunsSearch => ({
    automation: typeof search.automation === 'string' ? search.automation : undefined,
    status: statuses.includes(search.status as RunStatus) ? (search.status as RunStatus) : undefined,
  }),
  component: Runs,
});

/** What the automations did (EVT-07…09): status, approval outcomes, the log and errors; cancel what still runs. */
function Runs() {
  const { workspaceId } = Route.useParams();
  const search = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const { data: automations } = useQuery(automationsQuery(workspaceId));
  const runs = useInfiniteQuery({
    queryKey: [...keys.workspace(workspaceId), 'runs', search],
    initialPageParam: undefined as string | undefined,
    queryFn: async ({ pageParam }) => {
      const builder = workspaceBuilder(workspaceId).automations.runs;
      return (
        (pageParam
          ? await builder.withUrl(pageParam).get()
          : await builder.get({
              queryParameters: { automationId: search.automation, status: search.status, top: 50 },
            })) ?? { value: [] }
      );
    },
    getNextPageParam: (last) => last.odataNextLink ?? undefined,
  });
  const items = runs.data?.pages.flatMap((p) => p.value ?? []) ?? [];

  return (
    <SettingsSection
      title="Runs"
      description="Every time an automation started, newest first. Finished runs are kept for 30 days."
      className="px-0 pb-0"
    >
      <div className="flex flex-wrap gap-2 px-5 pb-3">
        <Select
          aria-label="Automation"
          className="w-56"
          value={search.automation ?? ''}
          onChange={(e) => void navigate({ search: { ...search, automation: e.target.value || undefined } })}
        >
          <option value="">All automations</option>
          {automations?.map((a) => (
            <option key={a.id} value={a.id!}>
              {a.name}
            </option>
          ))}
        </Select>
        <Select
          aria-label="Status"
          className="w-40"
          value={search.status ?? ''}
          onChange={(e) =>
            void navigate({ search: { ...search, status: (e.target.value || undefined) as RunStatus | undefined } })
          }
        >
          <option value="">Any status</option>
          {statuses.map((s) => (
            <option key={s} value={s}>
              {s[0]!.toUpperCase() + s.slice(1)}
            </option>
          ))}
        </Select>
      </div>
      {runs.isPending ? (
        <Skeleton className="mx-5 mb-5 h-24" />
      ) : items.length ? (
        <ul className="divide-y border-t">
          {items.map((run) => (
            <RunRow key={run.id} run={run} workspaceId={workspaceId} />
          ))}
        </ul>
      ) : (
        <EmptyState icon={History} title="No runs" className="border-t py-8" />
      )}
      {runs.hasNextPage && (
        <div className="border-t p-3 text-center">
          <Button size="sm" disabled={runs.isFetchingNextPage} onClick={() => void runs.fetchNextPage()}>
            Show more
          </Button>
        </div>
      )}
    </SettingsSection>
  );
}

function RunRow({ run, workspaceId }: { run: RunResponse; workspaceId: string }) {
  const format = useFormat();
  const users = useUsers();
  const queryClient = useQueryClient();
  const { data: workspace } = useQuery(workspaceQuery(workspaceId));
  const [open, setOpen] = useState(false);
  const cancel = useMutation({
    mutationFn: () => workspaceBuilder(workspaceId).automations.runs.byId(run.id!).cancel.post(),
    onSuccess: async () => {
      toast.success('Run cancelled.');
      await queryClient.invalidateQueries({ queryKey: [...keys.workspace(workspaceId), 'runs'] });
    },
  });
  const status = run.status ?? 'running';
  const log = (jsonOf(run.log) as { at?: string; message?: string }[] | undefined) ?? [];
  const outcomes = Object.entries(run.outcomes?.additionalData ?? {});
  const link = itemLink(run);
  const active = status === 'running' || status === 'waiting';

  return (
    <li>
      <button
        type="button"
        aria-expanded={open}
        onClick={() => setOpen(!open)}
        className="flex w-full items-center gap-3 px-5 py-2.5 text-left hover:bg-surface-muted/50"
      >
        <ChevronRight className={cn('size-4 text-muted transition-transform', open && 'rotate-90')} />
        <Badge tone={tones[status]}>{status}</Badge>
        <span className="min-w-0 flex-1 truncate text-[13px] font-medium">
          {run.automation ?? 'Deleted automation'}
        </span>
        <span className="text-xs text-muted" title={format.dateTime(run.startedAt)}>
          {format.relative(run.startedAt)}
        </span>
      </button>
      {open && (
        <div className="flex flex-col gap-3 bg-surface-muted/30 px-5 py-3 pl-12 text-[13px]">
          <dl className="grid grid-cols-[8rem_1fr] gap-x-3 gap-y-1 text-xs">
            <dt className="text-muted">Started</dt>
            <dd>
              {format.dateTime(run.startedAt)}
              {run.startedBy && ` by ${userName(users.get(run.startedBy), run.startedBy)}`}
            </dd>
            {run.completedAt && (
              <>
                <dt className="text-muted">Finished</dt>
                <dd>{format.dateTime(run.completedAt)}</dd>
              </>
            )}
            <dt className="text-muted">Version</dt>
            <dd>{run.automationVersion}</dd>
            {link && (
              <>
                <dt className="text-muted">Item</dt>
                <dd>
                  <Link {...link} className="text-accent hover:underline">
                    Open the item
                  </Link>
                </dd>
              </>
            )}
            {outcomes.map(([step, outcome]) => (
              <div key={step} className="contents">
                <dt className="text-muted">{step}</dt>
                <dd>{String(outcome)}</dd>
              </div>
            ))}
          </dl>
          {run.errorEscaped && (
            <p className="rounded-md bg-danger-soft px-3 py-2 text-xs text-danger">{run.errorEscaped}</p>
          )}
          {log.length > 0 && (
            <ol className="flex flex-col gap-1 border-l pl-3 text-xs">
              {log.map((entry, i) => (
                <li key={i}>
                  <span className="text-muted">{entry.at ? format.time(entry.at) : ''}</span> {entry.message}
                </li>
              ))}
            </ol>
          )}
          {active && workspace?.access === 'manage' && (
            <Button size="sm" className="self-start" disabled={cancel.isPending} onClick={() => cancel.mutate()}>
              Cancel run
            </Button>
          )}
        </div>
      )}
    </li>
  );
}
