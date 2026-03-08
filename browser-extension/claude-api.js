// Claude.ai API client for browser extension context.
// Cookies are sent automatically by the browser (same-origin).

import { createRateLimiter } from './rate-limiter.js';

const CLAUDE_BASE = 'https://claude.ai';
const rateLimited = createRateLimiter(5);

export async function getOrgId() {
  return rateLimited(async () => {
    try {
      const res = await fetch(`${CLAUDE_BASE}/api/organizations`, { credentials: 'include' });
      if (!res.ok) return null;
      const orgs = await res.json();
      return orgs[0]?.uuid ?? null;
    } catch (err) {
      console.warn('[ses-local] getOrgId failed:', err.message);
      return null;
    }
  });
}

export async function* listConversations(orgId) {
  let offset = 0;
  const limit = 50;
  while (true) {
    try {
      const res = await rateLimited(() =>
        fetch(`${CLAUDE_BASE}/api/organizations/${orgId}/chat_conversations?limit=${limit}&offset=${offset}`,
          { credentials: 'include' })
      );
      if (!res.ok) break;
      const page = await res.json();
      if (!page || page.length === 0) break;
      for (const item of page) yield item;
      if (page.length < limit) break;
      offset += limit;
    } catch (err) {
      console.warn('[ses-local] listConversations failed:', err.message);
      break;
    }
  }
}

export async function getConversation(orgId, uuid) {
  return rateLimited(async () => {
    try {
      const res = await fetch(
        `${CLAUDE_BASE}/api/organizations/${orgId}/chat_conversations/${uuid}`,
        { credentials: 'include' }
      );
      if (!res.ok) return null;
      return await res.json();
    } catch (err) {
      console.warn('[ses-local] getConversation failed:', err.message);
      return null;
    }
  });
}
