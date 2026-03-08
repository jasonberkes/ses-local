const patInput   = document.getElementById('pat');
const saveBtn    = document.getElementById('save');
const syncBtn    = document.getElementById('sync');
const msgEl      = document.getElementById('msg');
const claudeDot  = document.getElementById('claude-dot');
const claudeLabel = document.getElementById('claude-label');
const chatgptDot  = document.getElementById('chatgpt-dot');
const chatgptLabel = document.getElementById('chatgpt-label');
const claudeSyncTime  = document.getElementById('claude-sync-time');
const chatgptSyncTime = document.getElementById('chatgpt-sync-time');

function showMsg(text, isErr = false) {
  msgEl.textContent = text;
  msgEl.className   = isErr ? 'err' : 'msg';
}

function fmtTime(ts) {
  return ts ? new Date(ts).toLocaleString() : 'Never';
}

async function loadStatus() {
  const res = await chrome.runtime.sendMessage({ type: 'GET_STATUS' });

  if (res.hasPat) {
    claudeLabel.textContent  = 'Claude.ai: ● Connected';
    claudeDot.className      = 'dot dot-green';
    chatgptLabel.textContent = 'ChatGPT: ● Connected';
    chatgptDot.className     = 'dot dot-green';
  } else {
    claudeLabel.textContent  = 'Claude.ai: ○ No PAT configured';
    claudeDot.className      = 'dot dot-grey';
    chatgptLabel.textContent = 'ChatGPT: ○ No PAT configured';
    chatgptDot.className     = 'dot dot-grey';
  }

  claudeSyncTime.textContent  = `Claude.ai last sync: ${fmtTime(res.claudeLastSyncTs)}`;
  chatgptSyncTime.textContent = `ChatGPT last sync: ${fmtTime(res.chatgptLastSyncTs)}`;
}

function isValidPat(pat) {
  // PATs are JWTs — must have 3 dot-separated base64 segments
  if (!pat || typeof pat !== 'string') return false;
  const parts = pat.trim().split('.');
  return parts.length === 3 && parts.every(p => p.length > 0);
}

saveBtn.addEventListener('click', async () => {
  const pat = patInput.value.trim();
  if (!pat) { showMsg('Enter a PAT first', true); return; }
  if (!isValidPat(pat)) { showMsg('Invalid PAT format — expected a JWT token (xxx.yyy.zzz)', true); return; }
  await chrome.storage.local.set({ ses_pat: pat });
  patInput.value = '';
  showMsg('PAT saved.');
  await loadStatus();
});

syncBtn.addEventListener('click', async () => {
  syncBtn.disabled = true;
  syncBtn.textContent = 'Syncing...';
  showMsg('');
  const res = await chrome.runtime.sendMessage({ type: 'TRIGGER_SYNC', full: false });
  syncBtn.disabled = false;
  syncBtn.textContent = 'Sync Now';
  showMsg(res?.ok ? 'Sync triggered.' : 'Sync started (check background logs).');
  await loadStatus();
});

loadStatus();
