const API_BASE = "http://127.0.0.1:18765/api/v1";
const TOKEN_KEY = "x_post_archive_token";
const m3u8ByTab = new Map();

chrome.webRequest.onBeforeRequest.addListener(
  (details) => {
    if (!details.tabId || details.tabId < 0) return;
    if (!details.url.includes(".m3u8")) return;

    const items = m3u8ByTab.get(details.tabId) ?? [];
    items.push(details.url);
    m3u8ByTab.set(details.tabId, items.slice(-20));
  },
  { urls: ["<all_urls>"] }
);

chrome.runtime.onMessage.addListener((message, sender, sendResponse) => {
  if (message?.type !== "SAVE_POST") return;

  (async () => {
    try {
      const tabId = sender.tab?.id;
      if (typeof tabId !== "number") {
        throw new Error("タブIDが取得できませんでした。");
      }

      const screenshot = await chrome.tabs.captureVisibleTab(sender.tab.windowId, { format: "png" });
      let token = await loadOrBootstrapToken();
      const videoUrls = (m3u8ByTab.get(tabId) ?? []).map((m3u8_url) => ({ m3u8_url }));

      const payload = {
        ...message.payload,
        screenshot_base64: screenshot,
        video_playlists: videoUrls
      };

      let response = await postSave(payload, token);
      if (response.status === 401) {
        token = await loadOrBootstrapToken(true);
        response = await postSave(payload, token);
      }

      const body = await safeJson(response);
      sendResponse({ ok: response.ok, body, status: response.status });
    } catch (error) {
      sendResponse({ ok: false, status: 0, body: { ok: false, error_code: "EXTENSION_ERROR", message: String(error), can_retry: true } });
    }
  })();

  return true;
});

chrome.tabs.onRemoved.addListener((tabId) => {
  m3u8ByTab.delete(tabId);
});

async function loadOrBootstrapToken(forceRefresh = false) {
  if (forceRefresh) {
    await chrome.storage.local.remove(TOKEN_KEY);
  }

  const stored = await chrome.storage.local.get(TOKEN_KEY);
  if (stored[TOKEN_KEY]) {
    return stored[TOKEN_KEY];
  }

  const response = await fetch(`${API_BASE}/auth/bootstrap`, { method: "POST" });
  if (!response.ok) {
    throw new Error(`認証トークンの初期化に失敗しました: ${response.status}`);
  }

  const body = await response.json();
  if (!body?.token) {
    throw new Error("認証トークンの取得結果が不正です。");
  }

  await chrome.storage.local.set({ [TOKEN_KEY]: body.token });
  return body.token;
}

async function postSave(payload, token) {
  return fetch(`${API_BASE}/posts`, {
    method: "POST",
    headers: {
      "Content-Type": "application/json",
      "Authorization": `Bearer ${token}`
    },
    body: JSON.stringify(payload)
  });
}

async function safeJson(response) {
  try {
    return await response.json();
  } catch {
    return {
      ok: false,
      error_code: "INVALID_RESPONSE",
      message: `APIレスポンスの解析に失敗しました: ${response.status}`
    };
  }
}
