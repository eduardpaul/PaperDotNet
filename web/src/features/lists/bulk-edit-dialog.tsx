import type { ListResponse } from '@paperdotnet/client';
import { fields as fieldValues, waitForOperation } from '@paperdotnet/client';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useState, type FormEvent } from 'react';
import { toast } from 'sonner';
import { client } from '@/api/client';
import { keys } from '@/api/keys';
import { Button } from '@/components/ui/button';
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from '@/components/ui/dialog';
import { Alert, Spinner } from '@/components/ui/feedback';
import { Label } from '@/components/ui/input';
import { Select } from '@/components/ui/select';
import { FieldEditor } from '@/features/fields/editors';
import { ValueNamesProvider } from '@/features/fields/lookups';
import { normalize } from '@/features/fields/values';
import { problemMessage } from '@/lib/errors';
import { listBuilder } from './queries';
import { fieldLabel, listFields } from './schema';

/** Sets one field on the selected items (LST-05), as a background operation. */
export function BulkEditDialog({
  workspaceId,
  list,
  ids,
  open,
  onOpenChange,
  onDone,
}: {
  workspaceId: string;
  list: ListResponse;
  ids: string[];
  open: boolean;
  onOpenChange: (open: boolean) => void;
  onDone: () => void;
}) {
  const queryClient = useQueryClient();
  const fields = listFields(list).filter((f) => f.name !== 'title');
  const [name, setName] = useState(fields[0]?.name ?? '');
  const [value, setValue] = useState<unknown>(null);
  const field = fields.find((f) => f.name === name);
  const apply = useMutation({
    meta: { silent: true },
    mutationFn: async () => {
      const accepted = await listBuilder(workspaceId, list.id!).items.bulkUpdate.post({
        filter: `id in (${ids.join(',')})`,
        fields: fieldValues({ [name]: normalize(field!, value) }),
      });
      const operation = await waitForOperation(client, accepted!.id!, { interval: 500 });
      if (operation.status !== 'succeeded') throw new Error(operation.errorEscaped ?? 'The update failed.');
    },
    onSuccess: async () => {
      toast.success(`${ids.length} ${ids.length === 1 ? 'item' : 'items'} updated.`);
      await queryClient.invalidateQueries({ queryKey: keys.items(workspaceId, list.id!) });
      onOpenChange(false);
      onDone();
    },
  });

  const onSubmit = (event: FormEvent) => {
    event.preventDefault();
    apply.mutate();
  };

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent>
        <form onSubmit={onSubmit}>
          <DialogHeader>
            <DialogTitle>
              Edit {ids.length} {ids.length === 1 ? 'item' : 'items'}
            </DialogTitle>
            <DialogDescription>
              Sets the same value on every selected item. Leave it empty to clear it.
            </DialogDescription>
          </DialogHeader>
          <ValueNamesProvider fields={field ? [field] : []} values={[{ [name]: value }]}>
            <div className="flex flex-col gap-4 px-5 pb-5">
              {apply.isError && (
                <Alert>
                  {apply.error instanceof Error && !('responseStatusCode' in apply.error)
                    ? apply.error.message
                    : problemMessage(apply.error)}
                </Alert>
              )}
              <div className="flex flex-col gap-1.5">
                <Label htmlFor="bulk-field">Field</Label>
                <Select
                  id="bulk-field"
                  value={name}
                  onChange={(e) => {
                    setName(e.target.value);
                    setValue(null);
                  }}
                >
                  {fields.map((f) => (
                    <option key={f.name} value={f.name!}>
                      {fieldLabel(f)}
                    </option>
                  ))}
                </Select>
              </div>
              {field && (
                <div className="flex flex-col gap-1.5">
                  {field.type !== 'boolean' && (
                    <Label id="bulk-value-label" htmlFor="bulk-value">
                      Value
                    </Label>
                  )}
                  <FieldEditor
                    key={field.name}
                    id="bulk-value"
                    field={{ ...field, required: false }}
                    value={value}
                    onChange={setValue}
                  />
                </div>
              )}
            </div>
          </ValueNamesProvider>
          <DialogFooter>
            <Button onClick={() => onOpenChange(false)}>Cancel</Button>
            <Button type="submit" variant="primary" disabled={!field || apply.isPending}>
              {apply.isPending && <Spinner className="text-current" />}
              Apply
            </Button>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  );
}
