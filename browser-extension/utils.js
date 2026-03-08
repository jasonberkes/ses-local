// Shared utilities for the ses-local browser extension.

/**
 * Validates that a PAT has JWT format (3 dot-separated non-empty segments).
 * This is a client-side format check only — actual validation happens server-side.
 */
export function isValidPat(pat) {
  if (!pat || typeof pat !== 'string') return false;
  const parts = pat.trim().split('.');
  return parts.length === 3 && parts.every(p => p.length > 0);
}
