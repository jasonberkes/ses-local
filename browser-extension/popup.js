const statusEl = document.getElementById('status');
const patInput  = document.getElementById('pat');
const saveBtn   = document.getElementById('save');
const syncBtn   = document.getElementById('sync');
const msgEl     = document.getElementById('msg');

function showMsg(text, isErr = false) {
  msgEl.textContent = text;
  msgEl.className   = isErr ? 'err' : 'msg';
}

async function loadStatus() {
  const res = await chrome.runtime.sendMessage({ type: 'GET_STATUS' });
  const last = res.lastSyncTs
    ? new Date(res.lastSyncTs).toLocaleString()
    : 'Never';
  statusEl.textContent = res.hasPat
    ? `PAT configured. Last sync: ${last}`
    : 'No PAT configured.';
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
