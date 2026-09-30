// Live events (API-07, `/v1.0/me/events`): server-sent events with the client's authentication and tenant, reconnecting
// with Last-Event-ID. Kiota does not generate event streams, so this uses the `eventsource` package (MIT), which
// accepts a custom fetch (browsers' EventSource cannot send an Authorization header).
import { EventSource } from 'eventsource';
import type { OperationStatus } from '../generated/models/index.js';
import type { PaperDotNetClient } from './client.js';

/** `connected`: the stream is live (sent first on every connection). */
export interface ConnectedEvent {
  tenantId: string;
  userId: string;
}

/** `operation`: status and progress of a long-running operation the user started. */
export interface OperationEvent {
  id: string;
  type: string;
  status: OperationStatus;
  percentComplete?: number | null;
  error?: string | null;
}

/** `document.changed`: a document workflow made something for a file version (ADR-0038): its text, thumbnail, pages or an OCR version. */
export interface DocumentChangedEvent {
  workspaceId: string;
  listId: string;
  itemId: string;
  version: number;
  what: 'text' | 'thumbnail' | 'pages' | 'ocr';
}

/** `notification`: a new notification in the user's inbox (same shape as the notifications endpoint, as JSON). */
export interface NotificationEvent {
  id: string;
  type: string;
  title: string;
  body?: string | null;
  workspaceId?: string | null;
  listId?: string | null;
  itemId?: string | null;
  /** ISO 8601. */
  createdAt: string;
  readAt?: string | null;
}

/** `item.changed`: an item was added, updated, deleted or restored. Ids only; clients reload through the API. */
export interface ItemChangedEvent {
  kind: 'added' | 'updated' | 'deleted' | 'restored' | string;
  workspaceId: string;
  listId: string;
  itemId: string;
}

export interface LiveEventMap {
  connected: ConnectedEvent;
  operation: OperationEvent;
  'document.changed': DocumentChangedEvent;
  notification: NotificationEvent;
  'item.changed': ItemChangedEvent;
}

export type LiveEventHandlers = { [K in keyof LiveEventMap]?: (data: LiveEventMap[K]) => void } & {
  /** Events of other types (from extensions, or added later). */
  other?: (type: string, data: unknown) => void;
  /** Connection problems; the stream reconnects by itself unless `closed` is true (e.g. 401 after a failed refresh, 403). */
  error?: (error: { status?: number; message?: string; closed: boolean }) => void;
};

/** An open live event subscription. */
export interface LiveEventSubscription {
  close(): void;
}

const KnownTypes = ['connected', 'operation', 'document.changed', 'notification', 'item.changed'] as const;

/**
 * Subscribes to the signed-in user's live events:
 * `const sub = subscribeLiveEvents(client, { operation: (e) => …, notification: (n) => … }); … sub.close();`
 */
export function subscribeLiveEvents(client: PaperDotNetClient, handlers: LiveEventHandlers, extraTypes: string[] = []): LiveEventSubscription {
  const source = new EventSource(`${client.baseUrl}/v1.0/me/events`, {
    fetch: (url, init) => client.fetch(url, init as RequestInit),
  });

  for (const type of KnownTypes) {
    source.addEventListener(type, (event: MessageEvent) => {
      const handler = handlers[type] as ((data: unknown) => void) | undefined;
      const data = parse(event.data);
      handler ? handler(data) : handlers.other?.(type, data);
    });
  }

  for (const type of extraTypes) {
    source.addEventListener(type, (event: MessageEvent) => handlers.other?.(type, parse(event.data)));
  }

  source.onerror = (event) => {
    const detail = event as unknown as { code?: number; message?: string };
    handlers.error?.({ status: detail.code, message: detail.message, closed: source.readyState === EventSource.CLOSED });
  };

  return { close: () => source.close() };
}

function parse(data: string): unknown {
  try {
    return JSON.parse(data);
  } catch {
    return data;
  }
}
