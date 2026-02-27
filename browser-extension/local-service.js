const LOCAL_URL = 'http://localhost:37780/api/sync/conversations';

export async function postConversations(conversations, pat) {
  try {
    const res = await fetch(LOCAL_URL, {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
        'Authorization': `Bearer ${pat}`
      },
      body: JSON.stringify({ conversations })
    });
    if (res.status === 401) {
      console.warn('[ses-local] Unauthorized — PAT may be invalid');
      return false;
    }
    return res.ok;
  } catch (e) {
    // ses-local not running — normal when user hasn't started it yet
    console.debug('[ses-local] Local service unreachable:', e.message);
    return false;
  }
}
