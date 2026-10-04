// The public Form entry avoids the package root's test registry, which imports AJV.
import Form from '@rjsf/core/lib/components/Form.js';
import { customizeValidator } from 'rjsf-validator-cfworker';
import { DomainSelection } from './domain-field';
import { domainSchema, domainUiSchema } from './domain-schema';
import type { SchemaFormProps } from './schema-form';

const validator = customizeValidator<Record<string, unknown>>({ draft: '7' });

export default function SchemaFormRenderer({ onSubmit, children, ...props }: SchemaFormProps) {
  return (
    <Form<Record<string, unknown>>
      {...props}
      schema={domainSchema(props.schema)}
      validator={validator}
      experimental_defaultFormStateBehavior={{
        arrayMinItems: { computeSkipPopulate: (_validator, schema) => 'x-paperdotnet' in schema },
      }}
      fields={{ DomainSelection }}
      uiSchema={domainUiSchema(props.schema)}
      onSubmit={({ formData }, event) => {
        const button = (event?.nativeEvent as SubmitEvent | undefined)?.submitter;
        onSubmit?.(formData ?? {}, button instanceof HTMLButtonElement ? button.value : undefined);
      }}
      className="flex flex-col gap-4 [&_.form-group]:mb-3 [&_input]:rounded [&_input]:border [&_input]:p-2 [&_label]:block [&_select]:rounded [&_select]:border [&_select]:p-2"
    >
      {children}
    </Form>
  );
}
