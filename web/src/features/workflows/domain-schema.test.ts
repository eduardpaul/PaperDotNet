import type { RJSFSchema } from '@rjsf/utils';
import { describe, expect, it } from 'vitest';
import { domainSchema, domainUiSchema } from './domain-schema';

describe('workflow domain schemas', () => {
  it('selects domain fields within objects and repeated sections', () => {
    expect(
      domainUiSchema({
        type: 'object',
        properties: {
          targets: {
            type: 'array',
            items: { type: 'string' },
            'x-paperdotnet': { kind: 'relationship', relationshipType: 'Depends on' },
          },
          reviewers: {
            type: 'array',
            items: { type: 'string' },
            'x-paperdotnet': { kind: 'people' },
          },
          rows: {
            type: 'array',
            items: { type: 'object', properties: { tag: { type: 'string', 'x-paperdotnet': { kind: 'terms' } } } },
          },
        },
      } as RJSFSchema),
    ).toEqual({
      targets: { 'ui:field': 'DomainSelection' },
      reviewers: { 'ui:field': 'PeopleSelection' },
      rows: { items: { tag: { 'ui:field': 'DomainSelection' } } },
    });
  });
  it('enforces required selections without changing optional fields or the source schema', () => {
    const schema = {
      type: 'object' as const,
      properties: {
        tags: { type: 'array' as const, items: { type: 'string' as const }, 'x-paperdotnet': { kind: 'terms' } },
        target: { type: 'string' as const, 'x-paperdotnet': { kind: 'relationship' } },
        optional: { type: 'array' as const, items: { type: 'string' as const }, 'x-paperdotnet': { kind: 'keywords' } },
      },
      required: ['tags', 'target'],
    };
    expect(domainSchema(schema).properties).toMatchObject({ tags: { minItems: 1 }, target: { minLength: 1 } });
    expect(domainSchema(schema).properties?.optional).not.toHaveProperty('minItems');
    expect(schema.properties.tags).not.toHaveProperty('minItems');
  });
});
