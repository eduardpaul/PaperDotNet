import type { RJSFSchema } from '@rjsf/utils';
import { lazy, Suspense, type ReactNode } from 'react';
import { Skeleton } from '@/components/ui/feedback';

const Renderer = lazy(() => import('./schema-form-renderer'));

export interface SchemaFormProps {
  schema: RJSFSchema;
  formData?: Record<string, unknown>;
  disabled?: boolean;
  idPrefix?: string;
  onSubmit?: (data: Record<string, unknown>, submitter?: string) => void;
  children: ReactNode;
}

/** Load schema rendering only when a person needs a form. */
export function SchemaForm(props: SchemaFormProps) {
  return (
    <Suspense fallback={<Skeleton className="h-24" />}>
      <Renderer {...props} />
    </Suspense>
  );
}
