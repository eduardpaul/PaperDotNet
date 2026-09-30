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
  items: ScriptItems;
  /** A line in the run's log. */
  log(text: unknown): void;
}

/** A planned write. */
export interface ScriptWrite {
  op: 'create' | 'update' | 'delete';
  list: string;
  listId: string;
  id: string;
  fields: Record<string, unknown> | null;
}

/** The default limits (the server's can be changed in `Workflows:Scripts`, and it adds time, memory, statement and recursion limits). */
export const scriptLimits = { maxReads: 200, maxWrites: 1000, maxCodeLength: 50_000, defaultTop: 100, maxTop: 1000 } as const;

/** The globals of a script as TypeScript declarations, for editors (e.g. Monaco's `addExtraLib`). */
export const scriptDeclarations = `
interface ScriptItem { id: string; list: string; [field: string]: unknown }
interface ScriptQuery { filter?: string; orderBy?: string; top?: number }
/** The run's item, or null. */
declare const item: ScriptItem | null;
/** The run's variables: changes to its properties are kept. */
declare const vars: Record<string, any>;
/** The outputs of earlier steps, by node id. */
declare const steps: Record<string, any>;
/** The trigger's data. */
declare const trigger: Record<string, any>;
/** The workspace's lists, by name. Writes are applied in order when the script has returned. */
declare const items: {
  get(list: string, id: string): Promise<ScriptItem | null>;
  query(list: string, options?: ScriptQuery): Promise<ScriptItem[]>;
  create(list: string, fields: Record<string, unknown>): Promise<string>;
  update(list: string, id: string, fields: Record<string, unknown>): Promise<void>;
  delete(list: string, id: string): Promise<void>;
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
const AsyncFunction = Object.getPrototypeOf(async () => undefined).constructor as new (...args: string[]) => (...args: unknown[]) => Promise<unknown>;

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
    body = new AsyncFunction('item', 'vars', 'steps', 'trigger', 'items', 'log', `"use strict";${code}`);
  } catch (error) {
    throw new ScriptError(`code: ${(error as Error).message}`);
  }

  const workspace = client.api.v10.workspaces.byWorkspaceId(options.workspaceId);
  const lists = new Map((await workspace.lists.get() ?? []).map((l) => [l.name ?? '', l.id ?? '']));
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
  const write = (op: ScriptWrite['op'], name: unknown, id: unknown, values: unknown) => {
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
        q[key] === undefined || q[key] === null ? undefined : typeof q[key] === 'string' ? (q[key] as string) : fail(`${key} must be a string.`);
      const top = q.top === undefined || q.top === null ? scriptLimits.defaultTop : typeof q.top === 'number' ? Math.min(Math.max(Math.trunc(q.top), 1), scriptLimits.maxTop) : fail('top must be a number.');
      try {
        const page = await workspace.lists.byListId(target.id).items.get({ queryParameters: { filter: text('filter'), orderby: text('orderBy'), top } });
        return (page?.value ?? []).filter((i) => !i.isFolder).map((i) => asItem(i, target.name)!);
      } catch (error) {
        return fail(describe(error));
      }
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
    result = json(await body(run, vars, json(options.steps ?? {}), json(options.trigger ?? {}), items, (text: unknown) => log.push(String(text))));
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
export async function applyScriptPlan(client: PaperDotNetClient, workspaceId: string, plan: readonly ScriptWrite[]): Promise<Pick<ScriptRun, 'created' | 'updated' | 'deleted'>> {
  const lists = client.api.v10.workspaces.byWorkspaceId(workspaceId).lists;
  const created: string[] = [];
  let updated = 0;
  let deleted = 0;
  for (const [index, write] of plan.entries()) {
    const items = lists.byListId(write.listId).items;
    try {
      switch (write.op) {
        case 'create':
          await items.post({ id: write.id, fields: fieldValues(write.fields ?? {}) });
          created.push(write.id);
          break;
        case 'update':
          await withCurrentEtag(() => items.byItemId(write.id).get(), (current) => items.byItemId(write.id).patch({ fields: fieldValues(write.fields ?? {}) }, ifMatch(current)));
          updated++;
          break;
        default:
          try {
            await withCurrentEtag(() => items.byItemId(write.id).get(), (current) => items.byItemId(write.id).delete(ifMatch(current)));
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

function asItem(response: { id?: string | null; fields?: { additionalData?: Record<string, unknown> } | null } | undefined, list: string): ScriptItem | null {
  return response ? { ...structuredClone(fieldsOf(response)), id: response.id ?? '', list } : null;
}

function isObject(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

/** An API problem as text: its detail and validation messages. */
function describe(error: unknown): string {
  const problem = error as { detail?: string; title?: string; message?: string; errors?: { additionalData?: Record<string, unknown> } };
  const errors = Object.entries(problem.errors?.additionalData ?? {}).map(([key, value]) => `${key}: ${Array.isArray(value) ? value.join(' ') : String(value)}`);
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
