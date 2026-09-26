import { problemOf, validationErrors } from '@paperdotnet/client';

/** A sentence for the user from an API problem (or any error). */
export function problemMessage(error: unknown, fallback = 'Something went wrong. Please try again.'): string {
  const problem = problemOf(error);
  if (problem) {
    const first = Object.values(validationErrors(error))[0]?.[0];
    return first ?? problem.detail ?? problem.title ?? fallback;
  }

  if (error instanceof TypeError) return 'The server cannot be reached. Check your connection.';
  return fallback;
}
