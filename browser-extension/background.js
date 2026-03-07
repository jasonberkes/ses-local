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

// ── Helpers ──────────────────────────────────────────────────────────────────

/** Returns a cutoff Date for incremental sync, or null for a full sync. */
function getSyncCutoff(full, lastSyncTs) {
  if (full) return null;
  return lastSyncTs ? new Date(lastSyncTs) : new Date(Date.now() - 24 * 60 * 60 * 1000);
}

// ── Sync Logic ───────────────────────────────────────────────────────────────

async function runSync({ full }) {
  const { ses_pat: pat } = await chrome.storage.local.get('ses_pat');
  if (!pat) {
    console.debug('[ses-local] No PAT configured — skipping sync');
    return;
  }

  // Run both syncs independently — one failing doesn't block the other
  await Promise.allSettled([
    syncClaude(pat, full),
    syncChatGpt(pat, full)
  ]);
}

async function syncClaude(pat, full) {
  const orgId = await getOrgId();
  if (!orgId) {
    console.debug('[ses-local] Not logged in to Claude.ai or cannot get org');
    return;
  }

  const { last_sync_ts: lastSyncTs } = await chrome.storage.local.get('last_sync_ts');
  const cutoff = getSyncCutoff(full, lastSyncTs);

  console.log('[ses-local] Claude sync starting', { full, cutoff });
  let batch = [];
  let synced = 0;

  for await (const meta of listClaudeConvs(orgId)) {
    if (cutoff && new Date(meta.updated_at) < cutoff) break;

    const conv = await getClaudeConv(orgId, meta.uuid);
    if (!conv) continue;

    batch.push({
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
    });

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

  await chrome.storage.local.set({ last_sync_ts: new Date().toISOString() });
  console.log('[ses-local] Claude sync complete —', synced, 'conversations');
}

async function syncChatGpt(pat, full) {
  const loggedIn = await isChatGptLoggedIn();
  if (!loggedIn) {
    console.debug('[ses-local] Not logged in to ChatGPT — skipping');
    return;
  }

  const { chatgpt_last_sync_ts: lastSyncTs } = await chrome.storage.local.get('chatgpt_last_sync_ts');
  const cutoff = getSyncCutoff(full, lastSyncTs);

  console.log('[ses-local] ChatGPT sync starting', { full, cutoff });
  let batch = [];
  let synced = 0;

  for await (const meta of listChatGptConvs()) {
    // ChatGPT timestamps are Unix epoch seconds — multiply by 1000 for ms
    if (cutoff && new Date(meta.update_time * 1000) < cutoff) break;

    const conv = await getChatGptConv(meta.id);
    if (!conv) continue;

    const messages = flattenMessages(conv.mapping)
      .filter(m => m.text.trim().length > 0)
      .map(m => ({
        uuid:       m.id,
        sender:     m.role === 'user' ? 'human' : 'assistant',
        text:       m.text,
        created_at: m.create_time ? new Date(m.create_time * 1000).toISOString() : null
      }));

    if (messages.length === 0) continue; // skip system-only conversations

    batch.push({
      uuid:       meta.id,
      name:       meta.title ?? 'Untitled',
      created_at: new Date(meta.create_time * 1000).toISOString(),
      updated_at: new Date(meta.update_time * 1000).toISOString(),
      source:     'chatgpt',
      messages
    });

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

  await chrome.storage.local.set({ chatgpt_last_sync_ts: new Date().toISOString() });
  console.log('[ses-local] ChatGPT sync complete —', synced, 'conversations');
}

let _statusCache = null;
let _statusCacheTs = 0;
const STATUS_CACHE_TTL_MS = 60_000;

async function getStatus() {
  const now = Date.now();
  // Return cached login status — storage reads always bypass cache for fresh timestamps
  const data = await chrome.storage.local.get(['ses_pat', 'last_sync_ts', 'chatgpt_last_sync_ts']);
  if (_statusCache && now - _statusCacheTs < STATUS_CACHE_TTL_MS) {
    return { ..._statusCache, lastSyncTs: data.last_sync_ts ?? null, chatgptLastSyncTs: data.chatgpt_last_sync_ts ?? null };
  }
  const [claudeOrgId, chatgptLoggedIn] = await Promise.all([
    getOrgId(),
    isChatGptLoggedIn()
  ]);
  _statusCache = {
    hasPat:          !!data.ses_pat,
    claudeLoggedIn:  claudeOrgId !== null,
    chatgptLoggedIn: chatgptLoggedIn
  };
  _statusCacheTs = now;
  return { ..._statusCache, lastSyncTs: data.last_sync_ts ?? null, chatgptLastSyncTs: data.chatgpt_last_sync_ts ?? null };
}
