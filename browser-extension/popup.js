const claudeDot     = document.getElementById('claude-dot');
const claudeStatus  = document.getElementById('claude-status');
const chatgptDot    = document.getElementById('chatgpt-dot');
const chatgptStatus = document.getElementById('chatgpt-status');
const patInput      = document.getElementById('pat');
const saveBtn       = document.getElementById('save');
const syncBtn       = document.getElementById('sync');
const msgEl         = document.getElementById('msg');

function showMsg(text, isErr = false) {
  msgEl.textContent = text;
  msgEl.className   = isErr ? 'err' : 'msg';
}

function formatLastSync(ts) {
  if (!ts) return 'Never synced';
  const diffMin = Math.floor((Date.now() - new Date(ts).getTime()) / 60000);
  if (diffMin < 1)  return 'Just now';
  if (diffMin < 60) return `${diffMin} min ago`;
  const diffHr = Math.floor(diffMin / 60);
  return `${diffHr} hr ago`;
}

function setServiceStatus(dot, statusEl, connected, lastSyncTs) {
  if (connected) {
    dot.classList.add('green');
    statusEl.textContent = `Connected · ${formatLastSync(lastSyncTs)}`;
  } else {
    dot.classList.remove('green');
    statusEl.textContent = 'Not logged in';
  }
}

async function loadStatus() {
  const res = await chrome.runtime.sendMessage({ type: 'GET_STATUS' });
  setServiceStatus(claudeDot, claudeStatus, res.claudeLoggedIn ?? false, res.lastSyncTs);
  setServiceStatus(chatgptDot, chatgptStatus, res.chatgptLoggedIn ?? false, res.chatgptLastSyncTs);
}

saveBtn.addEventListener('click', async () => {
  const pat = patInput.value.trim();
  if (!pat) { showMsg('Enter a PAT first', true); return; }
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
