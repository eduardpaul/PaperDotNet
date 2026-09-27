import { downloadFile, type TemplateResult } from '@paperdotnet/client';
import { useInfiniteQuery, useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute } from '@tanstack/react-router';
import { useState } from 'react';
import { toast } from 'sonner';
import { api, client } from '@/api/client';
import { workspacesQuery } from '@/api/queries';
import { Button } from '@/components/ui/button';
import { Alert, Skeleton } from '@/components/ui/feedback';
import { Label } from '@/components/ui/input';
import { Checkbox, Select } from '@/components/ui/select';
import { useAdminAccess } from '@/features/admin/access';
import { userName, useUsers } from '@/features/fields/directory';
import { SettingsSection } from '@/features/settings/section';
import { problemMessage } from '@/lib/errors';
import { useFormat } from '@/lib/preferences';

export const Route = createFileRoute('/_app/admin/maintenance')({ component: Maintenance });

function Maintenance() {
  const admin = useAdminAccess();
  return (
    <>
      {admin.has('search.manage') && <Reindex />}
      {admin.has('extension.manage') && <Extensions />}
      {admin.has('template.read') && (
        <>
          <Provisioning canApply={admin.has('template.manage')} />
          <Portability canImport={admin.has('template.manage')} />
        </>
      )}
      {admin.has('audit.read') && <Audit />}
    </>
  );
}

function Reindex() {
  const run = useMutation({
    meta: { silent: true },
    mutationFn: () => api.v10.search.reindex.post(),
    onSuccess: () => toast.success('Reindex started. It runs in the background.'),
  });
  return (
    <SettingsSection
      title="Search index"
      description="Rebuild the index after a large import or a model change."
      actions={
        <Button variant="primary" disabled={run.isPending} onClick={() => run.mutate()}>
          Rebuild index
        </Button>
      }
    >
      {run.isError && <Alert>{problemMessage(run.error)}</Alert>}
    </SettingsSection>
  );
}

function Extensions() {
  const queryClient = useQueryClient();
  const { data, isPending } = useQuery({
    queryKey: ['extensions'],
    queryFn: async () => (await api.v10.extensions.get()) ?? [],
  });
  const toggle = useMutation({
    mutationFn: (extension: { id?: string | null; enabled?: boolean | null }) =>
      extension.enabled
        ? api.v10.extensions.byId(extension.id!).disable.post()
        : api.v10.extensions.byId(extension.id!).enable.post(),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['extensions'] }),
  });
  return (
    <SettingsSection title="Extensions" description="Turn an installed extension on or off for this organization.">
      {isPending ? (
        <Skeleton className="h-16" />
      ) : (
        <ul className="divide-y text-[13px]">
          {(data ?? []).map((extension) => (
            <li key={extension.id} className="flex items-center gap-2 py-2">
              <span className="min-w-0 flex-1">
                <span className="font-medium">{extension.name}</span>
                <span className="ml-2 text-muted">{extension.id}</span>
              </span>
              <Button size="sm" disabled={toggle.isPending} onClick={() => toggle.mutate(extension)}>
                {extension.enabled ? 'Disable' : 'Enable'}
              </Button>
            </li>
          ))}
        </ul>
      )}
      {toggle.isError && <Alert className="mt-3">{problemMessage(toggle.error)}</Alert>}
    </SettingsSection>
  );
}

function Provisioning({ canApply }: { canApply: boolean }) {
  const { data: workspaces } = useQuery(workspacesQuery);
  const [workspaceId, setWorkspaceId] = useState('');
  const [includeContent, setIncludeContent] = useState(false);
  const [file, setFile] = useState<File>();
  const [plan, setPlan] = useState<TemplateResult>();
  const download = useMutation({
    meta: { silent: true },
    mutationFn: async () => {
      const bytes = await api.v10.provisioning.exportEscaped.get({
        queryParameters: {
          workspaceId: workspaceId || undefined,
          includeContent: includeContent || undefined,
        },
      });
      if (!bytes) throw new Error('The export was empty.');
      const zip = includeContent;
      const blob = new Blob([bytes], { type: zip ? 'application/zip' : 'application/xml' });
      const url = URL.createObjectURL(blob);
      const name = workspaceId
        ? zip
          ? 'workspace-package.zip'
          : 'workspace-template.xml'
        : zip
          ? 'tenant-package.zip'
          : 'tenant-template.xml';
      Object.assign(document.createElement('a'), { href: url, download: name }).click();
      setTimeout(() => URL.revokeObjectURL(url), 10_000);
    },
    onSuccess: () => toast.success('Template downloaded.'),
  });
  const apply = useMutation({
    meta: { silent: true },
    mutationFn: async (dryRun: boolean) => {
      if (!file) throw new Error('Choose a template first.');
      const zip = file.name.toLowerCase().endsWith('.zip') || file.type === 'application/zip';
      return api.v10.provisioning.apply.post(await file.arrayBuffer(), zip ? 'application/zip' : 'application/xml', {
        queryParameters: { dryRun, workspaceId: workspaceId || undefined },
      });
    },
    onSuccess: (result, dryRun) => {
      setPlan(result);
      toast.success(dryRun ? 'Dry run finished. Nothing was written.' : 'Template applied.');
    },
  });

  return (
    <SettingsSection
      title="Provisioning"
      description="Download this organization, or one workspace, as a template. Apply a template again elsewhere; a dry run writes nothing."
      actions={
        <Button variant="primary" disabled={download.isPending} onClick={() => download.mutate()}>
          Download template
        </Button>
      }
    >
      {download.isError && <Alert>{problemMessage(download.error)}</Alert>}
      <div className="grid gap-3 sm:grid-cols-2">
        <div className="flex flex-col gap-1.5">
          <Label htmlFor="template-workspace">Workspace</Label>
          <Select id="template-workspace" value={workspaceId} onChange={(e) => setWorkspaceId(e.target.value)}>
            <option value="">Whole organization</option>
            {(workspaces ?? [])
              .filter((workspace) => !workspace.isPersonal)
              .map((workspace) => (
                <option key={workspace.id} value={workspace.id!}>
                  {workspace.name}
                </option>
              ))}
          </Select>
        </div>
        <label className="flex items-center gap-2 self-end pb-2 text-[13px]">
          <Checkbox checked={includeContent} onChange={(e) => setIncludeContent(e.target.checked)} />
          Include items and files (a package)
        </label>
      </div>
      {canApply && (
        <div className="mt-3 flex flex-col gap-2">
          <label className="inline-flex cursor-pointer items-center gap-2 text-[13px]">
            <input
              type="file"
              accept=".xml,.zip,application/xml,application/zip,text/xml"
              aria-label="Template file"
              className="text-xs"
              onChange={(e) => {
                setFile(e.target.files?.[0]);
                setPlan(undefined);
              }}
            />
          </label>
          <div className="flex gap-2">
            <Button size="sm" disabled={!file || apply.isPending} onClick={() => apply.mutate(true)}>
              Check only
            </Button>
            <Button size="sm" variant="primary" disabled={!file || apply.isPending} onClick={() => apply.mutate(false)}>
              Apply template
            </Button>
          </div>
          {apply.isError && <Alert>{problemMessage(apply.error)}</Alert>}
          {!!plan?.changes?.length && (
            <ul className="text-[13px] text-muted">
              {plan.changes.slice(0, 12).map((change, index) => (
                <li key={`${change.kind}-${change.name}-${index}`}>
                  {change.action} {change.kind} {change.name}
                  {change.detail ? ` — ${change.detail}` : ''}
                </li>
              ))}
              {plan.changes.length > 12 && <li>And {plan.changes.length - 12} more.</li>}
            </ul>
          )}
          {!!plan?.warnings?.length && (
            <Alert>
              {plan.warnings.map((warning) => (
                <span key={warning} className="block">
                  {warning}
                </span>
              ))}
            </Alert>
          )}
        </div>
      )}
    </SettingsSection>
  );
}

function Portability({ canImport }: { canImport: boolean }) {
  const queryClient = useQueryClient();
  const exports = useQuery({
    queryKey: ['portability', 'exports'],
    queryFn: async () => (await api.v10.portability.exports.get()) ?? [],
  });
  const start = useMutation({
    meta: { silent: true },
    mutationFn: () => api.v10.portability.exports.post({}),
    onSuccess: async () => {
      toast.success('Export started. The package is ready to download when it finishes.');
      await queryClient.invalidateQueries({ queryKey: ['portability', 'exports'] });
    },
  });
  const upload = useMutation({
    meta: { silent: true },
    mutationFn: async (file: File) => api.v10.portability.imports.post(await file.arrayBuffer()),
    onSuccess: () => toast.success('Import started.'),
  });
  const download = async (id: string) => {
    const { blob, fileName } = await downloadFile(client, `/v1.0/portability/exports/${id}/package`);
    const url = URL.createObjectURL(blob);
    Object.assign(document.createElement('a'), { href: url, download: fileName ?? 'export.zip' }).click();
    setTimeout(() => URL.revokeObjectURL(url), 10_000);
  };

  return (
    <SettingsSection
      title="Export and import"
      description="A package of this organization's configuration and content. Download it after the export finishes."
      actions={
        <Button variant="primary" disabled={start.isPending} onClick={() => start.mutate()}>
          Start export
        </Button>
      }
    >
      {start.isError && <Alert>{problemMessage(start.error)}</Alert>}
      <ul className="divide-y text-[13px]">
        {(exports.data ?? []).map((item) => (
          <li key={item.id} className="flex items-center gap-2 py-2">
            <span className="flex-1">{item.ready ? 'Ready' : 'Working'}</span>
            {item.ready && (
              <Button size="sm" onClick={() => void download(item.id!)}>
                Download
              </Button>
            )}
          </li>
        ))}
      </ul>
      {canImport && (
        <label className="mt-3 inline-flex cursor-pointer items-center gap-2 text-[13px]">
          <input
            type="file"
            accept=".zip,application/zip"
            className="text-xs"
            onChange={(e) => {
              const file = e.target.files?.[0];
              if (file) upload.mutate(file);
            }}
          />
          {upload.isPending ? 'Importing…' : 'Import a package'}
        </label>
      )}
      {upload.isError && <Alert className="mt-3">{problemMessage(upload.error)}</Alert>}
    </SettingsSection>
  );
}

function Audit() {
  const format = useFormat();
  const users = useUsers();
  const [expanded, setExpanded] = useState(false);
  const log = useInfiniteQuery({
    queryKey: ['auditLog'],
    initialPageParam: undefined as string | undefined,
    queryFn: async ({ pageParam }) =>
      (pageParam
        ? await api.v10.auditLog.withUrl(pageParam).get()
        : await api.v10.auditLog.get({ queryParameters: { top: expanded ? 50 : 20 } })) ?? { value: [] },
    getNextPageParam: (last) => last.odataNextLink ?? undefined,
  });
  const rows = log.data?.pages.flatMap((page) => page.value ?? []) ?? [];
  return (
    <SettingsSection title="Audit log" description="Who changed what. Newest first.">
      {log.isPending ? (
        <Skeleton className="h-16" />
      ) : (
        <ul className="divide-y text-[13px]">
          {rows.map((entry) => (
            <li key={entry.id} className="flex flex-wrap gap-x-2 py-2">
              <span className="text-muted">{format.dateTime(entry.at)}</span>
              <span>{userName(users.get(entry.userId ?? ''), entry.userId ?? '')}</span>
              <span className="font-medium">{entry.action}</span>
              <span className="text-muted">
                {entry.entityType}
                {entry.entityId ? ` ${entry.entityId.slice(0, 8)}` : ''}
              </span>
            </li>
          ))}
        </ul>
      )}
      {log.hasNextPage && (
        <Button
          className="mt-3"
          size="sm"
          disabled={log.isFetchingNextPage}
          onClick={() => {
            setExpanded(true);
            void log.fetchNextPage();
          }}
        >
          Load more
        </Button>
      )}
    </SettingsSection>
  );
}
