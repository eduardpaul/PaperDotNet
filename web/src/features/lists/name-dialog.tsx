import { useState, type FormEvent } from 'react';
import { Button } from '@/components/ui/button';
import { Dialog, DialogContent, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog';
import { Alert } from '@/components/ui/feedback';
import { Input, Label } from '@/components/ui/input';

/** Asks for a name (new folder, new view, rename). */
export function NameDialog({
  open,
  onOpenChange,
  title,
  label = 'Name',
  submit,
  initial = '',
  error,
  busy,
  onSubmit,
}: {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  title: string;
  label?: string;
  submit: string;
  initial?: string;
  error?: string;
  busy?: boolean;
  onSubmit: (name: string) => void;
}) {
  const [name, setName] = useState(initial);
  const handle = (event: FormEvent) => {
    event.preventDefault();
    if (name.trim()) onSubmit(name.trim());
  };
  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className="max-w-md">
        <form onSubmit={handle}>
          <DialogHeader>
            <DialogTitle>{title}</DialogTitle>
          </DialogHeader>
          <div className="flex flex-col gap-3 px-5 pb-5">
            {error && <Alert>{error}</Alert>}
            <Label htmlFor="name-dialog-input">{label}</Label>
            <Input id="name-dialog-input" autoFocus required value={name} onChange={(e) => setName(e.target.value)} />
          </div>
          <DialogFooter>
            <Button onClick={() => onOpenChange(false)}>Cancel</Button>
            <Button type="submit" variant="primary" disabled={!name.trim() || busy}>
              {submit}
            </Button>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  );
}
