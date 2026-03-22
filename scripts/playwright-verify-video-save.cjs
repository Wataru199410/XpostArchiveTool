const fs = require("fs");
const fsp = require("fs/promises");
const os = require("os");
const path = require("path");
const { spawn, spawnSync } = require("child_process");
const { chromium } = require("playwright");

const ROOT = process.cwd();
const API_BASE = "http://127.0.0.1:18765/api/v1";
const TWEET_URL =
  process.env.TWEET_URL ||
  "https://x.com/gkmas_official/status/2034917872464712107";
const TWEET_ID = (TWEET_URL.match(/status\/(\d+)/) || [])[1];
const USER_DATA_DIR =
  process.env.PLAYWRIGHT_CHROME_USER_DATA_DIR ||
  path.join(process.env.LOCALAPPDATA || "", "Google", "Chrome", "User Data");
const PROFILE_DIR = process.env.PLAYWRIGHT_CHROME_PROFILE_DIRECTORY || "Default";
const HEADLESS = process.env.PLAYWRIGHT_HEADLESS !== "0";
const CONTENT_JS = fs
  .readFileSync(path.join(ROOT, "content.js"), "utf8")
  .replace(
    "boot();",
    "if (document.documentElement) { boot(); } else { document.addEventListener('DOMContentLoaded', () => boot(), { once: true }); }"
  );
const FFMPEG_PATH = path.join(ROOT, "third_party", "ffmpeg", "ffmpeg.exe");

async function main() {
  if (!TWEET_ID) throw new Error(`Invalid TWEET_URL: ${TWEET_URL}`);
  if (!fs.existsSync(USER_DATA_DIR)) throw new Error(`Chrome profile not found: ${USER_DATA_DIR}`);

  const token = await bootstrapToken();
  const existing = await getPostStatus(token, TWEET_ID);
  if (existing.exists) {
    deleteSavedPost(TWEET_ID);
  }

  const profileSnapshot = await prepareUserDataSnapshot(USER_DATA_DIR, PROFILE_DIR);
  const context = await chromium.launchPersistentContext(profileSnapshot, {
    channel: "chrome",
    headless: HEADLESS,
    viewport: { width: 1365, height: 960 },
    args: [`--profile-directory=${PROFILE_DIR}`]
  });

  try {
    const page = context.pages()[0] || (await context.newPage());
    const observedVideoUrls = [];
    page.on("pageerror", (error) => {
      console.error("PAGE_ERROR", error);
    });
    page.on("console", (msg) => {
      if (msg.type() === "error") {
        console.error("PAGE_CONSOLE_ERROR", msg.text());
      }
    });
    page.on("request", (request) => {
      const url = request.url();
      if (url.includes("video.twimg.com")) {
        observedVideoUrls.push(url);
        if (observedVideoUrls.length > 400) {
          observedVideoUrls.splice(0, observedVideoUrls.length - 400);
        }
      }
    });
    await installBridge(page, token, observedVideoUrls);
    await page.goto(TWEET_URL, { waitUntil: "domcontentloaded", timeout: 120000 });
    await page.waitForTimeout(5000);

    if (page.url().includes("/i/flow/login")) {
      throw new Error("Chrome profile is not logged in to x.com.");
    }

    await page.waitForTimeout(3000);
    await page.evaluate(() => {
      if (typeof scanArticles === "function") {
        scanArticles();
      }
    });
    await page.waitForTimeout(3000);

    const buttonCount = await page.evaluate(() => document.querySelectorAll(".x-post-archive-inline-save").length);
    if (buttonCount !== 1) {
      const debug = await page.evaluate(() => ({
        articleCount: document.querySelectorAll("article").length,
        hasStyle: !!document.getElementById("x-post-archive-inline-style"),
        hasFunction: typeof scanArticles,
        labels: [...document.querySelectorAll(".x-post-archive-inline-save")].map((b) => b.textContent)
      }));
      throw new Error(`Expected exactly one save button, found ${buttonCount}. debug=${JSON.stringify(debug)}`);
    }

    await page.keyboard.press("Escape").catch(() => {});
    await page.waitForTimeout(500);
    await page.evaluate(() => {
      const button = document.querySelector(".x-post-archive-inline-save");
      if (!(button instanceof HTMLButtonElement)) {
        throw new Error("save button not found");
      }
      button.click();
    });
    await page.waitForSelector("#x-post-archive-modal", { timeout: 30000 });
    await page.click(".x-post-archive-footer-button.primary");

    await waitForToast(page);

    const saved = await waitForSavedPost(token, TWEET_ID);
    const videoFile = await waitForVideoFile(saved.post.dir_path);
    await validateVideoFile(videoFile);

    console.log("VERIFY_OK");
    console.log(`tweet_id=${TWEET_ID}`);
    console.log(`dir_path=${saved.post.dir_path}`);
    console.log(`video_file=${videoFile}`);
  } finally {
    await context.close();
    await fsp.rm(profileSnapshot, { recursive: true, force: true });
  }
}

async function bootstrapToken() {
  const response = await fetch(`${API_BASE}/auth/bootstrap`, { method: "POST" });
  const body = await response.json();
  if (!response.ok || !body?.token) {
    throw new Error(`bootstrap failed: status=${response.status} body=${JSON.stringify(body)}`);
  }
  return body.token;
}

async function apiFetch(token, method, url, body) {
  const response = await fetch(url, {
    method,
    headers: {
      Authorization: `Bearer ${token}`,
      "Content-Type": "application/json"
    },
    body: body ? JSON.stringify(body) : undefined
  });
  let json = {};
  try {
    json = await response.json();
  } catch {
  }
  return { ok: response.ok, status: response.status, body: json };
}

async function getPostStatus(token, tweetId) {
  return await apiFetch(token, "GET", `${API_BASE}/posts/${encodeURIComponent(tweetId)}`);
}

function deleteSavedPost(tweetId) {
  const dbPath = path.join(ROOT, "data", "archive.db");
  const exePath = path.join(ROOT, "tools", "DbDeletePost", "bin", "Debug", "net8.0", "DbDeletePost.exe");
  if (!fs.existsSync(exePath)) {
    throw new Error(`DbDeletePost.exe not found: ${exePath}`);
  }
  const result = spawnSync(exePath, [dbPath, tweetId], { cwd: ROOT, encoding: "utf8" });
  if (result.status !== 0) {
    throw new Error(`delete failed: ${result.stdout}\n${result.stderr}`);
  }
  const postDir = path.join(ROOT, "XArchive", `tweet-${tweetId}`);
  fs.rmSync(postDir, { recursive: true, force: true });
}

async function prepareUserDataSnapshot(sourceUserDataDir, profileDir) {
  const snapshotRoot = await fsp.mkdtemp(path.join(os.tmpdir(), "xpost-playwright-"));
  const sourceProfile = path.join(sourceUserDataDir, profileDir);
  const targetProfile = path.join(snapshotRoot, profileDir);

  await copyIfExists(path.join(sourceUserDataDir, "Local State"), path.join(snapshotRoot, "Local State"));
  await copyDirBestEffort(sourceProfile, targetProfile);
  return snapshotRoot;
}

async function copyIfExists(src, dst) {
  try {
    await fsp.mkdir(path.dirname(dst), { recursive: true });
    await fsp.copyFile(src, dst);
  } catch {
  }
}

async function copyDirBestEffort(srcDir, dstDir) {
  await fsp.mkdir(dstDir, { recursive: true });
  const entries = await fsp.readdir(srcDir, { withFileTypes: true });
  for (const entry of entries) {
    const src = path.join(srcDir, entry.name);
    const dst = path.join(dstDir, entry.name);
    if (entry.isDirectory()) {
      await copyDirBestEffort(src, dst);
      continue;
    }
    try {
      await fsp.copyFile(src, dst);
    } catch {
    }
  }
}

async function installBridge(page, token, observedVideoUrls) {
  await page.exposeFunction("__xpostSendMessage", async (message) => {
    if (message?.type === "GET_TAG_CATALOG") {
      return await apiFetch(token, "GET", `${API_BASE}/tags`);
    }
    if (message?.type === "GET_POST_STATUS") {
      return await apiFetch(token, "GET", `${API_BASE}/posts/${encodeURIComponent(message.tweetId)}`);
    }
    if (message?.type === "SAVE_POST" || message?.type === "UPDATE_POST") {
      const payload = { ...(message.payload || {}) };
      if (payload.video_context) {
        payload.video_context.resource_urls = observedVideoUrls.slice();
      }
      payload.video_playlists = selectVideoPlaylists(payload);
      console.log("SELECTED_VIDEO_PLAYLISTS", JSON.stringify({
        tweet_id: payload.tweet_id,
        poster_urls: payload.video_context?.poster_urls || [],
        candidate_urls: payload.video_context?.candidate_urls || [],
        resource_count: Array.isArray(payload.video_context?.resource_urls) ? payload.video_context.resource_urls.length : 0,
        video_playlists: payload.video_playlists
      }));
      delete payload.video_context;
      const method = message.type === "UPDATE_POST" ? "PUT" : "POST";
      const url =
        message.type === "UPDATE_POST"
          ? `${API_BASE}/posts/${encodeURIComponent(payload.tweet_id)}`
          : `${API_BASE}/posts`;
      const response = await apiFetch(token, method, url, payload);
      console.log("SAVE_RESPONSE", JSON.stringify(response));
      return response;
    }
    throw new Error(`Unsupported message type: ${message?.type}`);
  });

  const bridgeScript = `
      window.chrome = {
        runtime: {
          sendMessage: async (message) => {
            if (message && message.payload && message.payload.video_context) {
              const resources = performance.getEntriesByType('resource')
                .map((entry) => entry.name)
                .filter((url) => typeof url === 'string' && url.includes('video.twimg.com'));
              message = {
                ...message,
                payload: {
                  ...message.payload,
                  video_context: {
                    ...message.payload.video_context,
                    resource_urls: resources
                  }
                }
              };
            }
            return await window.__xpostSendMessage(message);
          }
        }
      };
  `;

  await page.addInitScript({ content: `${bridgeScript}\n${CONTENT_JS}` });
}

function selectVideoPlaylists(payload) {
  const videoContext = payload?.video_context || {};
  const mediaIds = extractVideoMediaIds(videoContext);
  const direct = normalizeDirectVideoCandidates(videoContext);
  if (direct.length > 0) {
    return [{ m3u8_url: direct[0] }];
  }

  const resources = Array.isArray(videoContext.resource_urls) ? videoContext.resource_urls : [];
  const filtered = resources.filter((url) => {
    const value = String(url || "");
    if (!value.includes("video.twimg.com")) return false;
    if (!isAllowedVideoCandidateUrl(value)) return false;
    if (mediaIds.length === 0) return false;
    return mediaIds.some((mediaId) => value.includes(`/${mediaId}/`));
  });
  if (filtered.length > 0) {
    filtered.sort((a, b) => rankVideoCandidateUrl(a) - rankVideoCandidateUrl(b));
    return [{ m3u8_url: filtered[0] }];
  }
  return [];
}

function normalizeDirectVideoCandidates(videoContext) {
  const values = Array.isArray(videoContext?.candidate_urls) ? videoContext.candidate_urls : [];
  const seen = new Set();
  const normalized = [];
  for (const value of values) {
    const url = String(value || "").trim();
    if (!url || seen.has(url) || !/^https:/i.test(url)) continue;
    seen.add(url);
    let parsed;
    try {
      parsed = new URL(url);
    } catch {
      continue;
    }
    const host = parsed.host.toLowerCase();
    if (!(host === "video.twimg.com" || host.endsWith(".twimg.com"))) continue;
    const pathName = parsed.pathname.toLowerCase();
    const isLikely =
      pathName.endsWith(".m3u8") ||
      /\.(mp4|m4v|mov|webm|ts|mkv)$/i.test(pathName) ||
      pathName.includes("/ext_tw_video/") ||
      pathName.includes("/amplify_video/");
    if (!isLikely) continue;
    normalized.push(url);
  }
  return normalized;
}

function extractVideoMediaIds(videoContext) {
  const values = [
    ...(Array.isArray(videoContext?.candidate_urls) ? videoContext.candidate_urls : []),
    ...(Array.isArray(videoContext?.poster_urls) ? videoContext.poster_urls : []),
    ...(Array.isArray(videoContext?.resource_urls) ? videoContext.resource_urls : [])
  ];
  const ids = new Set();
  for (const value of values) {
    const text = String(value || "");
    const matches = text.match(/(?:amplify_video(?:_thumb)?|ext_tw_video(?:_thumb)?)\/(\d+)/gi) || [];
    for (const match of matches) {
      const idMatch = match.match(/\/(\d+)/);
      if (idMatch?.[1]) ids.add(idMatch[1]);
    }
  }
  return [...ids];
}

function isAllowedVideoCandidateUrl(url) {
  const value = String(url || "").toLowerCase();
  return value.includes(".m3u8") || /\.(mp4|m4v|mov|webm|ts|mkv)(\?|$)/i.test(value);
}

function rankVideoCandidateUrl(url) {
  const value = String(url || "").toLowerCase();
  if (/(?:\\.mp4|\\.m4v|\\.mov|\\.webm|\\.ts|\\.mkv)(?:\\?|$)/i.test(value) && value.includes("/vid/")) return 0;
  if (value.includes(".m3u8") && value.includes("/pl/")) return 1;
  if (/(?:\\.mp4|\\.m4v|\\.mov|\\.webm|\\.ts|\\.mkv)(?:\\?|$)/i.test(value) && value.includes("/aud/")) return 3;
  if (/(?:\\.mp4|\\.m4v|\\.mov|\\.webm|\\.ts|\\.mkv)(?:\\?|$)/i.test(value)) return 2;
  return 4;
}

async function waitForToast(page) {
  await page.waitForFunction(() => {
    const toast = document.getElementById("x-post-archive-toast");
    return toast && toast.textContent && toast.textContent.includes("保存しました");
  }, { timeout: 60000 });
}

async function waitForSavedPost(token, tweetId) {
  const start = Date.now();
  while (Date.now() - start < 60000) {
    const status = await getPostStatus(token, tweetId);
    if (status.ok && status.body?.exists === true) {
      return status.body;
    }
    await sleep(1000);
  }
  throw new Error(`Saved post not found: ${tweetId}`);
}

async function waitForVideoFile(dirPath) {
  const start = Date.now();
  while (Date.now() - start < 180000) {
    const videosDir = path.join(dirPath, "videos");
    if (fs.existsSync(videosDir)) {
      const files = fs.readdirSync(videosDir).map((name) => path.join(videosDir, name));
      const candidate = files.find((file) => fs.statSync(file).isFile() && fs.statSync(file).size > 32768);
      if (candidate) {
        return candidate;
      }
    }
    await sleep(2000);
  }
  throw new Error("Video file was not created in time");
}

async function validateVideoFile(filePath) {
  await new Promise((resolve, reject) => {
    const child = spawn(FFMPEG_PATH, ["-v", "error", "-i", filePath, "-f", "null", "-"], {
      cwd: ROOT,
      stdio: ["ignore", "ignore", "pipe"]
    });
    let stderr = "";
    child.stderr.on("data", (chunk) => {
      stderr += chunk.toString();
    });
    child.on("exit", (code) => {
      if (code === 0) resolve();
      else reject(new Error(`ffmpeg validation failed: code=${code}\n${stderr}`));
    });
    child.on("error", reject);
  });
}

function sleep(ms) {
  return new Promise((resolve) => setTimeout(resolve, ms));
}

main().catch((error) => {
  console.error("VERIFY_FAILED");
  console.error(error);
  process.exit(1);
});
