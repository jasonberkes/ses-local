// Claude.ai API client for browser extension context.
// Cookies are sent automatically by the browser (same-origin).

const CLAUDE_BASE = 'https://claude.ai';
const MAX_RPS = 5;
let _requestQueue = Promise.resolve();

function rateLimited(fn) {
  _requestQueue = _requestQueue.then(() =>
    new Promise(resolve => setTimeout(resolve, 1000 / MAX_RPS))
  ).then(fn);
  return _requestQueue;
}

export async function getOrgId() {
  return rateLimited(async () => {
    const res = await fetch(`${CLAUDE_BASE}/api/organizations`, { credentials: 'include' });
    if (!res.ok) return null;
    const orgs = await res.json();
    return orgs[0]?.uuid ?? null;
  });
}

export async function* listConversations(orgId) {
  let offset = 0;
  const limit = 50;
  while (true) {
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
  }
}

export async function getConversation(orgId, uuid) {
  return rateLimited(async () => {
    const res = await fetch(
      `${CLAUDE_BASE}/api/organizations/${orgId}/chat_conversations/${uuid}`,
      { credentials: 'include' }
    );
    if (!res.ok) return null;
    return await res.json();
  });
}
