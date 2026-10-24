import type { PaperDotNetClient } from "./client.js";
import { throwProblem } from "./errors.js";

/** Content field numbers are strings on the wire, including integers outside JavaScript's safe range. */
export interface ContentField {
  id: string;
  label: string;
  type: string;
  required?: boolean;
  choices?: string[];
  indexed?: boolean;
  scale?: number;
  definition_id?: string;
  options?: {
    multiple?: boolean;
    unique?: boolean;
    max_length?: number;
    default_value?: unknown;
    term_set_id?: string;
  };
}
export interface ContentTypeDefinition {
  id: string;
  key: string;
  name: string;
  field_keys: string[];
  is_default?: boolean;
}
export interface ContentSchema {
  fields: ContentField[];
  content_types: ContentTypeDefinition[];
  rules?: unknown[];
}
export interface ContentResource {
  id: string;
  workspaceId: string;
  parentId?: string;
  containerId?: string;
  name: string;
  kind: string;
  version: number;
  publishingEnabled: boolean;
  versioning: string;
  maxVersions: number;
  archivedAt?: string;
  archivedBy?: string;
}
export interface ContentSnapshot {
  resource: ContentResource;
  revisionId: string;
  revisionNumber: number;
  schemaRevisionId: string;
  contentTypeId: string;
  values: Record<string, unknown>;
  keywords: string[];
  filename: string;
  hasFile: boolean;
  isDraft: boolean;
  schema: ContentSchema;
}
export interface ContentCollection {
  resource: ContentResource;
  schema?: ContentSchema;
  schemaRevisionId?: string;
  canWrite: boolean;
  canPublish: boolean;
  canReadDraft: boolean;
  canManage: boolean;
}
export interface ContentValues {
  name: string;
  contentTypeId: string;
  values: Record<string, unknown>;
  keywords?: string[];
}
export interface ContentHistory {
  number: number;
  changedFields: string[];
  revision: {
    id: string;
    name: string;
    createdAt: string;
    createdBy: string;
    payload: string;
    schemaRevisionId: string;
  };
  schema: { id: string; definition: string };
}
export interface ContentPermission {
  principalType: string;
  principalId: string;
  action: string;
}
export interface ContentMigration {
  workspaceId: string;
  name: string;
  sourceHash: string;
  collections: number;
  items: number;
  historyEntries: number;
  files: number;
  alreadyImported: boolean;
  blockers: string[];
}

/** Precision-preserving JSON and streamed files over the generated API's content contract. Authentication stays in the shared client. */
export function contentClient(client: PaperDotNetClient) {
  const root = `${client.baseUrl}/v1.0/content`;
  const id = encodeURIComponent;
  async function request<T>(
    path: string,
    method = "GET",
    body?: unknown,
    version?: number,
  ): Promise<T> {
    const headers = new Headers();
    if (version !== undefined) headers.set("If-Match", `"${version}"`);
    if (body !== undefined) headers.set("Content-Type", "application/json");
    const response = await client.fetch(root + path, {
      method,
      headers,
      body: body === undefined ? undefined : JSON.stringify(body),
    });
    if (!response.ok) await throwProblem(response);
    return (await response.json()) as T;
  }
  return {
    workspaces: () => request<ContentResource[]>("/workspaces"),
    createWorkspace: (name: string) =>
      request<ContentResource>("/workspaces", "POST", { name }),
    collections: (workspace: string) =>
      request<ContentResource[]>(`/workspaces/${id(workspace)}/collections`),
    createCollection: (
      workspace: string,
      name: string,
      library: boolean,
      publishingEnabled: boolean,
    ) =>
      request<ContentResource>(
        `/workspaces/${id(workspace)}/collections`,
        "POST",
        { name, library, publishingEnabled },
      ),
    collection: (collection: string) =>
      request<ContentCollection>(`/collections/${id(collection)}`),
    schema: (collection: ContentResource, schema: ContentSchema) =>
      request(
        `/collections/${id(collection.id)}/schema`,
        "PUT",
        schema,
        collection.version,
      ),
    settings: (
      collection: ContentResource,
      publishingEnabled: boolean,
      versioning: string,
      maxVersions: number,
    ) =>
      request<ContentResource>(
        `/collections/${id(collection.id)}/settings`,
        "PUT",
        { publishingEnabled, versioning, maxVersions },
        collection.version,
      ),
    items: (
      collection: string,
      options: {
        surface?: string;
        archive?: string;
        search?: string;
        parentId?: string;
        afterId?: string;
        limit?: number;
      },
    ) => {
      const query = new URLSearchParams(
        Object.entries(options)
          .filter(([, v]) => v !== undefined && v !== "")
          .map(([k, v]) => [k, String(v)]),
      );
      return request<ContentSnapshot[]>(
        `/collections/${id(collection)}/items?${query}`,
      );
    },
    globalItems: (options: {
      surface?: string;
      archive?: string;
      search?: string;
      afterId?: string;
      limit?: number;
    }) => {
      const query = new URLSearchParams(
        Object.entries(options)
          .filter(([, v]) => v !== undefined && v !== "")
          .map(([k, v]) => [k, String(v)]),
      );
      return request<ContentSnapshot[]>(`/items?${query}`);
    },
    item: (item: string, surface: string) =>
      request<ContentSnapshot>(`/items/${id(item)}?surface=${id(surface)}`),
    create: (parent: string, content: ContentValues, folder = false) =>
      request<ContentSnapshot>(`/resources/${id(parent)}/items`, "POST", {
        content,
        folder,
      }),
    edit: (item: ContentResource, content: ContentValues) =>
      request<ContentSnapshot>(
        `/items/${id(item.id)}`,
        "PUT",
        content,
        item.version,
      ),
    publish: (item: ContentResource) =>
      request<ContentResource>(
        `/items/${id(item.id)}/publish`,
        "POST",
        undefined,
        item.version,
      ),
    unpublish: (item: ContentResource) =>
      request<ContentResource>(
        `/items/${id(item.id)}/unpublish`,
        "POST",
        undefined,
        item.version,
      ),
    archive: (item: ContentResource, archived: boolean) =>
      request<ContentResource>(
        `/items/${id(item.id)}/archive`,
        "PUT",
        { archived },
        item.version,
      ),
    history: (item: string) =>
      request<ContentHistory[]>(`/items/${id(item)}/history`),
    restore: (item: ContentResource, number: number) =>
      request<ContentSnapshot>(
        `/items/${id(item.id)}/restore`,
        "POST",
        { number },
        item.version,
      ),
    grants: (resource: string) =>
      request<{ grants: ContentPermission[]; inherit: boolean }>(
        `/resources/${id(resource)}/grants`,
      ),
    setGrants: (
      resource: ContentResource,
      grants: ContentPermission[],
      inherit: boolean,
    ) =>
      request<ContentResource>(
        `/resources/${id(resource.id)}/grants`,
        "PUT",
        { grants, inherit },
        resource.version,
      ),
    preview: (workspace: string) =>
      request<ContentMigration>(`/migrations/${id(workspace)}/preview`),
    migrate: (preview: ContentMigration) =>
      request<ContentResource>(
        `/migrations/${id(preview.workspaceId)}`,
        "POST",
        { sourceHash: preview.sourceHash },
      ),
    upload: async (
      item: ContentResource,
      file: File,
    ): Promise<ContentSnapshot> => {
      const body = new FormData();
      body.set("file", file);
      const response = await client.fetch(`${root}/items/${id(item.id)}/file`, {
        method: "PUT",
        headers: { "If-Match": `"${item.version}"` },
        body,
      });
      if (!response.ok) await throwProblem(response);
      return (await response.json()) as ContentSnapshot;
    },
    download: async (item: string, surface: string) => {
      const response = await client.fetch(
        `${root}/items/${id(item)}/file?surface=${id(surface)}`,
      );
      if (!response.ok) await throwProblem(response);
      return response.blob();
    },
  };
}
export type ContentClient = ReturnType<typeof contentClient>;
