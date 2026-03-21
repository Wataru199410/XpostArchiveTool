const API_BASE_CANDIDATES = [
  "http://127.0.0.1:18765/api/v1",
  "http://localhost:18765/api/v1"
];
const TOKEN_KEY = "x_post_archive_token";
const FETCH_TIMEOUT_MS = 600000;
const m3u8ByTab = new Map();
const MAX_M3U8_PER_TAB = 80;
const MAX_VIDEO_PLAYLISTS_PER_SAVE = 5;
const M3U8_MAX_AGE_MS = 10 * 60 * 1000;
const VIDEO_FALLBACK_WINDOW_MS = 60 * 1000;
let activeApiBase = API_BASE_CANDIDATES[0];

chrome.webRequest.onBeforeRequest.addListener(
  (details) => {
    if (!details.tabId || details.tabId < 0) return;
    if (details.type === "main_frame" && details.url.includes("/status/")) {
      m3u8ByTab.set(details.tabId, []);
      return;
    }
    const isM3u8 = details.url.includes(".m3u8");
    const isDirectVideoFile = /\.(mp4|m4v|mov|webm|ts|mkv)(\?|$)/i.test(details.url);
    const isKnownVideoPath = /\/(ext_tw_video|amplify_video)\//i.test(details.url);
    if (!isM3u8 && !(isDirectVideoFile && isKnownVideoPath)) return;

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
  if (message?.type !== "SAVE_POST" && message?.type !== "GET_TAG_CATALOG" && message?.type !== "GET_POST_STATUS" && message?.type !== "UPDATE_POST") return;

  (async () => {
    try {
      if (message?.type === "GET_TAG_CATALOG") {
        const apiBase = await resolveApiBase();
        let token = await loadOrBootstrapToken(apiBase);
        let response = await fetchTagCatalog(apiBase, token);
        if (response.status === 401) {
          token = await loadOrBootstrapToken(apiBase, true);
          response = await fetchTagCatalog(apiBase, token);
        }

        const body = await safeJson(response);
        sendResponse({
          ok: response.ok,
          status: response.status,
          body: {
            ...body,
            debug: {
              api_base: apiBase
            }
          }
        });
        return;
      }

      if (message?.type === "GET_POST_STATUS") {
        const apiBase = await resolveApiBase();
        let token = await loadOrBootstrapToken(apiBase);
        let response = await fetchPostStatus(apiBase, token, message.tweetId);
        if (response.status === 401) {
          token = await loadOrBootstrapToken(apiBase, true);
          response = await fetchPostStatus(apiBase, token, message.tweetId);
        }

        const body = await safeJson(response);
        sendResponse({
          ok: response.ok,
          status: response.status,
          body: {
            ...body,
            debug: {
              api_base: apiBase
            }
          }
        });
        return;
      }

      if (message?.type === "UPDATE_POST") {
        const apiBase = await resolveApiBase();
        let token = await loadOrBootstrapToken(apiBase);
        let response = await updatePost(message.payload, token, apiBase);
        if (response.status === 401) {
          token = await loadOrBootstrapToken(apiBase, true);
          response = await updatePost(message.payload, token, apiBase);
        }

        const body = await safeJson(response);
        sendResponse({
          ok: response.ok,
          status: response.status,
          body: {
            ...body,
            debug: {
              api_base: apiBase,
              payload_bytes: byteLengthUtf8(JSON.stringify(message.payload || {}))
            }
          }
        });
        return;
      }

      const tabId = sender.tab?.id;
      if (typeof tabId !== "number") {
        throw new Error("TAB_ID_MISSING");
      }

      const apiBase = await resolveApiBase();
      let token = await loadOrBootstrapToken(apiBase);
      const videoPlaylists = selectVideoPlaylistsForTweet(
        tabId,
        message.payload?.url,
        message.payload?.tweet_id,
        message.payload?.video_context
      );
      const payload = {
        ...message.payload,
        video_playlists: videoPlaylists
      };
      delete payload.video_context;

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
      const message = error?.name === "AbortError"
        ? "REQUEST_TIMEOUT: 動画保存に時間がかかり、拡張の待機時間を超えました。"
        : String(error);
      sendResponse({
        ok: false,
        status: 0,
        body: {
          ok: false,
          error_code: "EXTENSION_ERROR",
          message,
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

  const controller = new AbortController();
  const timeout = setTimeout(() => controller.abort(), 10000);
  let response;
  try {
    response = await fetch(`${apiBase}/auth/bootstrap`, { method: "POST", signal: controller.signal });
  } finally {
    clearTimeout(timeout);
  }
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

async function fetchTagCatalog(apiBase, token) {
  const controller = new AbortController();
  const timeout = setTimeout(() => controller.abort(), FETCH_TIMEOUT_MS);
  try {
    return await fetch(`${apiBase}/tags`, {
      method: "GET",
      headers: {
        Authorization: `Bearer ${token}`
      },
      signal: controller.signal
    });
  } finally {
    clearTimeout(timeout);
  }
}

async function fetchPostStatus(apiBase, token, tweetId) {
  const controller = new AbortController();
  const timeout = setTimeout(() => controller.abort(), FETCH_TIMEOUT_MS);
  try {
    return await fetch(`${apiBase}/posts/${encodeURIComponent(tweetId)}`, {
      method: "GET",
      headers: {
        Authorization: `Bearer ${token}`
      },
      signal: controller.signal
    });
  } finally {
    clearTimeout(timeout);
  }
}

async function updatePost(payload, token, apiBase) {
  const controller = new AbortController();
  const timeout = setTimeout(() => controller.abort(), FETCH_TIMEOUT_MS);
  try {
    return await fetch(`${apiBase}/posts/${encodeURIComponent(payload?.tweet_id || "")}`, {
      method: "PUT",
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

function selectVideoPlaylistsForTweet(tabId, tweetUrl, tweetIdFromPayload, videoContext) {
  const tweetId = tweetIdFromPayload || extractTweetId(tweetUrl || "");
  if (!tweetId) return [];

  const marker = `/status/${tweetId}`;
  const now = Date.now();
  const seen = new Set();
  const output = [];
  const hasVideo = videoContext?.has_video === true;

  for (const candidateUrl of normalizeDirectVideoCandidates(videoContext)) {
    if (seen.has(candidateUrl)) continue;
    seen.add(candidateUrl);
    output.push({ m3u8_url: candidateUrl });
    return output;
  }

  if (!hasVideo) {
    return output;
  }

  const items = m3u8ByTab.get(tabId) ?? [];
  const normalized = items
    .map((item) => (typeof item === "string"
      ? { m3u8_url: item, page_url: "", captured_at: 0 }
      : item))
    .filter((item) => typeof item?.m3u8_url === "string");

  const recentItems = normalized.filter((item) => {
    const recent = now - (item.captured_at || 0) <= M3U8_MAX_AGE_MS;
    return recent;
  });

  const filtered = recentItems.filter((item) =>
    (item.page_url || "").includes(marker) ||
    (item.m3u8_url || "").includes(tweetId)
  );

  const candidates = (filtered.length > 0
    ? filtered
    : recentItems.filter((item) => now - (item.captured_at || 0) <= VIDEO_FALLBACK_WINDOW_MS))
    .slice()
    .sort((a, b) => (b.captured_at || 0) - (a.captured_at || 0))
    .slice(0, MAX_VIDEO_PLAYLISTS_PER_SAVE);

  for (const item of candidates) {
    if (seen.has(item.m3u8_url)) continue;
    seen.add(item.m3u8_url);
    output.push({ m3u8_url: item.m3u8_url });
    break;
  }
  return output;
}

function normalizeDirectVideoCandidates(videoContext) {
  const values = Array.isArray(videoContext?.candidate_urls) ? videoContext.candidate_urls : [];
  const normalized = [];
  const seen = new Set();

  for (const value of values) {
    const url = String(value || "").trim();
    if (!url || seen.has(url)) continue;
    seen.add(url);

    if (!/^https:/i.test(url)) continue;

    let parsed;
    try {
      parsed = new URL(url);
    } catch {
      continue;
    }

    const host = parsed.host.toLowerCase();
    if (!(host === "video.twimg.com" || host.endsWith(".twimg.com"))) continue;

    const path = parsed.pathname.toLowerCase();
    const isLikelyVideo =
      path.endsWith(".m3u8") ||
      /\.(mp4|m4v|mov|webm|ts|mkv)$/i.test(path) ||
      path.includes("/ext_tw_video/") ||
      path.includes("/amplify_video/");
    if (!isLikelyVideo) continue;

    normalized.push(url);
  }

  return normalized;
}

function extractTweetId(url) {
  const match = String(url || "").match(/status\/(\d+)/);
  return match ? match[1] : "";
}
