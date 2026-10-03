import { HttpErrorResponse } from '@angular/common/http';

/**
 * A readable sentence for a failed API call. The API answers in several shapes — a plain string,
 * a list of Identity errors, or ProblemDetails — and showing the raw body put "[object Object]"
 * on the sign-in pages.
 */
export function httpErrorMessage(error: unknown, fallback: string): string {
  const response = error as Partial<HttpErrorResponse> | null;

  if (response?.status === 429) {
    return 'Too many attempts. Please wait a minute and try again.';
  }
  if (response?.status === 0) {
    return 'Cannot reach the server. Check your connection and try again.';
  }

  const body = response?.error as unknown;
  if (typeof body === 'string' && body.trim()) {
    return body;
  }
  if (Array.isArray(body)) {
    const messages = body
      .map((item) => (typeof item === 'string' ? item : (item as { description?: string })?.description))
      .filter((m): m is string => !!m);
    if (messages.length) {
      return messages.join(' ');
    }
  }
  if (body && typeof body === 'object') {
    const problem = body as { detail?: string; title?: string; errors?: Record<string, string[]> };
    const validation = problem.errors ? Object.values(problem.errors).flat().filter(Boolean) : [];
    if (validation.length) {
      return validation.join(' ');
    }
    if (problem.detail) {
      return problem.detail;
    }
    if (problem.title) {
      return problem.title;
    }
  }
  return fallback;
}
