import { useQuery } from '@tanstack/react-query';
import { createFileRoute } from '@tanstack/react-router';
import { api } from '@/api/client';
import { Skeleton } from '@/components/ui/feedback';
import { useHasScope } from '@/extensibility/admin';
import { PreferencesForm } from '@/features/settings/preferences-form';
import { SettingsSection } from '@/features/settings/section';

export const Route = createFileRoute('/_app/admin/')({ component: Organization });

/** The organization and the defaults everyone starts with (PLT-18). */
function Organization() {
  const canManage = useHasScope('organization.manage');
  const { data: organization } = useQuery({
    queryKey: ['organization'],
    queryFn: async () => (await api.v10.organization.get())!,
  });
  return (
    <>
      {organization ? (
        <SettingsSection title={organization.displayName ?? 'Organization'}>
          <dl className="grid grid-cols-[8rem_1fr] gap-y-1 text-[13px]">
            <dt className="text-muted">Identifier</dt>
            <dd className="font-mono text-xs">{organization.identifier}</dd>
            <dt className="text-muted">Id</dt>
            <dd className="font-mono text-xs">{organization.id}</dd>
          </dl>
        </SettingsSection>
      ) : (
        <Skeleton className="h-24" />
      )}
      <PreferencesForm scope="organization" canEdit={canManage} />
    </>
  );
}
