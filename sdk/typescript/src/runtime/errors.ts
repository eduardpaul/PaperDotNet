// Every failed call throws the generated ApiProblem (RFC 9457 with PaperDotNet's `code` and validation `errors`).
import type { ApiProblem } from '../generated/models/index.js';

/** The problem details of a failed call, or undefined when the error is not an API problem (e.g. no network). */
export function problemOf(error: unknown): (ApiProblem & { responseStatusCode?: number }) | undefined {
  return typeof error === 'object' && error !== null && 'responseStatusCode' in error ? (error as ApiProblem & { responseStatusCode?: number }) : undefined;
}

/** True when the call failed with this HTTP status. */
export function isStatus(error: unknown, status: number): boolean {
  return problemOf(error)?.responseStatusCode === status;
}

/** Validation messages by field (`errors` of a 400 problem); empty when there are none. */
export function validationErrors(error: unknown): Record<string, string[]> {
  const errors = problemOf(error)?.errors?.additionalData ?? {};
  return Object.fromEntries(Object.entries(errors).map(([key, value]) => [key, Array.isArray(value) ? value.map(String) : [String(value)]]));
}

/**
 * Throws the problem of a failed response read with plain fetch (downloads, event streams), shaped like the errors of
 * the generated client so `problemOf`, `isStatus` and `validationErrors` work on both.
 */
export async function throwProblem(response: Response): Promise<never> {
  let body: Record<string, unknown> = {};
  try {
    body = (await response.json()) as Record<string, unknown>;
  } catch {
    // Not a problem document (e.g. a proxy error page).
  }

  const error = new Error(typeof body.title === 'string' ? body.title : `HTTP ${response.status}`) as Error & ApiProblem & { responseStatusCode: number };
  Object.assign(error, body);
  error.responseStatusCode = response.status;
  error.errors = typeof body.errors === 'object' && body.errors !== null ? { additionalData: body.errors as Record<string, unknown> } : undefined;
  throw error;
}
