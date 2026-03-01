const fs = require("fs");
const fsp = require("fs/promises");
const path = require("path");
const { spawn } = require("child_process");
const { chromium } = require("playwright");

const ROOT = process.cwd();
const PORT = 18766;
const API_BASE = `http://127.0.0.1:${PORT}/api/v1`;
const TWEET_URL =
  process.env.TWEET_URL ||
  "https://x.com/Interior/status/463440424141459456";

async function main() {
  const tweetIdMatch = TWEET_URL.match(/status\/(\d+)/);
  if (!tweetIdMatch) {
    throw new Error(`Invalid tweet url: ${TWEET_URL}`);
  }

  const tweetId = tweetIdMatch[1];
  await resetE2eState();
  const server = startServer();
  try {
    await waitForHealth();

    const postData = await collectPostDataWithPlaywright(TWEET_URL, tweetId);
    const token = await bootstrapToken();
    const saveResult = await savePost(postData, token);

    if (!saveResult.ok) {
      throw new Error(
        `Save failed: status=${saveResult.status} body=${JSON.stringify(saveResult.body)}`
      );
    }

    await verifySavedArtifacts(saveResult.body?.dir_path, tweetId);
    console.log("E2E OK:", saveResult.body);
  } finally {
    server.kill("SIGTERM");
  }
}

async function resetE2eState() {
  await fsp.rm(path.join(ROOT, "XArchive_e2e"), { recursive: true, force: true });
  await fsp.rm(path.join(ROOT, "data_e2e"), { recursive: true, force: true });
}

function startServer() {
  const env = {
    ...process.env,
    Server__Host: "127.0.0.1",
    Server__Port: String(PORT),
    Storage__RootPath: "./XArchive_e2e",
    Database__Path: "./data_e2e/archive.db",
    Auth__TokenFilePath: "./data_e2e/auth_token.txt",
    Video__FfmpegPath: "./third_party/ffmpeg/ffmpeg.exe",
    DOTNET_CLI_TELEMETRY_OPTOUT: "1",
    DOTNET_NOLOGO: "1"
  };

  const child = spawn("dotnet", ["run", "--project", "XPostArchive.Api.csproj"], {
    cwd: ROOT,
    env,
    stdio: ["ignore", "pipe", "pipe"]
  });

  child.stdout.on("data", (chunk) => {
    process.stdout.write(`[api] ${chunk}`);
  });
  child.stderr.on("data", (chunk) => {
    process.stderr.write(`[api-err] ${chunk}`);
  });

  return child;
}

async function waitForHealth() {
  const timeoutMs = 60_000;
  const start = Date.now();

  while (Date.now() - start < timeoutMs) {
    try {
      const response = await fetch(`${API_BASE}/health`);
      if (response.ok) return;
    } catch {
      // retry
    }
    await sleep(500);
  }

  throw new Error("API health check timed out");
}

async function collectPostDataWithPlaywright(url, tweetId) {
  const browser = await chromium.launch({ headless: true });
  const context = await browser.newContext({
    viewport: { width: 1280, height: 1800 }
  });
  const page = await context.newPage();

  try {
    await page.goto(url, { waitUntil: "domcontentloaded", timeout: 120_000 });
    await page.waitForTimeout(4000);

    const data = await page.evaluate(({ tweetId: idFromUrl }) => {
      const article =
        document.querySelector("article[data-testid='tweet']") ||
        document.querySelector("article");

      const url = location.href;
      const timeEl = article ? article.querySelector("time") : null;
      const createdAt = timeEl?.getAttribute("datetime") || new Date().toISOString();
      const textEl = article ? article.querySelector("div[data-testid='tweetText']") : null;
      const text =
        textEl?.innerText?.trim() ||
        document.querySelector("meta[property='og:description']")?.getAttribute("content") ||
        document.title ||
        "E2E fallback text";

      const authorLink = article ? article.querySelector("a[href*='/status/']") : null;
      const handleMatch = authorLink?.getAttribute("href")?.match(/^\/([^/]+)\/status\//);
      const handle = handleMatch ? `@${handleMatch[1]}` : "@unknown";
      const name =
        (article ? article.querySelector("div[dir='ltr'] span")?.textContent?.trim() : "") ||
        handle;

      const imageUrls = article
        ? [...article.querySelectorAll("img")]
            .map((img) => img.getAttribute("src") || "")
            .filter((src) => src.includes("pbs.twimg.com/media"))
            .slice(0, 10)
            .map((u) => ({ url: u }))
        : [];

      return {
        tweet_id: idFromUrl,
        url,
        author: { handle, name },
        created_at: createdAt,
        text,
        tags: ["e2e", "playwright"],
        note: "playwright e2e",
        images: imageUrls,
        video_playlists: []
      };
    }, { tweetId });

    const screenshotBuffer = await page.screenshot({ fullPage: false, type: "png" });
    data.screenshot_base64 = `data:image/png;base64,${screenshotBuffer.toString("base64")}`;

    return data;
  } finally {
    await context.close();
    await browser.close();
  }
}

async function bootstrapToken() {
  const response = await fetch(`${API_BASE}/auth/bootstrap`, { method: "POST" });
  const body = await response.json();
  if (!response.ok || !body?.token) {
    throw new Error(`Token bootstrap failed: status=${response.status} body=${JSON.stringify(body)}`);
  }
  return body.token;
}

async function savePost(payload, token) {
  const response = await fetch(`${API_BASE}/posts`, {
    method: "POST",
    headers: {
      "Content-Type": "application/json",
      Authorization: `Bearer ${token}`
    },
    body: JSON.stringify(payload)
  });

  let body = {};
  try {
    body = await response.json();
  } catch {
    body = { parse_error: true };
  }

  return { ok: response.ok, status: response.status, body };
}

async function verifySavedArtifacts(dirPath, tweetId) {
  if (!dirPath) throw new Error("dir_path is missing from API response");

  const metaPath = path.join(dirPath, "meta.json");
  const screenshotPath = path.join(dirPath, "screenshot.png");
  if (!fs.existsSync(dirPath)) throw new Error(`Saved dir not found: ${dirPath}`);
  if (!fs.existsSync(metaPath)) throw new Error(`meta.json not found: ${metaPath}`);
  if (!fs.existsSync(screenshotPath)) throw new Error(`screenshot.png not found: ${screenshotPath}`);

  const meta = JSON.parse(await fsp.readFile(metaPath, "utf8"));
  if (meta.tweet_id !== tweetId) {
    throw new Error(`tweet_id mismatch in meta.json: expected=${tweetId} actual=${meta.tweet_id}`);
  }
}

function sleep(ms) {
  return new Promise((resolve) => setTimeout(resolve, ms));
}

main().catch((err) => {
  console.error("E2E FAILED:", err);
  process.exit(1);
});
