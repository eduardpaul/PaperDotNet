import type { AuditEntryResponse } from '@paperdotnet/client';
import { useInfiniteQuery } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { ScrollText } from 'lucide-react';
import { api } from '@/api/client';
import { Badge } from '@/components/ui/badge';
import { Button } from '@/components/ui/button';
import { EmptyState, Skeleton } from '@/components/ui/feedback';
import { Input } from '@/components/ui/input';
import { Select } from '@/components/ui/select';
import { userName, useUsers } from '@/features/fields/directory';
import { SettingsSection } from '@/features/settings/section';
import { useFormat } from '@/lib/preferences';

interface AuditSearch {
  entityType?: string;
  userId?: string;
  from?: string;
  to?: string;
}

export const Route = createFileRoute('/_app/admin/audit')({
  validateSearch: (search: Record<string, unknown>): AuditSearch => {
    const text = (v: unknown) => (typeof v === 'string' && v ? v : undefined);
    return {
      entityType: text(search.entityType),
      userId: text(search.userId),
      from: text(search.from),
      to: text(search.to),
    };
  },
  component: Audit,
});

const tones: Record<string, 'neutral' | 'success' | 'warning' | 'danger'> = {
  created: 'success',
  updated: 'neutral',
  deleted: 'danger',
  restored: 'warning',
  purged: 'danger',
};

/** Who changed what and when, across modules (LST-14). */
function Audit() {
  const search = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const format = useFormat();
  const users = useUsers();
  const setSearch = (patch: Partial<AuditSearch>) => void navigate({ search: { ...search, ...patch } });
  const entries = useInfiniteQuery({
    queryKey: ['auditLog', search],
    initialPageParam: undefined as string | undefined,
    queryFn: async ({ pageParam }) =>
      (pageParam
        ? await api.v10.auditLog.withUrl(pageParam).get()
        : await api.v10.auditLog.get({
            queryParameters: {
              entityType: search.entityType,
              userId: search.userId,
              from: search.from ? new Date(`${search.from}T00:00:00`) : undefined,
              to: search.to ? new Date(`${search.to}T23:59:59`) : undefined,
              top: 50,
            },
          })) ?? { value: [] },
    getNextPageParam: (last) => last.odataNextLink ?? undefined,
  });
  const rows: AuditEntryResponse[] = entries.data?.pages.flatMap((p) => p.value ?? []) ?? [];
  return (
    <SettingsSection title="Audit log" description="Every change, newest first." className="px-0 pb-0">
      <div className="flex flex-wrap gap-2 px-5 pb-3">
        <Input
          aria-label="What"
          placeholder="What (e.g. identity.User)"
          className="w-48"
          defaultValue={search.entityType ?? ''}
          onBlur={(e) => setSearch({ entityType: e.target.value.trim() || undefined })}
        />
        <Select
          aria-label="Who"
          className="w-48"
          value={search.userId ?? ''}
          onChange={(e) => setSearch({ userId: e.target.value || undefined })}
        >
          <option value="">Anyone</option>
          {[...users.values()].map((u) => (
            <option key={u.id} value={u.id!}>
              {userName(u, u.id!)}
            </option>
          ))}
        </Select>
        <Input
          aria-label="From"
          type="date"
          className="w-40"
          value={search.from ?? ''}
          onChange={(e) => setSearch({ from: e.target.value || undefined })}
        />
        <Input
          aria-label="To"
          type="date"
          className="w-40"
          value={search.to ?? ''}
          onChange={(e) => setSearch({ to: e.target.value || undefined })}
        />
      </div>
      {entries.isPending ? (
        <Skeleton className="mx-5 mb-5 h-40" />
      ) : rows.length ? (
        <div className="overflow-x-auto border-t">
          <table className="w-full text-[13px]">
            <thead>
              <tr className="bg-surface-muted/40 text-left text-xs text-muted">
                <th className="px-5 py-2 font-medium">When</th>
                <th className="px-3 py-2 font-medium">Who</th>
                <th className="px-3 py-2 font-medium">What</th>
                <th className="px-5 py-2 font-medium">Changed</th>
              </tr>
            </thead>
            <tbody className="divide-y">
              {rows.map((entry) => (
                <tr key={entry.id}>
                  <td className="px-5 py-2 whitespace-nowrap text-muted" title={format.dateTime(entry.at)}>
                    {format.dateTime(entry.at)}
                  </td>
                  <td className="px-3 py-2">
                    {entry.userId ? userName(users.get(entry.userId), entry.userId) : 'System'}
                  </td>
                  <td className="px-3 py-2">
                    <Badge tone={tones[entry.action ?? ''] ?? 'neutral'}>{entry.action}</Badge> {entry.entityType}{' '}
                    <code className="text-xs text-muted">{entry.entityId?.slice(0, 8)}</code>
                  </td>
                  <td className="max-w-72 truncate px-5 py-2 text-xs text-muted">{entry.properties?.join(', ')}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      ) : (
        <EmptyState icon={ScrollText} title="Nothing recorded for these filters" className="border-t py-8" />
      )}
      {entries.hasNextPage && (
        <div className="border-t p-3 text-center">
          <Button size="sm" disabled={entries.isFetchingNextPage} onClick={() => void entries.fetchNextPage()}>
            Show more
          </Button>
        </div>
      )}
    </SettingsSection>
  );
}
