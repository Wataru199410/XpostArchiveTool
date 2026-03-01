const BUTTON_ID = "x-post-archive-save-button";
const MODAL_ID = "x-post-archive-modal";

boot();

function boot() {
  if (document.getElementById(BUTTON_ID)) return;

  const button = document.createElement("button");
  button.id = BUTTON_ID;
  button.textContent = "このポストを保存";
  Object.assign(button.style, {
    position: "fixed",
    right: "16px",
    bottom: "16px",
    zIndex: "999999",
    background: "#1d9bf0",
    color: "white",
    border: "none",
    borderRadius: "999px",
    padding: "10px 16px",
    fontWeight: "700",
    cursor: "pointer"
  });

  button.addEventListener("click", onSaveClick);
  document.body.appendChild(button);
}

async function onSaveClick() {
  try {
    const payload = extractPostData();
    const result = await chrome.runtime.sendMessage({ type: "SAVE_POST", payload });

    if (!result?.ok) {
      const body = result?.body || {};
      const canRetry = body.can_retry === true;
      const detail = buildErrorDetail(result, payload);

      if (canRetry) {
        const retry = await showConfirmDialog(
          "保存に失敗しました",
          `${body.message || "不明なエラー"}\n\n再試行しますか？`,
          detail
        );
        if (retry) {
          const retryResult = await chrome.runtime.sendMessage({ type: "SAVE_POST", payload });
          if (!retryResult?.ok) {
            await showResultDialog("再試行でも失敗しました", buildErrorDetail(retryResult, payload), true);
            return;
          }

          await showResultDialog("保存に成功しました", JSON.stringify(retryResult.body || {}, null, 2), false);
          return;
        }
      }

      await showResultDialog("保存に失敗しました", detail, true);
      return;
    }

    await showResultDialog("保存に成功しました", JSON.stringify(result.body || {}, null, 2), false);
  } catch (error) {
    const detail = `Unhandled Error\n\n${String(error)}\n\n${error?.stack || ""}`;
    await showResultDialog("保存処理で例外が発生しました", detail, true);
  }
}

function extractPostData() {
  const url = location.href;
  const tweetMatch = url.match(/status\/(\d+)/);
  if (!tweetMatch) {
    throw new Error("tweet_id を URL から取得できませんでした。");
  }

  const article = document.querySelector("article[data-testid='tweet']") || document.querySelector("article");
  if (!article) {
    throw new Error("投稿DOMを取得できませんでした。");
  }

  const timeEl = article.querySelector("time");
  const createdAt = timeEl?.getAttribute("datetime") || "";
  if (!createdAt) {
    throw new Error("created_at を取得できませんでした。");
  }

  const textEl = article.querySelector("div[data-testid='tweetText']");
  const text = textEl?.innerText?.trim() || "";
  if (!text) {
    throw new Error("本文を取得できませんでした。");
  }

  const handleEl = article.querySelector("a[href*='/status/']");
  const handle = resolveHandle(handleEl?.getAttribute("href"));
  const nameEl = article.querySelector("div[dir='ltr'] span");
  const authorName = nameEl?.textContent?.trim() || handle;

  const imageUrls = [...article.querySelectorAll("img")]
    .map((img) => img.getAttribute("src") || "")
    .filter((src) => src.includes("twimg.com/media"))
    .map((imgUrl) => ({ url: imgUrl }))
    .slice(0, 10);

  return {
    tweet_id: tweetMatch[1],
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

function resolveHandle(href) {
  if (!href) return "@unknown";
  const match = href.match(/^\/([^/]+)\/status\//);
  if (!match) return "@unknown";
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
  const { root, body, okButton, copyButton } = createModal(title, detailText, isError, false);

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
  if (existing) existing.remove();

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
    overflow: "auto",
    background: "#fff",
    color: "#111",
    borderRadius: "12px",
    padding: "16px",
    fontFamily: "ui-sans-serif, system-ui, -apple-system, Segoe UI, sans-serif",
    boxShadow: "0 10px 30px rgba(0,0,0,0.25)"
  });

  const heading = document.createElement("h3");
  heading.textContent = title;
  heading.style.margin = "0 0 10px 0";
  heading.style.color = isError ? "#b42318" : "#0f5132";

  const body = document.createElement("div");

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

  const actions = document.createElement("div");
  Object.assign(actions.style, {
    display: "flex",
    justifyContent: "flex-end",
    gap: "8px",
    marginTop: "12px"
  });

  const copyButton = createButton("コピー", "#e4e7ec", "#111");
  const okButton = createButton(withCancel ? "再試行する" : "OK", "#1d4ed8", "#fff");
  const cancelButton = withCancel ? createButton("閉じる", "#e4e7ec", "#111") : null;

  body.appendChild(detail);
  actions.appendChild(copyButton);
  if (cancelButton) actions.appendChild(cancelButton);
  actions.appendChild(okButton);

  panel.appendChild(heading);
  panel.appendChild(body);
  panel.appendChild(actions);
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
    window.prompt("コピーできないため手動でコピーしてください", text);
  }
}
