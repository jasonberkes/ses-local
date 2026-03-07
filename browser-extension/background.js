import { getOrgId, listConversations as listClaudeConvs, getConversation as getClaudeConv } from './claude-api.js';
import { isLoggedIn as isChatGptLoggedIn, listConversations as listChatGptConvs, getConversation as getChatGptConv, flattenMessages } from './chatgpt-api.js';
import { postConversations } from './local-service.js';

const ALARM_NAME = 'ses-local-sync';
const POLL_MINUTES = 5;
const BATCH_SIZE = 20;

// ── Lifecycle ────────────────────────────────────────────────────────────────

chrome.runtime.onInstalled.addListener(async ({ reason }) => {
  console.log('[ses-local] Installed, reason:', reason);
  chrome.alarms.create(ALARM_NAME, { periodInMinutes: POLL_MINUTES });

  if (reason === 'install') {
    await runSync({ full: true });
  }
});

chrome.alarms.onAlarm.addListener(async (alarm) => {
  if (alarm.name === ALARM_NAME) {
    await runSync({ full: false });
  }
});

// Manual trigger from popup
chrome.runtime.onMessage.addListener((msg, _sender, sendResponse) => {
  if (msg.type === 'TRIGGER_SYNC') {
    runSync({ full: msg.full ?? false }).then(() => sendResponse({ ok: true }));
    return true; // async response
  }
  if (msg.type === 'GET_STATUS') {
    getStatus().then(sendResponse);
    return true;
  }
});

// ── Sync Logic ───────────────────────────────────────────────────────────────

async function runSync({ full }) {
  const { ses_pat: pat } = await chrome.storage.local.get('ses_pat');
  if (!pat) {
    console.debug('[ses-local] No PAT configured — skipping sync');
    return;
  }

  // Run both syncs independently — one failing does not block the other
  const results = await Promise.allSettled([
    syncClaude({ full, pat }),
    syncChatGpt({ full, pat })
  ]);

  for (const r of results) {
    if (r.status === 'rejected') console.warn('[ses-local] Sync failed:', r.reason);
  }
}

// Generic sync loop shared by all sources. Each source provides:
//   storageKey  — chrome.storage key for last sync timestamp
//   getItems    — async generator yielding conversation metadata
//   getCutoff   — (meta) => value accepted by new Date() for cutoff comparison
//   getDetail   — (meta) => Promise<conv | null>
//   transform   — (conv) => unified conversation object for the daemon
async function syncSource({ name, storageKey, getItems, getCutoff, getDetail, transform, full, pat }) {
  const { [storageKey]: lastSyncTs } = await chrome.storage.local.get(storageKey);
  const cutoff = full ? null : (lastSyncTs ? new Date(lastSyncTs) : new Date(Date.now() - 24 * 60 * 60 * 1000));

  console.log(`[ses-local] Starting ${name} sync`, { full, cutoff });
  let batch = [];
  let synced = 0;

  for await (const meta of getItems()) {
    if (cutoff && new Date(getCutoff(meta)) < cutoff) break;

    const conv = await getDetail(meta);
    if (!conv) continue;

    batch.push(transform(conv));

    if (batch.length >= BATCH_SIZE) {
      await postConversations(batch, pat);
      synced += batch.length;
      batch = [];
    }
  }

  if (batch.length > 0) {
    await postConversations(batch, pat);
    synced += batch.length;
  }

  await chrome.storage.local.set({ [storageKey]: new Date().toISOString() });
  console.log(`[ses-local] ${name} sync complete —`, synced, 'conversations');
}

async function syncClaude({ full, pat }) {
  const orgId = await getOrgId();
  if (!orgId) {
    console.debug('[ses-local] Not logged in to Claude.ai or cannot get org');
    return;
  }

  await syncSource({
    name:       'Claude',
    storageKey: 'last_sync_ts',
    getItems:   () => listClaudeConvs(orgId),
    getCutoff:  meta => meta.updated_at,
    getDetail:  meta => getClaudeConv(orgId, meta.uuid),
    transform:  conv => ({
      uuid:       conv.uuid,
      name:       conv.name,
      created_at: conv.created_at,
      updated_at: conv.updated_at,
      source:     'claude_ai',
      messages:   (conv.chat_messages ?? []).map(m => ({
        uuid:       m.uuid,
        sender:     m.sender,
        text:       m.text ?? '',
        created_at: m.created_at
      }))
    }),
    full,
    pat
  });
}

async function syncChatGpt({ full, pat }) {
  if (!await isChatGptLoggedIn()) {
    console.debug('[ses-local] Not logged in to ChatGPT — skipping');
    return;
  }

  await syncSource({
    name:       'ChatGPT',
    storageKey: 'chatgpt_last_sync_ts',
    getItems:   listChatGptConvs,
    getCutoff:  meta => meta.update_time * 1000, // ChatGPT timestamps are Unix epoch seconds
    getDetail:  meta => getChatGptConv(meta.id),
    transform:  conv => ({
      uuid:       conv.id,
      name:       conv.title ?? 'Untitled',
      created_at: new Date(conv.create_time * 1000).toISOString(),
      updated_at: new Date(conv.update_time * 1000).toISOString(),
      source:     'chatgpt',
      messages:   flattenMessages(conv.mapping).map(m => ({
        uuid:       m.id,
        sender:     m.role === 'user' ? 'human' : 'assistant',
        text:       m.text,
        created_at: m.create_time ? new Date(m.create_time * 1000).toISOString() : null
      }))
    }),
    full,
    pat
  });
}

async function getStatus() {
  const data = await chrome.storage.local.get(['ses_pat', 'last_sync_ts', 'chatgpt_last_sync_ts']);
  return {
    hasPat:            !!data.ses_pat,
    claudeLastSyncTs:  data.last_sync_ts ?? null,
    chatgptLastSyncTs: data.chatgpt_last_sync_ts ?? null
  };
}
