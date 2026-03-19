const fs = require("fs");
const fsp = require("fs/promises");
const os = require("os");
const path = require("path");
const { spawn } = require("child_process");
const { chromium } = require("playwright");

const ROOT = process.cwd();
const PORT = Number(process.env.E2E_API_PORT || (19000 + Math.floor(Math.random() * 1000)));
const API_BASE = `http://127.0.0.1:${PORT}/api/v1`;
const RUN_ID = new Date().toISOString().replace(/[:.]/g, "-");
const STORAGE_ROOT = `./XArchive_e2e/${RUN_ID}`;
const DATABASE_PATH = `./data_e2e/${RUN_ID}/archive.db`;
const TOKEN_PATH = `./data_e2e/${RUN_ID}/auth_token.txt`;
const TWEET_URL =
  process.env.TWEET_URL ||
  "https://x.com/Interior/status/463440424141459456";
const CHROME_USER_DATA_DIR = process.env.PLAYWRIGHT_CHROME_USER_DATA_DIR;
const CHROME_PROFILE_DIRECTORY = process.env.PLAYWRIGHT_CHROME_PROFILE_DIRECTORY || "Default";
const HEADLESS = process.env.PLAYWRIGHT_HEADLESS === "1";

async function main() {
  if (!CHROME_USER_DATA_DIR) {
    throw new Error(
      "PLAYWRIGHT_CHROME_USER_DATA_DIR is required. Point it to your logged-in Chrome user data directory."
    );
  }

  const tweetIdMatch = TWEET_URL.match(/status\/(\d+)/);
  if (!tweetIdMatch) {
    throw new Error(`Invalid tweet url: ${TWEET_URL}`);
  }

  const tweetId = tweetIdMatch[1];
  const server = startServer();
  try {
    await waitForHealth(server);

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

function startServer() {
  const env = {
    ...process.env,
    Server__Host: "127.0.0.1",
    Server__Port: String(PORT),
    Storage__RootPath: STORAGE_ROOT,
    Database__Path: DATABASE_PATH,
    Auth__TokenFilePath: TOKEN_PATH,
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

async function waitForHealth(server) {
  const timeoutMs = 60_000;
  const start = Date.now();

  while (Date.now() - start < timeoutMs) {
    if (server.exitCode !== null) {
      throw new Error(`API process exited early with code ${server.exitCode}`);
    }
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
  const preparedUserDataDir = await prepareUserDataSnapshot(
    CHROME_USER_DATA_DIR,
    CHROME_PROFILE_DIRECTORY
  );
  const context = await chromium.launchPersistentContext(preparedUserDataDir, {
    channel: "chrome",
    headless: HEADLESS,
    viewport: null,
    args: [
      `--profile-directory=${CHROME_PROFILE_DIRECTORY}`,
      "--start-maximized"
    ]
  });
  const page = context.pages()[0] || (await context.newPage());

  try {
    await ensureLoggedIn(page);
    await page.goto(url, { waitUntil: "domcontentloaded", timeout: 120_000 });
    await page.waitForTimeout(4000);

    if (page.url().includes("/i/flow/login")) {
      throw new Error("Not logged in on x.com. Test requires a logged-in profile.");
    }

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

    return data;
  } finally {
    await context.close();
    await fsp.rm(preparedUserDataDir, { recursive: true, force: true });
  }
}

async function prepareUserDataSnapshot(sourceUserDataDir, profileDir) {
  const snapshotRoot = await fsp.mkdtemp(path.join(os.tmpdir(), "xpost-chrome-profile-"));
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
    // best effort
  }
}

async function copyDirBestEffort(srcDir, dstDir) {
  await fsp.mkdir(dstDir, { recursive: true });
  let entries = [];
  try {
    entries = await fsp.readdir(srcDir, { withFileTypes: true });
  } catch (err) {
    throw new Error(`Cannot read Chrome profile directory: ${srcDir}. ${err.message}`);
  }

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
      // locked files are skipped
    }
  }
}

async function ensureLoggedIn(page) {
  await page.goto("https://x.com/home", { waitUntil: "domcontentloaded", timeout: 120_000 });
  await page.waitForTimeout(3000);
  const currentUrl = page.url();
  if (currentUrl.includes("/i/flow/login") || currentUrl.includes("/i/flow/signup")) {
    throw new Error(
      "x.com is not logged in for this Chrome profile.\n" +
        "Please login in normal Chrome first, then re-run this test with the same profile.\n" +
        `Current URL: ${currentUrl}`
    );
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

  if (!fs.existsSync(dirPath)) throw new Error(`Saved dir not found: ${dirPath}`);
}

function sleep(ms) {
  return new Promise((resolve) => setTimeout(resolve, ms));
}

main().catch((err) => {
  console.error("E2E FAILED:", err);
  process.exit(1);
});
