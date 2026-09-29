import type { TemplateResult } from '@paperdotnet/client';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute } from '@tanstack/react-router';
import { Download, FileUp, RefreshCw, Trash2 } from 'lucide-react';
import { useState } from 'react';
import { toast } from 'sonner';
import { api } from '@/api/client';
import { workspacesQuery } from '@/api/queries';
import { Badge } from '@/components/ui/badge';
import { Button } from '@/components/ui/button';
import { Alert } from '@/components/ui/feedback';
import { Input } from '@/components/ui/input';
import { Checkbox, Select } from '@/components/ui/select';
import { useHasScope } from '@/extensibility/admin';
import { OperationProgress } from '@/features/admin/operation-progress';
import { SettingRow, SettingsSection } from '@/features/settings/section';
import { saveBytes } from '@/lib/download';
import { problemMessage } from '@/lib/errors';
import { useFormat } from '@/lib/preferences';

export const Route = createFileRoute('/_app/admin/maintenance')({ component: Maintenance });

function Maintenance() {
  const canSearch = useHasScope('search.manage');
  const canRead = useHasScope('template.read');
  const canApply = useHasScope('template.manage');
  return (
    <>
      {canSearch && <Reindex />}
      {canRead && <Templates canApply={canApply} />}
      {canRead && <ExportImport canImport={canApply} />}
    </>
  );
}

/** Rebuilds the search index from the data (SRC-10), e.g. after restoring a backup. */
function Reindex() {
  const [operation, setOperation] = useState<string>();
  const start = useMutation({
    mutationFn: async () => (await api.v10.search.reindex.post())!,
    onSuccess: (result) => setOperation(result.id ?? undefined),
  });
  return (
    <SettingsSection
      title="Search index"
      description="Rebuild it when search misses things, for example after restoring a backup. Search keeps working meanwhile."
      actions={
        <Button disabled={start.isPending} onClick={() => start.mutate()}>
          <RefreshCw /> Rebuild the index
        </Button>
      }
    >
      {operation && <OperationProgress id={operation} label="Rebuilding the search index" />}
    </SettingsSection>
  );
}

function WorkspaceSelect({
  id,
  value,
  onChange,
  all,
}: {
  id: string;
  value: string;
  onChange: (v: string) => void;
  all: string;
}) {
  const { data: workspaces } = useQuery(workspacesQuery);
  return (
    <Select id={id} value={value} onChange={(e) => onChange(e.target.value)}>
      <option value="">{all}</option>
      {(workspaces ?? [])
        .filter((w) => !w.isPersonal)
        .map((w) => (
          <option key={w.id} value={w.id!}>
            {w.name}
          </option>
        ))}
    </Select>
  );
}

/** Templates (PRV-01…04): the configuration as portable XML, applied here or to another installation. */
function Templates({ canApply }: { canApply: boolean }) {
  const [workspaceId, setWorkspaceId] = useState('');
  const [withContent, setWithContent] = useState(false);
  const [file, setFile] = useState<File>();
  const [targetWorkspace, setTargetWorkspace] = useState('');
  const [result, setResult] = useState<TemplateResult>();
  const exportTemplate = useMutation({
    mutationFn: async () =>
      (await api.v10.provisioning.exportEscaped.get({
        queryParameters: { workspaceId: workspaceId || undefined, includeContent: withContent || undefined },
      }))!,
    onSuccess: (bytes) =>
      withContent
        ? saveBytes(bytes, 'paperdotnet-template.zip', 'application/zip')
        : saveBytes(bytes, 'paperdotnet-template.xml', 'application/xml'),
  });
  const apply = useMutation({
    meta: { silent: true },
    mutationFn: async (dryRun: boolean) => {
      const zip = file!.name.toLowerCase().endsWith('.zip');
      return (await api.v10.provisioning.apply.post(
        await file!.arrayBuffer(),
        zip ? 'application/zip' : 'application/xml',
        {
          queryParameters: { dryRun, workspaceId: targetWorkspace || undefined },
        },
      ))!;
    },
    onSuccess: (r) => {
      setResult(r);
      if (!r.dryRun) toast.success('Template applied.');
    },
  });
  return (
    <SettingsSection
      title="Templates"
      description="Workspaces, lists, fields, views, automations and terms as a portable file."
    >
      <div className="divide-y">
        <SettingRow id="tpl-workspace" label="Save as a template">
          <div className="flex flex-wrap items-center gap-2">
            <div className="w-56">
              <WorkspaceSelect
                id="tpl-workspace"
                value={workspaceId}
                onChange={setWorkspaceId}
                all="The whole organization"
              />
            </div>
            <label className="flex items-center gap-2 text-[13px]">
              <Checkbox checked={withContent} onChange={(e) => setWithContent(e.target.checked)} /> With items and files
            </label>
            <Button disabled={exportTemplate.isPending} onClick={() => exportTemplate.mutate()}>
              <Download /> Download
            </Button>
          </div>
        </SettingRow>
        {canApply && (
          <SettingRow id="tpl-file" label="Apply a template" hint="Try it first: nothing changes until you apply it.">
            <div className="flex flex-col gap-2">
              <Input
                id="tpl-file"
                type="file"
                accept=".xml,.zip,application/xml,application/zip"
                onChange={(e) => {
                  setFile(e.target.files?.[0]);
                  setResult(undefined);
                }}
              />
              <div className="flex flex-wrap items-center gap-2">
                <div className="w-56">
                  <WorkspaceSelect
                    id="tpl-target"
                    value={targetWorkspace}
                    onChange={setTargetWorkspace}
                    all="As the template says"
                  />
                </div>
                <Button disabled={!file || apply.isPending} onClick={() => apply.mutate(true)}>
                  Try it
                </Button>
                <Button
                  variant="primary"
                  disabled={!file || !result?.dryRun || apply.isPending}
                  onClick={() => apply.mutate(false)}
                >
                  Apply
                </Button>
              </div>
            </div>
          </SettingRow>
        )}
      </div>
      {apply.isError && <Alert className="mt-3">{problemMessage(apply.error)}</Alert>}
      {result && (
        <div className="mt-3 flex flex-col gap-2 rounded-md border p-3 text-[13px]" aria-label="Template changes">
          <p className="font-medium">
            {result.dryRun ? 'Applying it would make these changes:' : 'Applied:'}{' '}
            {!result.changes?.length && <span className="font-normal text-muted">nothing to change.</span>}
          </p>
          <ul className="flex flex-col gap-0.5">
            {result.changes?.map((c, i) => (
              <li key={i}>
                <Badge tone={c.action === 'create' ? 'success' : 'neutral'}>{c.action}</Badge> {c.kind}{' '}
                <span className="font-medium">{c.name}</span>
                {c.detail && <span className="text-xs text-muted"> · {c.detail}</span>}
              </li>
            ))}
          </ul>
          {result.warnings?.map((w, i) => (
            <Alert key={i} tone="warning">
              {w}
            </Alert>
          ))}
        </div>
      )}
    </SettingsSection>
  );
}

/** Export and import of everything, with content (PLT-13): moving to another installation or keeping a copy. */
function ExportImport({ canImport }: { canImport: boolean }) {
  const format = useFormat();
  const queryClient = useQueryClient();
  const [workspaceId, setWorkspaceId] = useState('');
  const [file, setFile] = useState<File>();
  const [importOperation, setImportOperation] = useState<string>();
  const exports = useQuery({
    queryKey: ['portability', 'exports'],
    queryFn: async () => (await api.v10.portability.exports.get()) ?? [],
    refetchInterval: (query) => (query.state.data?.some((e) => !e.ready) ? 2000 : false),
  });
  const create = useMutation({
    mutationFn: () => api.v10.portability.exports.post({ workspaceId: workspaceId || null }),
    onSuccess: async () => {
      toast.success('The export is being prepared.');
      await queryClient.invalidateQueries({ queryKey: ['portability', 'exports'] });
    },
  });
  const download = useMutation({
    mutationFn: async (id: string) => (await api.v10.portability.exports.byId(id).packageEscaped.get())!,
    onSuccess: (bytes) => saveBytes(bytes, 'paperdotnet-export.zip', 'application/zip'),
  });
  const remove = useMutation({
    mutationFn: (id: string) => api.v10.portability.exports.byId(id).delete(),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['portability', 'exports'] }),
  });
  const importPackage = useMutation({
    meta: { silent: true },
    mutationFn: async (dryRun: boolean) =>
      (await api.v10.portability.imports.post(await file!.arrayBuffer(), { queryParameters: { dryRun } }))!,
    onSuccess: (r) => setImportOperation(r.operationId ?? undefined),
  });
  return (
    <SettingsSection
      title="Export and import"
      description="Everything with its files, as one package, for another installation or safekeeping."
    >
      <div className="divide-y">
        <SettingRow id="exp-workspace" label="New export">
          <div className="flex flex-wrap items-center gap-2">
            <div className="w-56">
              <WorkspaceSelect
                id="exp-workspace"
                value={workspaceId}
                onChange={setWorkspaceId}
                all="The whole organization"
              />
            </div>
            <Button disabled={create.isPending} onClick={() => create.mutate()}>
              Export
            </Button>
          </div>
        </SettingRow>
        {!!exports.data?.length && (
          <ul className="flex flex-col py-2">
            {exports.data.map((e) => (
              <li key={e.id} className="flex items-center gap-3 py-1.5 text-[13px]">
                <span className="flex-1">
                  {format.dateTime(e.createdAt)}
                  <span className="text-xs text-muted">
                    {' '}
                    · {e.ready ? format.fileSize(e.size) : 'preparing…'} · kept until {format.date(e.expiresAt)}
                  </span>
                </span>
                <Button size="sm" disabled={!e.ready || download.isPending} onClick={() => download.mutate(e.id!)}>
                  <Download /> Download
                </Button>
                <Button size="icon" variant="ghost" aria-label="Delete export" onClick={() => remove.mutate(e.id!)}>
                  <Trash2 />
                </Button>
              </li>
            ))}
          </ul>
        )}
        {canImport && (
          <SettingRow
            id="imp-file"
            label="Import a package"
            hint="Try it first: it checks the package and reports what it would create."
          >
            <div className="flex flex-col gap-2">
              <Input
                id="imp-file"
                type="file"
                accept=".zip,application/zip"
                onChange={(e) => setFile(e.target.files?.[0])}
              />
              <div className="flex gap-2">
                <Button disabled={!file || importPackage.isPending} onClick={() => importPackage.mutate(true)}>
                  Try it
                </Button>
                <Button
                  variant="primary"
                  disabled={!file || importPackage.isPending}
                  onClick={() => importPackage.mutate(false)}
                >
                  <FileUp /> Import
                </Button>
              </div>
            </div>
          </SettingRow>
        )}
      </div>
      {importPackage.isError && <Alert className="mt-3">{problemMessage(importPackage.error)}</Alert>}
      {importOperation && <OperationProgress key={importOperation} id={importOperation} label="Import" />}
    </SettingsSection>
  );
}
