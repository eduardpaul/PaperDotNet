import type { DuplicatePolicy, OcrMode } from '@paperdotnet/client';
import { ifMatch } from '@paperdotnet/client';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute } from '@tanstack/react-router';
import { useState, type FormEvent } from 'react';
import { toast } from 'sonner';
import { Button } from '@/components/ui/button';
import { Alert, Skeleton } from '@/components/ui/feedback';
import { Input } from '@/components/ui/input';
import { Checkbox, Select } from '@/components/ui/select';
import { documentSettingsQuery, useCanManageList } from '@/features/list-settings/queries';
import { listBuilder } from '@/features/lists/queries';
import { SettingRow, SettingsSection } from '@/features/settings/section';
import { problemMessage } from '@/lib/errors';

export const Route = createFileRoute('/_app/w/$workspaceId/l/$listId/settings/documents')({ component: Documents });

interface Form {
  duplicatePolicy: DuplicatePolicy;
  autoProcess: boolean;
  ocrMode: OcrMode;
  ocrLanguages: string;
}

/** How the library handles new files: duplicates (DOC-10), processing and OCR (DOC-07, DOC-17). */
function Documents() {
  const { workspaceId, listId } = Route.useParams();
  const queryClient = useQueryClient();
  const { data: settings } = useQuery(documentSettingsQuery(workspaceId, listId));
  const canManage = useCanManageList(workspaceId, listId);
  const [form, setForm] = useState<Form>();
  const [baseline, setBaseline] = useState<string>();
  if (settings && settings.odataEtag !== baseline) {
    setBaseline(settings.odataEtag ?? undefined);
    setForm({
      duplicatePolicy: settings.duplicatePolicy ?? 'warn',
      autoProcess: settings.autoProcess ?? true,
      ocrMode: settings.ocrMode ?? 'auto',
      ocrLanguages: settings.ocrLanguagesInherited ? '' : (settings.ocrLanguages ?? ''),
    });
  }
  const save = useMutation({
    meta: { silent: true },
    mutationFn: async (value: Form) =>
      (await listBuilder(workspaceId, listId).documentSettings.put(
        { ...value, ocrLanguages: value.ocrLanguages.trim() || null },
        ifMatch(settings),
      ))!,
    onSuccess: (updated) => {
      queryClient.setQueryData(documentSettingsQuery(workspaceId, listId).queryKey, updated);
      toast.success('Settings saved.');
    },
  });

  if (!settings || !form) return <Skeleton className="h-64" />;
  const onSubmit = (event: FormEvent) => {
    event.preventDefault();
    save.mutate(form);
  };
  return (
    <form onSubmit={onSubmit}>
      <SettingsSection
        title="Documents"
        description="What happens to files uploaded to this library."
        actions={
          <Button type="submit" variant="primary" disabled={!canManage || save.isPending}>
            Save
          </Button>
        }
      >
        <fieldset disabled={!canManage} className="divide-y">
          <SettingRow
            id="doc-duplicates"
            label="Same file again"
            hint="A file with the same content as one already in the library."
          >
            <Select
              id="doc-duplicates"
              value={form.duplicatePolicy}
              onChange={(e) => setForm({ ...form, duplicatePolicy: e.target.value as DuplicatePolicy })}
            >
              <option value="allow">Allow it</option>
              <option value="warn">Allow it, with a warning</option>
              <option value="block">Refuse it</option>
            </Select>
          </SettingRow>
          <SettingRow
            id="doc-process"
            label="Processing"
            hint="Reads the text, makes thumbnails and page images for search and preview."
          >
            <label className="flex items-center gap-2 pt-2 text-[13px]">
              <Checkbox
                id="doc-process"
                checked={form.autoProcess}
                onChange={(e) => setForm({ ...form, autoProcess: e.target.checked })}
              />
              Process new files right away
            </label>
          </SettingRow>
          <SettingRow
            id="doc-ocr"
            label="OCR"
            hint="Recognizes the text of scans and photos; PDFs with text need none."
          >
            <Select
              id="doc-ocr"
              value={form.ocrMode}
              onChange={(e) => setForm({ ...form, ocrMode: e.target.value as OcrMode })}
            >
              <option value="auto">When a file has no text</option>
              <option value="off">Never</option>
            </Select>
          </SettingRow>
          <SettingRow
            id="doc-languages"
            label="OCR languages"
            hint="Tesseract codes joined with +, e.g. deu+eng. Empty uses the organization's default."
          >
            <Input
              id="doc-languages"
              placeholder={settings.ocrLanguagesInherited ? `Organization default (${settings.ocrLanguages})` : ''}
              value={form.ocrLanguages}
              onChange={(e) => setForm({ ...form, ocrLanguages: e.target.value })}
            />
          </SettingRow>
        </fieldset>
        {save.isError && <Alert className="mt-3">{problemMessage(save.error)}</Alert>}
      </SettingsSection>
    </form>
  );
}
