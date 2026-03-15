const API_BASE_CANDIDATES = [
  "http://127.0.0.1:18765/api/v1",
  "http://localhost:18765/api/v1"
];
const TOKEN_KEY = "x_post_archive_token";
const FETCH_TIMEOUT_MS = 180000;
const m3u8ByTab = new Map();
const MAX_M3U8_PER_TAB = 80;
const MAX_VIDEO_PLAYLISTS_PER_SAVE = 5;
const M3U8_MAX_AGE_MS = 10 * 60 * 1000;
let activeApiBase = API_BASE_CANDIDATES[0];

chrome.webRequest.onBeforeRequest.addListener(
  (details) => {
    if (!details.tabId || details.tabId < 0) return;
    if (details.type === "main_frame" && details.url.includes("/status/")) {
      m3u8ByTab.set(details.tabId, []);
      return;
    }
    if (!details.url.includes(".m3u8")) return;

    const items = m3u8ByTab.get(details.tabId) ?? [];
    items.push({
      m3u8_url: details.url,
      page_url: details.documentUrl || details.initiator || "",
      captured_at: Date.now()
    });
    m3u8ByTab.set(details.tabId, items.slice(-MAX_M3U8_PER_TAB));
  },
  { urls: ["<all_urls>"] }
);

chrome.runtime.onMessage.addListener((message, sender, sendResponse) => {
  if (message?.type !== "SAVE_POST") return;

  (async () => {
    try {
      const tabId = sender.tab?.id;
      if (typeof tabId !== "number") {
        throw new Error("TAB_ID_MISSING");
      }

      const screenshot = await chrome.tabs.captureVisibleTab(sender.tab.windowId, {
        format: "jpeg",
        quality: 70
      });

      const apiBase = await resolveApiBase();
      let token = await loadOrBootstrapToken(apiBase);
      const videoPlaylists = selectVideoPlaylistsForTweet(
        tabId,
        message.payload?.url,
        message.payload?.tweet_id
      );
      const payload = {
        ...message.payload,
        screenshot_base64: screenshot,
        video_playlists: videoPlaylists
      };

      const payloadBytes = byteLengthUtf8(JSON.stringify(payload));
      let response = await postSave(payload, token, apiBase);
      if (response.status === 401) {
        token = await loadOrBootstrapToken(apiBase, true);
        response = await postSave(payload, token, apiBase);
      }

      const body = await safeJson(response);
      sendResponse({
        ok: response.ok,
        status: response.status,
        body: {
          ...body,
          debug: {
            api_base: apiBase,
            payload_bytes: payloadBytes,
            video_playlists_count: videoPlaylists.length
          }
        }
      });
    } catch (error) {
      const health = await checkApiHealthAny();
      sendResponse({
        ok: false,
        status: 0,
        body: {
          ok: false,
          error_code: "EXTENSION_ERROR",
          message: String(error),
          can_retry: true,
          debug: {
            api_base: health.apiBase,
            health_ok: health.ok,
            health_status: health.status,
            tried: API_BASE_CANDIDATES,
            hint: "デスクトップアプリまたは API を起動し、18765 ポートに接続できることを確認してください。"
          }
        }
      });
    }
  })();

  return true;
});

chrome.tabs.onRemoved.addListener((tabId) => {
  m3u8ByTab.delete(tabId);
});

async function resolveApiBase() {
  const current = await checkApiHealth(activeApiBase);
  if (current.ok) return activeApiBase;

  for (const apiBase of API_BASE_CANDIDATES) {
    const health = await checkApiHealth(apiBase);
    if (health.ok) {
      activeApiBase = apiBase;
      return apiBase;
    }
  }

  throw new Error("API_UNREACHABLE");
}

async function checkApiHealthAny() {
  for (const apiBase of API_BASE_CANDIDATES) {
    const health = await checkApiHealth(apiBase);
    if (health.ok) {
      activeApiBase = apiBase;
      return { ...health, apiBase };
    }
  }
  return { ok: false, status: 0, apiBase: activeApiBase };
}

async function checkApiHealth(apiBase) {
  const controller = new AbortController();
  const timeout = setTimeout(() => controller.abort(), 3000);
  try {
    const response = await fetch(`${apiBase}/health`, { signal: controller.signal });
    return { ok: response.ok, status: response.status };
  } catch {
    return { ok: false, status: 0 };
  } finally {
    clearTimeout(timeout);
  }
}

async function loadOrBootstrapToken(apiBase, forceRefresh = false) {
  if (forceRefresh) {
    await chrome.storage.local.remove(TOKEN_KEY);
  }

  const stored = await chrome.storage.local.get(TOKEN_KEY);
  if (stored[TOKEN_KEY]) {
    return stored[TOKEN_KEY];
  }

  const response = await fetch(`${apiBase}/auth/bootstrap`, { method: "POST" });
  if (!response.ok) {
    throw new Error(`TOKEN_BOOTSTRAP_FAILED:${response.status}`);
  }

  const body = await response.json();
  if (!body?.token) {
    throw new Error("TOKEN_MISSING");
  }

  await chrome.storage.local.set({ [TOKEN_KEY]: body.token });
  return body.token;
}

async function postSave(payload, token, apiBase) {
  const controller = new AbortController();
  const timeout = setTimeout(() => controller.abort(), FETCH_TIMEOUT_MS);
  try {
    return await fetch(`${apiBase}/posts`, {
      method: "POST",
      headers: {
        "Content-Type": "application/json",
        Authorization: `Bearer ${token}`
      },
      body: JSON.stringify(payload),
      signal: controller.signal
    });
  } finally {
    clearTimeout(timeout);
  }
}

async function safeJson(response) {
  try {
    return await response.json();
  } catch {
    return {
      ok: false,
      error_code: "INVALID_RESPONSE",
      message: `INVALID_JSON_RESPONSE:${response.status}`
    };
  }
}

function byteLengthUtf8(text) {
  return new TextEncoder().encode(text).length;
}

function selectVideoPlaylistsForTweet(tabId, tweetUrl, tweetIdFromPayload) {
  const tweetId = tweetIdFromPayload || extractTweetId(tweetUrl || "");
  if (!tweetId) return [];

  const marker = `/status/${tweetId}`;
  const now = Date.now();
  const seen = new Set();
  const items = m3u8ByTab.get(tabId) ?? [];
  const normalized = items
    .map((item) => (typeof item === "string"
      ? { m3u8_url: item, page_url: "", captured_at: 0 }
      : item))
    .filter((item) => typeof item?.m3u8_url === "string");

  const filtered = normalized.filter((item) => {
    const recent = now - (item.captured_at || 0) <= M3U8_MAX_AGE_MS;
    const related =
      (item.page_url || "").includes(marker) ||
      (item.m3u8_url || "").includes(tweetId);
    return recent && related;
  });

  const output = [];
  for (const item of filtered) {
    if (seen.has(item.m3u8_url)) continue;
    seen.add(item.m3u8_url);
    output.push({ m3u8_url: item.m3u8_url });
    if (output.length >= MAX_VIDEO_PLAYLISTS_PER_SAVE) break;
  }
  return output;
}

function extractTweetId(url) {
  const match = String(url || "").match(/status\/(\d+)/);
  return match ? match[1] : "";
}
