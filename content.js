const STYLE_ID = "x-post-archive-inline-style";
const BUTTON_CLASS = "x-post-archive-inline-save";
const BUTTON_HOST_ATTR = "data-x-post-archive-save-host";
const ARTICLE_BOUND_ATTR = "data-x-post-archive-bound";
const MODAL_ID = "x-post-archive-modal";

let scanScheduled = false;

boot();

function boot() {
  installStyles();
  scanArticles();

  const observer = new MutationObserver(() => {
    if (scanScheduled) {
      return;
    }

    scanScheduled = true;
    requestAnimationFrame(() => {
      scanScheduled = false;
      scanArticles();
    });
  });

  observer.observe(document.documentElement, {
    childList: true,
    subtree: true
  });
}

function installStyles() {
  if (document.getElementById(STYLE_ID)) {
    return;
  }

  const style = document.createElement("style");
  style.id = STYLE_ID;
  style.textContent = `
    .${BUTTON_CLASS} {
      display: inline-flex;
      align-items: center;
      justify-content: center;
      min-width: 88px;
      height: 32px;
      border: none;
      border-radius: 999px;
      padding: 0 14px;
      background: #1d9bf0;
      color: #ffffff;
      font-size: 13px;
      font-weight: 700;
      cursor: pointer;
      transition: transform 0.12s ease, opacity 0.12s ease, background 0.12s ease;
      box-shadow: 0 4px 12px rgba(29, 155, 240, 0.28);
    }

    .${BUTTON_CLASS}:hover {
      background: #1681cc;
      transform: translateY(-1px);
    }

    .${BUTTON_CLASS}:disabled {
      cursor: default;
      opacity: 0.72;
      transform: none;
      box-shadow: none;
    }
  `;

  document.documentElement.appendChild(style);
}

function scanArticles() {
  const articles = document.querySelectorAll("article");
  for (const article of articles) {
    bindArticle(article);
  }
}

function bindArticle(article) {
  if (!(article instanceof HTMLElement)) {
    return;
  }

  const tweetId = resolveTweetIdFromArticle(article);
  if (!tweetId) {
    return;
  }

  if (article.getAttribute(ARTICLE_BOUND_ATTR) === tweetId) {
    return;
  }

  article.setAttribute(ARTICLE_BOUND_ATTR, tweetId);

  const existingHost = article.querySelector(`[${BUTTON_HOST_ATTR}]`);
  if (existingHost) {
    existingHost.remove();
  }

  const actionBar = findActionBar(article);
  if (!actionBar) {
    return;
  }

  const host = document.createElement("div");
  host.setAttribute(BUTTON_HOST_ATTR, "true");
  Object.assign(host.style, {
    display: "flex",
    justifyContent: "flex-end",
    marginTop: "8px"
  });

  const button = document.createElement("button");
  button.type = "button";
  button.className = BUTTON_CLASS;
  button.textContent = "保存";
  button.setAttribute("aria-label", "この投稿を保存");

  button.addEventListener("click", async (event) => {
    event.preventDefault();
    event.stopPropagation();
    await onInlineSaveClick(article, button);
  });

  host.addEventListener("click", (event) => {
    event.stopPropagation();
  });

  host.appendChild(button);
  actionBar.insertAdjacentElement("afterend", host);
}

async function onInlineSaveClick(article, button) {
  const originalLabel = button.textContent;

  try {
    setButtonState(button, "保存中...", true);

    const payload = await extractPostData(article);
    const result = await chrome.runtime.sendMessage({ type: "SAVE_POST", payload });

    if (!result?.ok) {
      const body = result?.body || {};
      const detail = buildErrorDetail(result, payload);

      if (body.can_retry === true) {
        const retry = await showConfirmDialog(
          "保存に失敗しました",
          `${body.message || "リクエストに失敗しました。"}\n\n再試行しますか？`,
          detail
        );

        if (retry) {
          setButtonState(button, "再試行中...", true);
          const retryResult = await chrome.runtime.sendMessage({ type: "SAVE_POST", payload });
          if (!retryResult?.ok) {
            await showResultDialog("再試行に失敗しました", buildErrorDetail(retryResult, payload), true);
            return;
          }

          await showResultDialog("保存しました", JSON.stringify(retryResult.body || {}, null, 2), false);
          flashSaved(button);
          return;
        }
      }

      await showResultDialog("保存に失敗しました", detail, true);
      return;
    }

    await showResultDialog("保存しました", JSON.stringify(result.body || {}, null, 2), false);
    flashSaved(button);
  } catch (error) {
    const detail = `Unhandled Error\n\n${String(error)}\n\n${error?.stack || ""}`;
    await showResultDialog("予期しないエラー", detail, true);
  } finally {
    if (button.textContent !== "保存済み") {
      setButtonState(button, originalLabel || "保存", false);
    }
  }
}

function flashSaved(button) {
  setButtonState(button, "保存済み", true);
  button.style.background = "#0f766e";
  button.style.boxShadow = "none";

  window.setTimeout(() => {
    button.style.background = "#1d9bf0";
    button.style.boxShadow = "0 4px 12px rgba(29, 155, 240, 0.28)";
    setButtonState(button, "保存", false);
  }, 1800);
}

function setButtonState(button, label, disabled) {
  button.textContent = label;
  button.disabled = disabled;
}

async function extractPostData(article) {
  const tweetId = resolveTweetIdFromArticle(article);
  if (!tweetId) {
    throw new Error("tweet_id を取得できませんでした。");
  }

  const url = resolveCanonicalUrl(article, tweetId);
  const timeEl = article.querySelector("time");
  const createdAt = timeEl?.getAttribute("datetime") || "";
  if (!createdAt) {
    throw new Error("created_at が見つかりませんでした。");
  }

  const textEl = article.querySelector("div[data-testid='tweetText']");
  const text = textEl?.innerText?.trim() || "";
  if (!text) {
    throw new Error("投稿本文が見つかりませんでした。");
  }

  const handleEl =
    article.querySelector(`a[href*="/status/${tweetId}"]`) ||
    article.querySelector("a[href*='/status/']");
  const handle = resolveHandle(handleEl?.getAttribute("href"));

  const nameEl = article.querySelector("div[dir='ltr'] span");
  const authorName = nameEl?.textContent?.trim() || handle;

  const imageUrls = [...article.querySelectorAll("img")]
    .map((img) => img.getAttribute("src") || "")
    .filter((src) => src.includes("twimg.com/media"))
    .map((imageUrl) => ({ url: imageUrl }))
    .slice(0, 10);

  return {
    tweet_id: tweetId,
    url,
    author: {
      handle,
      name: authorName
    },
    created_at: createdAt,
    text,
    tags: [],
    note: "",
    images: imageUrls
  };
}

function resolveTweetIdFromArticle(article) {
  const timeLink = article.querySelector("a[href*='/status/']");
  const href = timeLink?.getAttribute("href") || "";
  const match = href.match(/status\/(\d+)/);
  if (match) {
    return match[1];
  }

  const links = [...article.querySelectorAll("a[href*='/status/']")];
  for (const link of links) {
    const candidate = link.getAttribute("href") || "";
    const found = candidate.match(/status\/(\d+)/);
    if (found) {
      return found[1];
    }
  }

  return "";
}

function resolveCanonicalUrl(article, tweetId) {
  const statusLink =
    article.querySelector(`a[href*="/status/${tweetId}"]`) ||
    article.querySelector("a[href*='/status/']");
  const href = statusLink?.getAttribute("href") || `/i/status/${tweetId}`;
  return new URL(href, location.origin).toString();
}

function findActionBar(article) {
  const groups = article.querySelectorAll("div[role='group']");
  for (const group of groups) {
    if (group.querySelector("button[data-testid='reply']") || group.querySelector("button[data-testid='like']")) {
      return group;
    }
  }

  return article.querySelector("div[role='group']");
}

function resolveHandle(href) {
  if (!href) {
    return "@unknown";
  }

  const match = href.match(/^\/([^/]+)\/status\//);
  if (!match) {
    return "@unknown";
  }

  return `@${match[1]}`;
}

function buildErrorDetail(result, payload) {
  return [
    `status: ${result?.status ?? "unknown"}`,
    `ok: ${result?.ok === true}`,
    "",
    "response.body:",
    JSON.stringify(result?.body || {}, null, 2),
    "",
    "request.payload:",
    JSON.stringify(payload || {}, null, 2)
  ].join("\n");
}

async function showResultDialog(title, detailText, isError) {
  const { root, okButton, copyButton } = createModal(title, detailText, isError, false);
  copyButton.addEventListener("click", async () => {
    await copyText(detailText);
  });
  await waitButton(okButton);
  root.remove();
}

async function showConfirmDialog(title, message, detailText) {
  const { root, body, okButton, cancelButton, copyButton } = createModal(title, detailText, true, true);
  const messageNode = document.createElement("div");
  messageNode.textContent = message;
  messageNode.style.whiteSpace = "pre-wrap";
  messageNode.style.marginBottom = "10px";
  body.prepend(messageNode);

  copyButton.addEventListener("click", async () => {
    await copyText(detailText);
  });

  const clicked = await waitButtons(okButton, cancelButton);
  root.remove();
  return clicked === "ok";
}

function createModal(title, detailText, isError, withCancel) {
  const existing = document.getElementById(MODAL_ID);
  if (existing) {
    existing.remove();
  }

  const root = document.createElement("div");
  root.id = MODAL_ID;
  Object.assign(root.style, {
    position: "fixed",
    inset: "0",
    zIndex: "1000000",
    background: "rgba(0,0,0,0.5)",
    display: "flex",
    alignItems: "center",
    justifyContent: "center",
    padding: "12px"
  });

  const panel = document.createElement("div");
  Object.assign(panel.style, {
    width: "min(760px, 100%)",
    maxHeight: "90vh",
    background: "#fff",
    color: "#111",
    borderRadius: "12px",
    fontFamily: "ui-sans-serif, system-ui, -apple-system, Segoe UI, sans-serif",
    boxShadow: "0 10px 30px rgba(0,0,0,0.25)",
    display: "flex",
    flexDirection: "column",
    overflow: "hidden"
  });

  const header = document.createElement("div");
  Object.assign(header.style, {
    padding: "16px 16px 8px 16px"
  });

  const heading = document.createElement("h3");
  heading.textContent = title;
  heading.style.margin = "0";
  heading.style.color = isError ? "#b42318" : "#0f5132";
  header.appendChild(heading);

  const body = document.createElement("div");
  Object.assign(body.style, {
    padding: "8px 16px",
    overflow: "auto",
    flex: "1 1 auto"
  });

  const detail = document.createElement("textarea");
  detail.readOnly = true;
  detail.value = detailText;
  Object.assign(detail.style, {
    width: "100%",
    minHeight: "220px",
    resize: "vertical",
    boxSizing: "border-box",
    marginTop: "8px",
    fontFamily: "ui-monospace, SFMono-Regular, Menlo, monospace",
    fontSize: "12px",
    lineHeight: "1.45",
    border: "1px solid #d0d5dd",
    borderRadius: "8px",
    padding: "8px"
  });
  body.appendChild(detail);

  const footer = document.createElement("div");
  Object.assign(footer.style, {
    display: "flex",
    justifyContent: "flex-end",
    gap: "8px",
    padding: "12px 16px 16px 16px",
    borderTop: "1px solid #eaecf0",
    flex: "0 0 auto"
  });

  const copyButton = createButton("コピー", "#e4e7ec", "#111");
  const okButton = createButton(withCancel ? "再試行" : "OK", "#1d4ed8", "#fff");
  const cancelButton = withCancel ? createButton("キャンセル", "#e4e7ec", "#111") : null;

  footer.appendChild(copyButton);
  if (cancelButton) {
    footer.appendChild(cancelButton);
  }
  footer.appendChild(okButton);

  panel.appendChild(header);
  panel.appendChild(body);
  panel.appendChild(footer);
  root.appendChild(panel);
  document.body.appendChild(root);

  detail.focus();
  detail.select();

  return { root, body, okButton, cancelButton, copyButton };
}

function createButton(label, bg, fg) {
  const button = document.createElement("button");
  button.type = "button";
  button.textContent = label;
  Object.assign(button.style, {
    border: "none",
    borderRadius: "8px",
    padding: "8px 12px",
    background: bg,
    color: fg,
    cursor: "pointer",
    fontWeight: "600"
  });
  return button;
}

function waitButton(button) {
  return new Promise((resolve) => {
    button.addEventListener("click", () => resolve(), { once: true });
  });
}

function waitButtons(okButton, cancelButton) {
  return new Promise((resolve) => {
    okButton.addEventListener("click", () => resolve("ok"), { once: true });
    cancelButton.addEventListener("click", () => resolve("cancel"), { once: true });
  });
}

async function copyText(text) {
  try {
    await navigator.clipboard.writeText(text);
  } catch {
    window.prompt("コピーに失敗しました。手動でコピーしてください:", text);
  }
}
