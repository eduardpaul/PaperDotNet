// Workflow scripts (ADR-0037): the contract of a workflow's `script` step, and a runner of it on this client. The server
// runs the same contract in its sandbox; the shared contract tests (test/scripts.test.mjs) run both.
import type { PaperDotNetClient } from './client.js';
import { isStatus } from './errors.js';
import { ifMatch } from './etag.js';
import { fields as fieldValues, fieldsOf } from './fields.js';

/** An item as a script sees it: its fields, with `id` and `list` (the list's name). */
export interface ScriptItem {
  id: string;
  list: string;
  [field: string]: unknown;
}

/** Options of `items.query`: an OData filter and order over the list's fields, as in the items API. */
export interface ScriptQuery {
  /** e.g. `fields/status eq 'open'` */
  filter?: string;
  /** e.g. `fields/amount desc` */
  orderBy?: string;
  /** Most items (default 100, at most 1000). */
  top?: number;
}

export interface ScriptRelationship {
  id: string;
  sourceId: string;
  targetId: string;
  directed: boolean;
  type: string | null;
  item: ScriptItem;
  attributes: Record<string, unknown>;
  version: number;
}
export interface ScriptRelationshipQuery {
  type?: string;
  direction?: 'both' | 'incoming' | 'outgoing';
  top?: number;
  cursor?: string;
}
export interface ScriptRelationshipPage {
  value: ScriptRelationship[];
  nextCursor: string | null;
}

export interface ScriptWorkspaceRelationshipQuery {
  type?: string;
  directed?: boolean;
  filter?: string;
  top?: number;
  cursor?: string;
}
export interface ScriptWorkspaceRelationship {
  id: string;
  directed: boolean;
  type: string | null;
  attributes: Record<string, unknown>;
  version: number;
  sourceItem: ScriptItem;
  targetItem: ScriptItem;
}
export interface ScriptWorkspaceRelationshipPage {
  value: ScriptWorkspaceRelationship[];
  nextCursor: string | null;
}

/**
 * The workspace's lists, by name. Reads happen right away. `create`, `update` and `delete` are planned: they are applied
 * in order when the script has returned (a read does not see them), and `create` gives the new item's id at once.
 */
export interface ScriptItems {
  get(list: string, id: string): Promise<ScriptItem | null>;
  query(list: string, options?: ScriptQuery): Promise<ScriptItem[]>;
  create(list: string, fields: Record<string, unknown>): Promise<string>;
  update(list: string, id: string, fields: Record<string, unknown>): Promise<void>;
  delete(list: string, id: string): Promise<void>;
  related(id: string, options?: ScriptRelationshipQuery): Promise<ScriptRelationshipPage>;
  relate(id: string, otherId: string, type?: string, attributes?: Record<string, unknown>): Promise<void>;
  relationships(options?: ScriptWorkspaceRelationshipQuery): Promise<ScriptWorkspaceRelationshipPage>;
  updateRelationship(id: string, relationshipId: string, attributes: Record<string, unknown>, version: number): Promise<void>;
  unrelate(id: string, relationshipId: string): Promise<void>;
  deleteById(id: string): Promise<void>;
}

/** What a script sees. Values are JSON copies; the script's `return` value is the step's `result`. */
export interface ScriptGlobals {
  /** The run's item, or null. */
  item: ScriptItem | null;
  /** The run's variables: changes to its properties are kept. */
  vars: Record<string, unknown>;
  /** The outputs of earlier steps, by node id. */
  steps: Record<string, unknown>;
  /** The trigger's data. */
  trigger: Record<string, unknown>;
  /** Uniform execution metadata and original launch parameters. */
  context: Record<string, unknown>;
  input: Record<string, unknown>;
  items: ScriptItems;
  /** A line in the run's log. */
  log(text: unknown): void;
}

/** A planned write. */
export interface ScriptWrite {
  op: 'create' | 'update' | 'delete' | 'relate' | 'unrelate' | 'deleteGlobal' | 'updateRelationship';
  list: string;
  listId: string;
  id: string;
  fields: Record<string, unknown> | null;
}

/** The default limits (the server's can be changed in `Workflows:Scripts`, and it adds time, memory, statement and recursion limits). */
export const scriptLimits = {
  maxReads: 200,
  maxWrites: 1000,
  maxCodeLength: 50_000,
  defaultTop: 100,
  maxTop: 1000,
} as const;

/** The globals of a script as TypeScript declarations, for editors (e.g. Monaco's `addExtraLib`). */
export const scriptDeclarations = `
interface ScriptItem { id: string; list: string; [field: string]: unknown }
interface ScriptQuery { filter?: string; orderBy?: string; top?: number }
interface ScriptRelationship { id: string; sourceId: string; targetId: string; directed: boolean; type: string | null; item: ScriptItem; attributes: Record<string, unknown>; version: number }
interface ScriptRelationshipQuery { type?: string; direction?: 'both' | 'incoming' | 'outgoing'; top?: number; cursor?: string }
interface ScriptWorkspaceRelationshipQuery { type?: string; directed?: boolean; filter?: string; top?: number; cursor?: string }
interface ScriptWorkspaceRelationship { id: string; directed: boolean; type: string | null; attributes: Record<string, unknown>; version: number; sourceItem: ScriptItem; targetItem: ScriptItem }
interface ScriptWorkspaceRelationshipPage { value: ScriptWorkspaceRelationship[]; nextCursor: string | null }
interface ScriptRelationshipPage { value: ScriptRelationship[]; nextCursor: string | null }
/** The run's item, or null. */
declare const item: ScriptItem | null;
/** The run's variables: changes to its properties are kept. */
declare const vars: Record<string, any>;
/** The outputs of earlier steps, by node id. */
declare const steps: Record<string, any>;
/** The trigger's data. */
declare const trigger: Record<string, any>;
declare const context: Record<string, any>;
declare const input: Record<string, any>;
/** The workspace's lists, by name. Writes are applied in order when the script has returned. */
declare const items: {
  get(list: string, id: string): Promise<ScriptItem | null>;
  query(list: string, options?: ScriptQuery): Promise<ScriptItem[]>;
  create(list: string, fields: Record<string, unknown>): Promise<string>;
  update(list: string, id: string, fields: Record<string, unknown>): Promise<void>;
  delete(list: string, id: string): Promise<void>;
  related(id: string, options?: ScriptRelationshipQuery): Promise<ScriptRelationshipPage>;
  relate(id: string, otherId: string, type?: string, attributes?: Record<string, unknown>): Promise<void>;
  relationships(options?: ScriptWorkspaceRelationshipQuery): Promise<ScriptWorkspaceRelationshipPage>;
  updateRelationship(id: string, relationshipId: string, attributes: Record<string, unknown>, version: number): Promise<void>;
  unrelate(id: string, relationshipId: string): Promise<void>;
  deleteById(id: string): Promise<void>;
};
/** A line in the run's log. */
declare function log(text: unknown): void;
`;

export interface RunScriptOptions {
  workspaceId: string;
  /** The script: a string or an array of lines (as in a workflow definition). */
  code: string | string[];
  /** The run's item, by list name and id. */
  item?: { list: string; id: string } | null;
  vars?: Record<string, unknown>;
  steps?: Record<string, unknown>;
  trigger?: Record<string, unknown>;
  context?: Record<string, unknown>;
  input?: Record<string, unknown>;
  /** Apply the planned writes (default true); false only plans them (a dry run). */
  apply?: boolean;
}

export interface ScriptRun {
  result: unknown;
  vars: Record<string, unknown>;
  plan: ScriptWrite[];
  log: string[];
  /** Ids of the items created, and the numbers updated and deleted (when applied). */
  created: string[];
  updated: number;
  deleted: number;
}

/** A script that failed: its error, with the script line when known (the same messages as on the server). */
export class ScriptError extends Error {
  override name = 'ScriptError';
}

const guid = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const AsyncFunction = Object.getPrototypeOf(async () => undefined).constructor as new (
  ...args: string[]
) => (...args: unknown[]) => Promise<unknown>;

/** The code of a step: `code` as a string or an array of lines. */
export function scriptCode(code: string | string[]): string {
  return Array.isArray(code) ? code.join('\n') : code;
}

/**
 * Runs a workflow script on this client, with the caller's rights and no sandbox: for writing and testing scripts
 * against a real server (`apply: false` for a dry run). Workflows run their scripts on the server.
 */
export async function runWorkflowScript(client: PaperDotNetClient, options: RunScriptOptions): Promise<ScriptRun> {
  const code = scriptCode(options.code);
  if (!code.trim()) {
    throw new ScriptError('code is required (a string, or an array of lines).');
  }

  if (code.length > scriptLimits.maxCodeLength) {
    throw new ScriptError(`code has at most ${scriptLimits.maxCodeLength} characters.`);
  }

  let body: (...args: unknown[]) => Promise<unknown>;
  try {
    // "use strict" on the first line keeps the script's line numbers.
    body = new AsyncFunction('item', 'vars', 'steps', 'trigger', 'context', 'input', 'items', 'log', `"use strict";${code}`);
  } catch (error) {
    throw new ScriptError(`code: ${(error as Error).message}`);
  }

  const workspace = client.api.v10.workspaces.byWorkspaceId(options.workspaceId);
  const lists = new Map(((await workspace.lists.get()) ?? []).map((l) => [l.name ?? '', l.id ?? '']));
  const plan: ScriptWrite[] = [];
  const log: string[] = [];
  let reads = 0;
  // A failed items call fails the script even when it is not awaited or is caught: the plan would be incomplete.
  let failure: string | undefined;
  const fail = (message: string): never => {
    failure ??= message;
    throw new Error(message);
  };
  const list = (name: unknown) => {
    const id = typeof name === 'string' ? lists.get(name) : fail('The list must be given by name.');
    return id ? { name: name as string, id } : fail(`The list '${String(name)}' does not exist in the workspace.`);
  };
  const itemId = (id: unknown) => (typeof id === 'string' && guid.test(id) ? id : fail('id must be the id of an item.'));
  const read = () => (++reads > scriptLimits.maxReads ? fail(`A script reads at most ${scriptLimits.maxReads} times.`) : undefined);
  const json = <T>(value: T): T => (value === undefined ? value : JSON.parse(JSON.stringify(value)));
  const get = async (target: { name: string; id: string }, id: string): Promise<ScriptItem | null> => {
    try {
      return asItem(await workspace.lists.byListId(target.id).items.byItemId(id).get(), target.name);
    } catch (error) {
      return isStatus(error, 404) ? null : fail(describe(error));
    }
  };
  const write = (op: 'create' | 'update' | 'delete', name: unknown, id: unknown, values: unknown) => {
    if (plan.length >= scriptLimits.maxWrites) {
      fail(`A script writes at most ${scriptLimits.maxWrites} items.`);
    }

    const target = list(name);
    const entry: ScriptWrite = {
      op,
      list: target.name,
      listId: target.id,
      id: op === 'create' ? crypto.randomUUID() : itemId(id),
      fields: op === 'delete' ? null : isObject(values) ? json(values) : fail('fields must be an object.'),
    };
    plan.push(entry);
    return entry.id;
  };

  const graphWrite = (
    op: 'relate' | 'unrelate' | 'deleteGlobal' | 'updateRelationship',
    id: string,
    otherId?: string,
    type?: string,
    attributes?: Record<string, unknown>,
    version?: number,
  ) => {
    if (plan.length >= scriptLimits.maxWrites) fail(`A script writes at most ${scriptLimits.maxWrites} items.`);
    if (type !== undefined && type !== null && typeof type !== 'string') fail('type must be a string.');
    if ((op === 'updateRelationship' || attributes != null) && !isObject(attributes)) fail('attributes must be an object.');
    if (op === 'updateRelationship' && (!Number.isInteger(version) || version! < 1 || version! > 4294967295))
      fail('version must be a positive relationship version.');
    plan.push({
      op,
      list: 'global items',
      listId: '00000000-0000-0000-0000-000000000000',
      id: itemId(id),
      fields: {
        otherId: otherId == null ? null : itemId(otherId),
        type: type ?? null,
        attributes: attributes == null ? null : json(attributes),
        version: version ?? null,
      },
    });
  };

  const items: ScriptItems = Object.freeze({
    get: async (name: string, id: string) => {
      read();
      return get(list(name), itemId(id));
    },
    query: async (name: string, query?: ScriptQuery) => {
      read();
      const target = list(name);
      const q = query === undefined || query === null ? {} : isObject(query) ? query : fail('The query options must be an object.');
      const text = (key: 'filter' | 'orderBy') =>
        q[key] === undefined || q[key] === null
          ? undefined
          : typeof q[key] === 'string'
            ? (q[key] as string)
            : fail(`${key} must be a string.`);
      const top =
        q.top === undefined || q.top === null
          ? scriptLimits.defaultTop
          : typeof q.top === 'number'
            ? Math.min(Math.max(Math.trunc(q.top), 1), scriptLimits.maxTop)
            : fail('top must be a number.');
      try {
        const page = await workspace.lists.byListId(target.id).items.get({
          queryParameters: {
            filter: text('filter'),
            orderby: text('orderBy'),
            top,
          },
        });
        return (page?.value ?? []).filter((i) => !i.isFolder).map((i) => asItem(i, target.name)!);
      } catch (error) {
        return fail(describe(error));
      }
    },
    related: async (id: string, options?: ScriptRelationshipQuery) => {
      read();
      const q = options == null ? {} : isObject(options) ? options : fail('The relationship options must be an object.');
      const text = (key: 'type' | 'direction' | 'cursor') =>
        q[key] == null ? undefined : typeof q[key] === 'string' ? (q[key] as string) : fail(`${key} must be a string.`);
      const direction = text('direction');
      if (direction !== undefined && !['both', 'incoming', 'outgoing'].includes(direction))
        fail('direction must be both, incoming or outgoing.');
      const cursor = text('cursor');
      if (cursor !== undefined && !/^[A-Za-z0-9_-]{22}$/.test(cursor)) fail('The relationship cursor is invalid.');
      const top =
        q.top == null ? 100 : typeof q.top === 'number' ? Math.min(Math.max(Math.trunc(q.top), 1), 500) : fail('top must be a number.');
      try {
        const page = await client.api.v10.items.byItemId(itemId(id)).relationships.get({
          queryParameters: {
            type: text('type'),
            direction,
            top,
            skiptoken: cursor,
          },
        });
        return {
          value: (page?.value ?? []).map((edge) => ({
            id: edge.id!,
            sourceId: edge.sourceItemId!,
            targetId: edge.targetItemId!,
            directed: edge.directed ?? false,
            type: edge.type?.name ?? null,
            attributes: edge.attributes?.additionalData ?? {},
            version: edge.version ?? 1,
            item: asItem(edge.relatedItem?.item ?? undefined, edge.relatedItem?.listName ?? '')!,
          })),
          nextCursor: page?.odataNextLink ? new URL(page.odataNextLink).searchParams.get('$skiptoken') : null,
        };
      } catch (error) {
        return fail(describe(error));
      }
    },
    relate: async (id: string, otherId: string, type?: string, attributes?: Record<string, unknown>) => {
      graphWrite('relate', id, otherId, type, attributes);
    },
    updateRelationship: async (id: string, relationshipId: string, attributes: Record<string, unknown>, version: number) => {
      graphWrite('updateRelationship', id, relationshipId, undefined, attributes, version);
    },
    relationships: async (queryOptions?: ScriptWorkspaceRelationshipQuery) => {
      read();
      const q = queryOptions == null ? {} : isObject(queryOptions) ? queryOptions : fail('relationship options must be an object.');
      const text = (name: 'type' | 'filter' | 'cursor') =>
        q[name] == null ? undefined : typeof q[name] === 'string' ? q[name] : fail(`${name} must be a string.`);
      const directed = q.directed == null ? undefined : typeof q.directed === 'boolean' ? q.directed : fail('directed must be a boolean.');
      const cursor = text('cursor');
      if (cursor !== undefined && !/^[A-Za-z0-9_-]{22}$/.test(cursor)) fail('The relationship cursor is invalid.');
      const top =
        q.top == null ? 100 : typeof q.top === 'number' ? Math.min(Math.max(Math.trunc(q.top), 1), 500) : fail('top must be a number.');
      try {
        const page = await client.api.v10.workspaces
          .byWorkspaceId(options.workspaceId)
          .relationships.get({ queryParameters: { type: text('type'), directed, filter: text('filter'), top, skiptoken: cursor } });
        return {
          value: (page?.value ?? []).map((edge) => ({
            id: edge.id!,
            directed: edge.directed ?? false,
            type: edge.type?.name ?? null,
            attributes: edge.attributes?.additionalData ?? {},
            version: edge.version ?? 1,
            sourceItem: asItem(edge.sourceItem?.item ?? undefined, edge.sourceItem?.listName ?? '')!,
            targetItem: asItem(edge.targetItem?.item ?? undefined, edge.targetItem?.listName ?? '')!,
          })),
          nextCursor: page?.odataNextLink ? new URL(page.odataNextLink).searchParams.get('$skiptoken') : null,
        };
      } catch (error) {
        return fail(describe(error));
      }
    },
    unrelate: async (id: string, relationshipId: string) => {
      graphWrite('unrelate', id, relationshipId);
    },
    deleteById: async (id: string) => {
      graphWrite('deleteGlobal', id);
    },
    create: async (name: string, values: Record<string, unknown>) => write('create', name, null, values),
    update: async (name: string, id: string, values: Record<string, unknown>) => {
      write('update', name, id, values);
    },
    delete: async (name: string, id: string) => {
      write('delete', name, id, null);
    },
  });

  let run: ScriptItem | null = null;
  if (options.item) {
    try {
      run = await get(list(options.item.list), itemId(options.item.id));
    } catch (error) {
      throw new ScriptError((error as Error).message);
    }

    if (!run) {
      throw new ScriptError('The item does not exist.');
    }

    failure = undefined;
  }

  const vars = json(options.vars ?? {});
  let result: unknown;
  try {
    result = json(
      await body(run, vars, json(options.steps ?? {}), json(options.trigger ?? {}), json(options.context ?? {}), json(options.input ?? {}), items, (text: unknown) => log.push(String(text))),
    );
  } catch (error) {
    throw new ScriptError(failure ?? withLine(error));
  }

  if (failure) {
    throw new ScriptError(failure);
  }

  const done = options.apply === false ? { created: [], updated: 0, deleted: 0 } : await applyScriptPlan(client, options.workspaceId, plan);
  return { result: result ?? null, vars, plan, log, ...done };
}

/**
 * Applies planned writes in order, as the server does: creates use the planned ids, so applying a plan again (e.g. after
 * a failure) continues it; deleting an item that is gone is fine. Updates merge the fields (last writer wins).
 */
export async function applyScriptPlan(
  client: PaperDotNetClient,
  workspaceId: string,
  plan: readonly ScriptWrite[],
): Promise<Pick<ScriptRun, 'created' | 'updated' | 'deleted'>> {
  const lists = client.api.v10.workspaces.byWorkspaceId(workspaceId).lists;
  const created: string[] = [];
  let updated = 0;
  let deleted = 0;
  for (const [index, write] of plan.entries()) {
    const items = lists.byListId(write.listId).items;
    try {
      switch (write.op) {
        case 'relate':
          await withCurrentEtag(
            () => client.api.v10.items.byItemId(write.id).get(),
            () =>
              client.api.v10.items.byItemId(write.id).relationships.post({
                otherId: String(write.fields?.otherId),
                type: write.fields?.type as string | undefined,
                attributes: write.fields?.attributes == null ? undefined : fieldValues(write.fields.attributes as Record<string, unknown>),
              }),
          );
          break;
        case 'updateRelationship': {
          const target = client.api.v10.items.byItemId(write.id).relationships.byRelationshipId(String(write.fields?.otherId));
          const attributes = write.fields!.attributes as Record<string, unknown>;
          const version = Number(write.fields!.version);
          try {
            await target.patch({ attributes: fieldValues(attributes) }, { headers: { 'If-Match': `"${version}"` } });
          } catch (error) {
            if (!isStatus(error, 412)) throw error;
            const edge = await target.get();
            const current = edge?.attributes?.additionalData ?? {};
            if (
              edge?.version !== version + 1 ||
              !Object.entries(attributes).every(([key, value]) => (value == null ? !(key in current) : current[key] === value))
            )
              throw error;
          }
          break;
        }
        case 'unrelate':
          try {
            await client.api.v10.items.byItemId(write.id).relationships.byRelationshipId(String(write.fields?.otherId)).delete();
          } catch (error) {
            if (!isStatus(error, 404)) throw error;
          }
          break;
        case 'deleteGlobal': {
          try {
            const current = await client.api.v10.items.byItemId(write.id).get();
            const target = client.api.v10.workspaces
              .byWorkspaceId(current!.workspaceId!)
              .lists.byListId(current!.item!.listId!)
              .items.byItemId(write.id);
            await withCurrentEtag(
              () => target.get(),
              (value) => target.delete(ifMatch(value)),
            );
          } catch (error) {
            if (!isStatus(error, 404)) throw error;
          }
          deleted++;
          break;
        }
        case 'create':
          await items.post({
            id: write.id,
            fields: fieldValues(write.fields ?? {}),
          });
          created.push(write.id);
          break;
        case 'update':
          await withCurrentEtag(
            () => items.byItemId(write.id).get(),
            (current) => items.byItemId(write.id).patch({ fields: fieldValues(write.fields ?? {}) }, ifMatch(current)),
          );
          updated++;
          break;
        default:
          try {
            await withCurrentEtag(
              () => items.byItemId(write.id).get(),
              (current) => items.byItemId(write.id).delete(ifMatch(current)),
            );
          } catch (error) {
            if (!isStatus(error, 404)) {
              throw error;
            }
          }

          deleted++;
      }
    } catch (error) {
      throw new ScriptError(`write ${index + 1} (${write.op} in ${write.list}): ${describe(error)}`);
    }
  }

  return { created, updated, deleted };
}

/** Changes that need the item's ETag: read it, change it, and read it again when someone else changed it meanwhile. */
async function withCurrentEtag<T, R>(read: () => Promise<T>, change: (current: T) => Promise<R>): Promise<R> {
  for (let attempt = 1; ; attempt++) {
    try {
      return await change(await read());
    } catch (error) {
      if (!isStatus(error, 412) || attempt >= 3) {
        throw error;
      }
    }
  }
}

function asItem(
  response:
    | {
        id?: string | null;
        fields?: { additionalData?: Record<string, unknown> } | null;
      }
    | undefined,
  list: string,
): ScriptItem | null {
  return response ? { ...structuredClone(fieldsOf(response)), id: response.id ?? '', list } : null;
}

function isObject(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

/** An API problem as text: its detail and validation messages. */
function describe(error: unknown): string {
  const problem = error as {
    detail?: string;
    title?: string;
    message?: string;
    errors?: { additionalData?: Record<string, unknown> };
  };
  const errors = Object.entries(problem.errors?.additionalData ?? {}).map(
    ([key, value]) => `${key}: ${Array.isArray(value) ? value.join(' ') : String(value)}`,
  );
  return [problem.detail ?? problem.title ?? problem.message ?? String(error), ...errors].join(' ');
}

/** A thrown value as a message with the script line it came from (the function's body starts on line 3). */
function withLine(error: unknown): string {
  if (!(error instanceof Error)) {
    return String(error);
  }

  const line = /<anonymous>:(\d+):\d+/.exec(error.stack ?? '');
  return line ? `${error.message} (line ${Number(line[1]) - 2})` : error.message;
}
