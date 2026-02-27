import { getOrgId, listConversations, getConversation } from './claude-api.js';
import { postConversations } from './local-service.js';

const ALARM_NAME = 'ses-local-sync';
const POLL_MINUTES = 5;
const BATCH_SIZE = 20;

// ── Lifecycle ────────────────────────────────────────────────────────────────

chrome.runtime.onInstalled.addListener(async ({ reason }) => {
  console.log('[ses-local] Installed, reason:', reason);
  chrome.alarms.create(ALARM_NAME, { periodInMinutes: POLL_MINUTES });

  if (reason === 'install') {
    // First install: run full bulk sync
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

  const orgId = await getOrgId();
  if (!orgId) {
    console.debug('[ses-local] Not logged in to Claude.ai or cannot get org');
    return;
  }

  const { last_sync_ts: lastSyncTs } = await chrome.storage.local.get('last_sync_ts');
  const cutoff = full ? null : (lastSyncTs ? new Date(lastSyncTs) : new Date(Date.now() - 24 * 60 * 60 * 1000));

  console.log('[ses-local] Starting sync', { full, cutoff });
  let batch = [];
  let synced = 0;

  for await (const meta of listConversations(orgId)) {
    // Stop if we've gone past the cutoff (list is newest-first)
    if (cutoff && new Date(meta.updated_at) < cutoff) break;

    const full_conv = await getConversation(orgId, meta.uuid);
    if (!full_conv) continue;

    batch.push({
      uuid:       full_conv.uuid,
      name:       full_conv.name,
      created_at: full_conv.created_at,
      updated_at: full_conv.updated_at,
      messages:   (full_conv.chat_messages ?? []).map(m => ({
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
  console.log('[ses-local] Sync complete —', synced, 'conversations');
}

async function getStatus() {
  const data = await chrome.storage.local.get(['ses_pat', 'last_sync_ts']);
  return {
    hasPat:      !!data.ses_pat,
    lastSyncTs:  data.last_sync_ts ?? null
  };
}
