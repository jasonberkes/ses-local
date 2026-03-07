// ChatGPT backend API client for browser extension context.
// Cookies are sent automatically by the browser (same-origin).

import { createRateLimiter } from './rate-limiter.js';

const CHATGPT_BASE = 'https://chatgpt.com';
const rateLimited = createRateLimiter(3); // More conservative than Claude's 5 RPS

export async function isLoggedIn() {
  return rateLimited(async () => {
    try {
      const res = await fetch(`${CHATGPT_BASE}/backend-api/me`, { credentials: 'include' });
      return res.ok;
    } catch {
      return false;
    }
  });
}

export async function* listConversations() {
  let offset = 0;
  const limit = 50;
  while (true) {
    const res = await rateLimited(() =>
      fetch(
        `${CHATGPT_BASE}/backend-api/conversations?offset=${offset}&limit=${limit}&order=updated`,
        { credentials: 'include' }
      )
    );
    if (!res.ok) break;
    const data = await res.json();
    const items = data.items ?? [];
    if (items.length === 0) break;
    for (const item of items) yield item;
    if (items.length < limit) break;
    offset += limit;
  }
}

export async function getConversation(conversationId) {
  return rateLimited(async () => {
    const res = await fetch(
      `${CHATGPT_BASE}/backend-api/conversation/${conversationId}`,
      { credentials: 'include' }
    );
    if (!res.ok) return null;
    return await res.json();
  });
}

// Flatten ChatGPT's tree-structured message mapping into a chronological array.
// The mapping is a dict of node IDs; each node has parent/children references.
export function flattenMessages(mapping) {
  if (!mapping) return [];

  const nodes = Object.values(mapping);
  const root = nodes.find(n => !n.parent);
  if (!root) return [];

  const messages = [];
  const queue = [root.id];
  let queueIdx = 0;

  while (queueIdx < queue.length) {
    const nodeId = queue[queueIdx++];
    const node = mapping[nodeId];
    if (!node) continue;

    if (node.message?.content?.parts?.length > 0) {
      const role = node.message.author?.role;
      if (role === 'user' || role === 'assistant') {
        messages.push({
          id:          node.message.id,
          role:        role,
          text:        node.message.content.parts.join('\n'),
          create_time: node.message.create_time
        });
      }
    }

    if (node.children) queue.push(...node.children);
  }

  return messages;
}
