const path = require("path");
const os = require("os");
const { chromium } = require("playwright");

async function main() {
  const browser = await chromium.launch({ headless: true });
  const context = await browser.newContext({
    permissions: ["clipboard-read", "clipboard-write"]
  });
  const page = await context.newPage();

  try {
    await page.addInitScript(() => {
      const fakeResponse = {
        ok: false,
        status: 400,
        body: {
          ok: false,
          error_code: "TEST_ERROR",
          message: "copy-test-error",
          can_retry: false
        }
      };

      window.chrome = {
        runtime: {
          sendMessage: async () => fakeResponse
        }
      };
    });

    await page.goto("https://example.com", { waitUntil: "domcontentloaded" });
    await page.evaluate(() => {
      history.replaceState({}, "", "/some-user/status/1234567890123456789");
      document.body.innerHTML = `
        <article data-testid="tweet">
          <time datetime="2026-03-01T00:00:00.000Z"></time>
          <a href="/some-user/status/1234567890123456789">link</a>
          <div dir="ltr"><span>Some User</span></div>
          <div data-testid="tweetText">Playwright copy test post text</div>
          <img src="https://pbs.twimg.com/media/test.jpg?format=jpg&name=small" />
        </article>
      `;
    });

    await page.addScriptTag({ path: path.join(process.cwd(), "content.js") });
    await page.click("#x-post-archive-save-button");

    await page.waitForSelector("#x-post-archive-modal textarea", { timeout: 10_000 });
    const screenshotPath = path.join(os.homedir(), "Downloads", "x-post-copy-modal.png");
    await page.locator("#x-post-archive-modal").screenshot({ path: screenshotPath });
    console.log("SCREENSHOT_SAVED:", screenshotPath);
    await page.click("#x-post-archive-modal button");

    const clipboardText = await page.evaluate(async () => navigator.clipboard.readText());
    if (!clipboardText.includes("response.body:")) {
      throw new Error(`Clipboard text does not include response.body section: ${clipboardText}`);
    }
    if (!clipboardText.includes("copy-test-error")) {
      throw new Error(`Clipboard text does not include error message: ${clipboardText}`);
    }

    console.log("COPY_E2E_OK");
  } finally {
    await context.close();
    await browser.close();
  }
}

main().catch((err) => {
  console.error("COPY_E2E_FAILED:", err);
  process.exit(1);
});
