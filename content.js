const BUTTON_ID = "x-post-archive-save-button";

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
      const retry = canRetry
        ? confirm(`保存に失敗しました。\n${body.message || "不明なエラー"}\n\n再試行しますか？`)
        : false;

      if (retry) {
        const retryResult = await chrome.runtime.sendMessage({ type: "SAVE_POST", payload });
        if (!retryResult?.ok) {
          alert(`再試行でも失敗しました。\n${retryResult?.body?.message || "不明なエラー"}`);
          return;
        }

        alert("保存に成功しました。");
        return;
      }

      alert(`保存を中止しました。\n${body.message || "不明なエラー"}`);
      return;
    }

    alert("保存に成功しました。");
  } catch (error) {
    alert(`保存処理に失敗しました。\n${String(error)}`);
  }
}

function extractPostData() {
  const url = location.href;
  const tweetMatch = url.match(/status\/(\d+)/);
  if (!tweetMatch) {
    throw new Error("tweet_idをURLから取得できませんでした。");
  }

  const article = document.querySelector("article[data-testid='tweet']") || document.querySelector("article");
  if (!article) {
    throw new Error("ポストDOMを取得できませんでした。");
  }

  const timeEl = article.querySelector("time");
  const createdAt = timeEl?.getAttribute("datetime") || "";
  if (!createdAt) {
    throw new Error("created_atを取得できませんでした。Xの表示を確認してください。");
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
    .map((url) => ({ url }))
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
