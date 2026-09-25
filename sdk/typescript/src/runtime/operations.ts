// Long-running work (202 + `/v1.0/operations/{id}`): waits until an operation has finished.
import type { OperationResponse } from '../generated/models/index.js';
import type { PaperDotNetClient } from './client.js';

export interface WaitOptions {
  /** Poll interval in milliseconds (default 1000). */
  interval?: number;
  /** Give up after this many milliseconds (default: never). */
  timeout?: number;
  signal?: AbortSignal;
  onProgress?: (operation: OperationResponse) => void;
}

const Finished = new Set(['succeeded', 'failed', 'canceled', 'cancelled']);

/** Polls an operation until it succeeded, failed or was canceled, and returns its last state. */
export async function waitForOperation(client: PaperDotNetClient, id: string, options: WaitOptions = {}): Promise<OperationResponse> {
  const started = Date.now();
  for (;;) {
    options.signal?.throwIfAborted();
    const operation = await client.api.v10.operations.byId(id).get();
    if (!operation) {
      throw new Error(`Operation ${id} was not found.`);
    }

    options.onProgress?.(operation);
    if (operation.status && Finished.has(operation.status)) {
      return operation;
    }

    if (options.timeout !== undefined && Date.now() - started > options.timeout) {
      throw new Error(`Operation ${id} did not finish within ${options.timeout} ms.`);
    }

    await new Promise((resolve) => setTimeout(resolve, options.interval ?? 1000));
  }
}

/** The operation id of a 202 response's Location (`/v1.0/operations/{id}`). */
export function operationIdOf(location: string | null | undefined): string | undefined {
  return location ? /\/operations\/([0-9a-f-]{36})/i.exec(location)?.[1] : undefined;
}
