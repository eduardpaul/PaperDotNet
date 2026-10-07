import { problemOf } from '@paperdotnet/client';
import { useQuery } from '@tanstack/react-query';
import { Link } from '@tanstack/react-router';
import { KeyRound } from 'lucide-react';
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
import { Alert, Skeleton } from '@/components/ui/feedback';
import { listBuilder } from '@/features/lists/queries';
import { CopyField } from '@/features/settings/section';
import { problemMessage } from '@/lib/errors';

/** The scopes a WebDAV token needs to read and save files (ADR-0047); without the write scopes the drive is read-only. */
export const webDavScopes = ['list.read', 'document.read', 'list.write', 'document.write'];

/**
 * "Open in Explorer" (API-10): the library's WebDAV address from the server (names follow the server's rules) and how
 * to map it as a drive with an API token as the password.
 */
export function WebDavDialog({
  workspaceId,
  listId,
  onClose,
}: {
  workspaceId: string;
  listId: string;
  onClose: () => void;
}) {
  const location = useQuery({
    queryKey: keys.webDav(workspaceId, listId),
    queryFn: async () => (await listBuilder(workspaceId, listId).webDav.get())!,
    retry: false,
  });
  const disabled = problemOf(location.error)?.code === 'webDavDisabled';
  const url = location.data?.url ?? '';

  return (
    <Dialog open onOpenChange={(open) => !open && onClose()}>
      <DialogContent className="max-w-xl">
        <DialogHeader>
          <DialogTitle>Open in Explorer</DialogTitle>
          <DialogDescription>
            Map this library as a network drive to open, edit and save its files from your desktop apps. Saving a file
            stores a new version.
          </DialogDescription>
        </DialogHeader>
        <div className="flex flex-col gap-4 px-5 pb-4 text-sm">
          {location.isPending ? (
            <Skeleton className="h-9" />
          ) : disabled ? (
            <Alert tone="warning">WebDAV is turned off on this server.</Alert>
          ) : location.isError ? (
            <Alert tone="danger">{problemMessage(location.error)}</Alert>
          ) : (
            <>
              <CopyField label="WebDAV address" value={url} />
              <ol className="flex list-decimal flex-col gap-1.5 pl-5 text-muted">
                <li>
                  Create an API token with the scopes <code>list.read</code>, <code>document.read</code>,{' '}
                  <code>list.write</code> and <code>document.write</code> (only the first two for a read-only drive). It
                  is your password for the drive.
                </li>
                <li>
                  In Windows Explorer choose <strong>This PC → Map network drive</strong>, paste the address and select{' '}
                  <strong>Connect using different credentials</strong>.
                </li>
                <li>Sign in with any user name and the API token as the password.</li>
              </ol>
              <div>
                <p className="mb-1 text-muted">Or from a command prompt:</p>
                <CopyField label="Command" value={`net use * "${url}" /user:paperdotnet *`} />
              </div>
              {!url.startsWith('https:') && (
                <Alert tone="warning">
                  Windows sends passwords to WebDAV servers only over HTTPS. Use this server through HTTPS, or see the
                  WebDAV guide for testing on a local network.
                </Alert>
              )}
            </>
          )}
        </div>
        <DialogFooter>
          <Button asChild>
            <Link to="/settings/tokens" search={{ new: 'WebDAV', scopes: webDavScopes.join(',') }}>
              <KeyRound /> Create a token
            </Link>
          </Button>
          <Button variant="primary" onClick={onClose}>
            Done
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}
