// ChatGPT backend API client for browser extension context.
// Cookies are sent automatically by the browser (same-origin).

const CHATGPT_BASE = 'https://chatgpt.com';
const MAX_RPS = 3;
let _requestQueue = Promise.resolve();

function rateLimited(fn) {
  const slot = _requestQueue.then(() =>
    new Promise(resolve => setTimeout(resolve, 1000 / MAX_RPS))
  ).then(fn);
  // Recover queue on rejection so subsequent calls aren't poisoned
  _requestQueue = slot.catch(() => {});
  return slot;
}

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
      fetch(`${CHATGPT_BASE}/backend-api/conversations?offset=${offset}&limit=${limit}&order=updated`,
        { credentials: 'include' })
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

/**
 * Flatten ChatGPT's message tree (mapping) into chronological array.
 * The mapping is a dict of { nodeId: { id, parent, children, message } }.
 * Walk from root (no parent) through children to build ordered messages.
 * Deduplicates by message ID to handle branching conversations.
 */
export function flattenMessages(mapping) {
  if (!mapping) return [];

  // Find root node (parent is null or undefined)
  const nodes = Object.values(mapping);
  const root = nodes.find(n => !n.parent);
  if (!root) return [];

  const messages = [];
  const seen = new Set();
  const queue = [root.id];
  let head = 0;

  while (head < queue.length) {
    const nodeId = queue[head++];
    const node = mapping[nodeId];
    if (!node) continue;

    if (node.message?.content?.parts?.length > 0) {
      const role = node.message.author?.role;
      if (role === 'user' || role === 'assistant') {
        const msgId = node.message.id ?? nodeId;
        if (!seen.has(msgId)) {
          seen.add(msgId);
          messages.push({
            id: msgId,
            role: role,
            text: node.message.content.parts
              .filter(p => typeof p === 'string')
              .join('\n'),
            create_time: node.message.create_time
          });
        }
      }
    }

    // Add children to queue (first child = main thread)
    if (node.children?.length > 0) {
      queue.push(...node.children);
    }
  }

  return messages;
}
