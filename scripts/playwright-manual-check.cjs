const fsp = require("fs/promises");
const os = require("os");
const path = require("path");
const { spawn } = require("child_process");
const { chromium } = require("playwright");

const ROOT = process.cwd();
const API_PORT = Number(process.env.API_PORT || "18765");
const API_BASE = `http://127.0.0.1:${API_PORT}/api/v1`;
const TWEET_URL =
  process.env.TWEET_URL ||
  "https://x.com/manga_dioxide/status/2030501170264068103";
const START_API = process.env.START_API !== "0";
const HEADLESS = process.env.PLAYWRIGHT_HEADLESS === "1";
const EXT_PATH = ROOT;
const DOWNLOADS_DIR = path.join(os.homedir(), "Downloads");

async function main() {
  const runId = new Date().toISOString().replace(/[:.]/g, "-");
  const screenshotPath = path.join(DOWNLOADS_DIR, `x-post-manual-${runId}.png`);
  const modalPath = path.join(DOWNLOADS_DIR, `x-post-manual-modal-${runId}.png`);
  const profileDir = await fsp.mkdtemp(path.join(os.tmpdir(), "x-post-manual-"));

  let apiProcess = null;
  if (START_API) {
    apiProcess = startApi();
    await waitForHealth(apiProcess);
  }

  const context = await chromium.launchPersistentContext(profileDir, {
    channel: "chromium",
    headless: HEADLESS,
    viewport: { width: 1365, height: 900 },
    args: [
      `--disable-extensions-except=${EXT_PATH}`,
      `--load-extension=${EXT_PATH}`
    ]
  });

  const page = context.pages()[0] || (await context.newPage());
  try {
    await page.goto(TWEET_URL, { waitUntil: "domcontentloaded", timeout: 120_000 });
    await ensureOnTweetPage(page);
    console.log("PAGE_URL:", page.url());
    console.log("PAGE_TITLE:", await page.title());
    try {
      await page.waitForSelector("#x-post-archive-save-button", { timeout: 30_000 });
    } catch {
      const failShot = path.join(DOWNLOADS_DIR, `x-post-manual-fail-${runId}.png`);
      await page.screenshot({ path: failShot, fullPage: false }).catch(() => {});
      console.log("FAIL_SCREENSHOT:", failShot);
      if (HEADLESS) {
        throw new Error(
          "Save button not found in headless mode. Run without PLAYWRIGHT_HEADLESS to load extension UI."
        );
      }
      throw new Error(
        "Save button not found. Confirm extension is loaded and this is an x.com status page."
      );
    }
    await page.click("#x-post-archive-save-button");

    await waitForResult(page);
    await page.screenshot({ path: screenshotPath, fullPage: false });

    const modal = await page.$("#x-post-archive-modal");
    let modalTitle = null;
    let modalText = null;
    if (modal) {
      await modal.screenshot({ path: modalPath });
      modalTitle = await page.$eval("#x-post-archive-modal h3", (el) => el.textContent || "");
      modalText = await page.$eval(
        "#x-post-archive-modal textarea",
        (el) => el.value || ""
      );
    }

    console.log("MANUAL_CHECK_OK");
    console.log("URL:", page.url());
    console.log("SCREENSHOT:", screenshotPath);
    if (modal) {
      console.log("MODAL_SCREENSHOT:", modalPath);
      console.log("MODAL_TITLE:", modalTitle);
      console.log("MODAL_TEXT_BEGIN");
      console.log(modalText || "");
      console.log("MODAL_TEXT_END");
    } else {
      console.log("MODAL_NOT_FOUND");
    }

    if (!HEADLESS) {
      console.log("Browser is left open for manual inspection. Press Ctrl+C to end.");
      await new Promise(() => {});
    }
  } finally {
    if (HEADLESS) {
      await context.close();
      await cleanup(profileDir, apiProcess);
    } else {
      const handleExit = async () => {
        await context.close().catch(() => {});
        await cleanup(profileDir, apiProcess);
      };
      process.on("SIGINT", async () => {
        await handleExit();
        process.exit(0);
      });
      process.on("SIGTERM", async () => {
        await handleExit();
        process.exit(0);
      });
    }
  }
}

function startApi() {
  const env = {
    ...process.env,
    Server__Host: "127.0.0.1",
    Server__Port: String(API_PORT),
    Video__FfmpegPath: "./third_party/ffmpeg/ffmpeg.exe",
    DOTNET_CLI_TELEMETRY_OPTOUT: "1",
    DOTNET_NOLOGO: "1"
  };

  const child = spawn("dotnet", ["run", "--project", "XPostArchive.Api.csproj"], {
    cwd: ROOT,
    env,
    stdio: ["ignore", "pipe", "pipe"]
  });

  child.stdout.on("data", (chunk) => process.stdout.write(`[api] ${chunk}`));
  child.stderr.on("data", (chunk) => process.stderr.write(`[api-err] ${chunk}`));
  return child;
}

async function waitForHealth(apiProcess) {
  const timeoutMs = 60_000;
  const start = Date.now();
  while (Date.now() - start < timeoutMs) {
    if (apiProcess.exitCode !== null) {
      throw new Error(`API exited early with code ${apiProcess.exitCode}`);
    }
    try {
      const res = await fetch(`${API_BASE}/health`);
      if (res.ok) return;
    } catch {
      // retry
    }
    await sleep(500);
  }
  throw new Error("API health check timed out");
}

async function waitForResult(page) {
  await page.waitForTimeout(2000);
  const modal = page.locator("#x-post-archive-modal");
  const visible = await modal.isVisible().catch(() => false);
  if (visible) return;

  // Fallback: wait for either modal or timeout for native alert flow.
  const started = Date.now();
  while (Date.now() - started < 20_000) {
    const exists = await page
      .evaluate(() => Boolean(document.getElementById("x-post-archive-modal")))
      .catch(() => false);
    if (exists) return;
    await page.waitForTimeout(300);
  }
}

async function ensureOnTweetPage(page) {
  const isTweetUrl = () => /\/status\/\d+/.test(page.url());
  if (isTweetUrl()) return;

  const current = page.url();
  if (!current.includes("/i/flow/login")) {
    throw new Error(`Unexpected page before save: ${current}`);
  }

  if (HEADLESS) {
    throw new Error(
      "x.com login is required but PLAYWRIGHT_HEADLESS=1. Re-run without headless and login in the opened browser."
    );
  }

  console.log("LOGIN_REQUIRED: Please login to x.com in the opened browser window.");
  const timeoutMs = 5 * 60_000;
  const start = Date.now();
  while (Date.now() - start < timeoutMs) {
    if (isTweetUrl()) return;
    await page.waitForTimeout(1000);
  }
  throw new Error("Timed out waiting for manual login and tweet page navigation.");
}

async function cleanup(profileDir, apiProcess) {
  if (apiProcess && apiProcess.exitCode === null) {
    apiProcess.kill("SIGTERM");
  }
  await fsp.rm(profileDir, { recursive: true, force: true });
}

function sleep(ms) {
  return new Promise((resolve) => setTimeout(resolve, ms));
}

main().catch((error) => {
  console.error("MANUAL_CHECK_FAILED:", error);
  process.exit(1);
});
