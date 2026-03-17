const STYLE_ID = "x-post-archive-inline-style";
const BUTTON_CLASS = "x-post-archive-inline-save";
const BUTTON_HOST_ATTR = "data-x-post-archive-save-host";
const ARTICLE_BOUND_ATTR = "data-x-post-archive-bound";
const MODAL_ID = "x-post-archive-modal";
const TAG_LIMIT = 30;

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

    .x-post-archive-modal-root {
      position: fixed;
      inset: 0;
      z-index: 1000000;
      background: rgba(15, 23, 42, 0.46);
      display: flex;
      align-items: center;
      justify-content: center;
      padding: 20px;
      box-sizing: border-box;
    }

    .x-post-archive-modal-panel {
      width: min(460px, 100%);
      max-height: min(720px, calc(100vh - 40px));
      background: #f6f7fb;
      color: #111827;
      border-radius: 20px;
      box-shadow: 0 24px 64px rgba(15, 23, 42, 0.26);
      display: flex;
      flex-direction: column;
      overflow: hidden;
      font-family: "Segoe UI", "Hiragino Sans", "Yu Gothic UI", sans-serif;
    }

    .x-post-archive-sheet-header {
      padding: 22px 22px 14px;
      font-size: 28px;
      font-weight: 800;
      line-height: 1.2;
    }

    .x-post-archive-sheet-body {
      padding: 0 22px 22px;
      overflow: auto;
    }

    .x-post-archive-sheet-note {
      background: #eef1f6;
      border-radius: 16px;
      padding: 14px 16px;
      margin-bottom: 16px;
    }

    .x-post-archive-sheet-note-title {
      font-size: 15px;
      font-weight: 700;
      margin-bottom: 6px;
    }

    .x-post-archive-sheet-note-text {
      font-size: 13px;
      color: #667085;
      line-height: 1.6;
      white-space: pre-wrap;
    }

    .x-post-archive-section-title {
      display: flex;
      justify-content: space-between;
      align-items: center;
      font-size: 15px;
      font-weight: 800;
      margin-bottom: 10px;
    }

    .x-post-archive-section-count {
      color: #6b7280;
      font-size: 14px;
      font-weight: 700;
    }

    .x-post-archive-status {
      min-height: 22px;
      margin-bottom: 10px;
      font-size: 13px;
      line-height: 1.5;
      color: #6b7280;
      white-space: pre-wrap;
    }

    .x-post-archive-status.is-error {
      color: #b42318;
    }

    .x-post-archive-status.is-success {
      color: #15803d;
    }

    .x-post-archive-selected-list {
      display: flex;
      flex-direction: column;
      gap: 10px;
    }

    .x-post-archive-selected-item {
      display: grid;
      grid-template-columns: 1fr auto;
      gap: 10px;
      align-items: center;
      border: 1px solid #e5e7eb;
      background: #ffffff;
      border-radius: 14px;
      padding: 12px;
    }

    .x-post-archive-selected-name {
      font-size: 18px;
      line-height: 1.4;
      word-break: break-word;
    }

    .x-post-archive-remove-button {
      width: 28px;
      height: 28px;
      border: none;
      border-radius: 999px;
      background: transparent;
      color: #6b7280;
      font-size: 18px;
      cursor: pointer;
    }

    .x-post-archive-link-button {
      margin-top: 8px;
      border: none;
      background: transparent;
      color: #3b82f6;
      font-size: 18px;
      font-weight: 800;
      padding: 0;
      cursor: pointer;
      text-align: left;
    }

    .x-post-archive-suggestion-title {
      margin: 12px 0 10px;
      font-size: 14px;
      font-weight: 700;
      color: #6b7280;
    }

    .x-post-archive-suggestion-panel {
      border: 1px solid #e5e7eb;
      border-radius: 14px;
      background: #ffffff;
      overflow: hidden;
      margin-bottom: 10px;
    }

    .x-post-archive-suggestion-item {
      width: 100%;
      border: none;
      background: transparent;
      padding: 12px 14px;
      display: grid;
      grid-template-columns: 1fr auto auto;
      gap: 12px;
      align-items: center;
      cursor: pointer;
      text-align: left;
      font: inherit;
      color: inherit;
    }

    .x-post-archive-suggestion-item + .x-post-archive-suggestion-item {
      border-top: 1px solid #edf0f5;
    }

    .x-post-archive-suggestion-item:hover {
      background: #f8fafc;
    }

    .x-post-archive-suggestion-name {
      font-size: 18px;
      line-height: 1.4;
      word-break: break-word;
    }

    .x-post-archive-suggestion-plus {
      color: #3b82f6;
      font-size: 20px;
      font-weight: 800;
      width: 20px;
      text-align: center;
    }

    .x-post-archive-suggestion-count {
      color: #6b7280;
      font-size: 15px;
      white-space: nowrap;
    }

    .x-post-archive-input-shell {
      border: 2px solid #bfdbfe;
      border-radius: 12px;
      background: #ffffff;
      padding: 10px 12px;
      display: grid;
      grid-template-columns: 1fr auto;
      gap: 10px;
      align-items: center;
    }

    .x-post-archive-tag-input {
      border: none;
      outline: none;
      background: transparent;
      font-size: 18px;
      line-height: 1.4;
      color: #111827;
      width: 100%;
      min-width: 0;
    }

    .x-post-archive-tag-count {
      color: #6b7280;
      font-size: 15px;
      white-space: nowrap;
    }

    .x-post-archive-note-area {
      width: 100%;
      min-height: 110px;
      border: 1px solid #d0d5dd;
      border-radius: 14px;
      resize: vertical;
      padding: 12px 14px;
      box-sizing: border-box;
      font: inherit;
      line-height: 1.6;
      color: #111827;
      background: #ffffff;
      margin-top: 8px;
    }

    .x-post-archive-footer {
      display: flex;
      gap: 10px;
      margin-top: 16px;
    }

    .x-post-archive-footer-button {
      flex: 1 1 0;
      height: 46px;
      border: none;
      border-radius: 999px;
      display: inline-flex;
      align-items: center;
      justify-content: center;
      text-align: center;
      font-size: 16px;
      font-weight: 800;
      line-height: 1;
      padding: 0 18px;
      cursor: pointer;
    }

    .x-post-archive-footer-button.secondary {
      background: #e5e7eb;
      color: #111827;
    }

    .x-post-archive-footer-button.primary {
      background: #1d9bf0;
      color: #ffffff;
    }

    .x-post-archive-detail-panel {
      width: min(760px, 100%);
      max-height: 90vh;
      background: #ffffff;
      color: #111827;
      border-radius: 12px;
      box-shadow: 0 10px 30px rgba(0, 0, 0, 0.25);
      display: flex;
      flex-direction: column;
      overflow: hidden;
      font-family: "Segoe UI", "Hiragino Sans", "Yu Gothic UI", sans-serif;
    }

    .x-post-archive-detail-header {
      padding: 16px 16px 8px 16px;
    }

    .x-post-archive-detail-header h3 {
      margin: 0;
      font-size: 20px;
      line-height: 1.3;
    }

    .x-post-archive-detail-body {
      padding: 8px 16px;
      overflow: auto;
      flex: 1 1 auto;
    }

    .x-post-archive-detail-textarea {
      width: 100%;
      min-height: 220px;
      resize: vertical;
      box-sizing: border-box;
      margin-top: 8px;
      font-family: ui-monospace, SFMono-Regular, Menlo, monospace;
      font-size: 12px;
      line-height: 1.45;
      border: 1px solid #d0d5dd;
      border-radius: 8px;
      padding: 8px;
    }

    .x-post-archive-detail-footer {
      display: flex;
      justify-content: flex-end;
      gap: 8px;
      padding: 12px 16px 16px 16px;
      border-top: 1px solid #eaecf0;
      flex: 0 0 auto;
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
    setButtonState(button, "準備中...", true);

    const payload = await extractPostData(article);
    const tagCatalogResult = await chrome.runtime.sendMessage({ type: "GET_TAG_CATALOG" });
    const tagCatalog = normalizeTagCatalog(tagCatalogResult?.body?.tags);

    const formResult = await openSaveFormModal({
      tagCatalog,
      initialTags: payload.tags ?? [],
      initialNote: payload.note ?? ""
    });

    if (!formResult) {
      setButtonState(button, originalLabel || "保存", false);
      return;
    }

    payload.tags = formResult.tags;
    payload.note = formResult.note;

    setButtonState(button, "保存中...", true);
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

function normalizeTagCatalog(tags) {
  if (!Array.isArray(tags)) {
    return [];
  }

  return tags
    .map((item) => ({
      name: normalizeTag(item?.name || ""),
      count: Number(item?.count || 0)
    }))
    .filter((item) => item.name)
    .sort((left, right) => {
      if (right.count !== left.count) {
        return right.count - left.count;
      }

      return left.name.localeCompare(right.name, "ja");
    });
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

function normalizeTag(rawTag) {
  return String(rawTag || "").trim();
}

function buildSuggestionItems(catalog, currentTags, query) {
  const normalizedQuery = normalizeTag(query);
  const selectedSet = new Set(currentTags.map((tag) => tag.toLowerCase()));
  const suggestions = [];

  for (const item of catalog) {
    if (!item.name) {
      continue;
    }

    if (normalizedQuery && !item.name.toLowerCase().includes(normalizedQuery.toLowerCase())) {
      continue;
    }

    if (selectedSet.has(item.name.toLowerCase())) {
      continue;
    }

    suggestions.push({
      name: item.name,
      count: item.count,
      isCreateNew: false
    });
  }

  const exactMatch = catalog.some((item) => item.name.toLowerCase() === normalizedQuery.toLowerCase());
  if (normalizedQuery && !selectedSet.has(normalizedQuery.toLowerCase()) && !exactMatch) {
    suggestions.unshift({
      name: normalizedQuery,
      count: 0,
      isCreateNew: true
    });
  }

  return suggestions;
}

function openSaveFormModal({ tagCatalog, initialTags, initialNote }) {
  closeExistingModal();

  const state = {
    tags: [...new Set((initialTags || []).map(normalizeTag).filter(Boolean))],
    note: initialNote || "",
    query: "",
    showAddPanel: false,
    tagCatalog: tagCatalog || []
  };

  return new Promise((resolve) => {
    const root = document.createElement("div");
    root.id = MODAL_ID;
    root.className = "x-post-archive-modal-root";

    const panel = document.createElement("div");
    panel.className = "x-post-archive-modal-panel";

    const header = document.createElement("div");
    header.className = "x-post-archive-sheet-header";
    header.textContent = "保存内容を編集";

    const body = document.createElement("div");
    body.className = "x-post-archive-sheet-body";

    const noteBox = document.createElement("div");
    noteBox.className = "x-post-archive-sheet-note";
    noteBox.innerHTML = `
      <div class="x-post-archive-sheet-note-title">保存前にタグとメモを追加できます</div>
      <div class="x-post-archive-sheet-note-text">タグはこの投稿と一緒に保存されます。候補から追加するか、新しいタグ名を入力して保存できます。</div>
    `;

    const sectionTitle = document.createElement("div");
    sectionTitle.className = "x-post-archive-section-title";

    const sectionLabel = document.createElement("span");
    sectionLabel.textContent = "投稿タグ";

    const sectionCount = document.createElement("span");
    sectionCount.className = "x-post-archive-section-count";

    sectionTitle.append(sectionLabel, sectionCount);

    const statusText = document.createElement("div");
    statusText.className = "x-post-archive-status";

    const selectedList = document.createElement("div");
    selectedList.className = "x-post-archive-selected-list";

    const showAddButton = document.createElement("button");
    showAddButton.type = "button";
    showAddButton.className = "x-post-archive-link-button";
    showAddButton.textContent = "タグを追加";

    const addPanel = document.createElement("div");
    addPanel.hidden = true;

    const suggestionHeader = document.createElement("div");
    suggestionHeader.className = "x-post-archive-suggestion-title";
    suggestionHeader.textContent = "候補から追加";

    const suggestionPanel = document.createElement("div");
    suggestionPanel.className = "x-post-archive-suggestion-panel";

    const inputShell = document.createElement("div");
    inputShell.className = "x-post-archive-input-shell";

    const tagInput = document.createElement("input");
    tagInput.type = "text";
    tagInput.className = "x-post-archive-tag-input";
    tagInput.placeholder = "タグを追加";
    tagInput.maxLength = TAG_LIMIT;

    const tagCount = document.createElement("div");
    tagCount.className = "x-post-archive-tag-count";

    inputShell.append(tagInput, tagCount);
    addPanel.append(suggestionHeader, suggestionPanel, inputShell);

    const memoTitle = document.createElement("div");
    memoTitle.className = "x-post-archive-section-title";
    memoTitle.style.marginTop = "16px";
    memoTitle.textContent = "メモ";

    const memoArea = document.createElement("textarea");
    memoArea.className = "x-post-archive-note-area";
    memoArea.placeholder = "メモを入力";
    memoArea.value = state.note;

    const footer = document.createElement("div");
    footer.className = "x-post-archive-footer";

    const cancelButton = document.createElement("button");
    cancelButton.type = "button";
    cancelButton.className = "x-post-archive-footer-button secondary";
    cancelButton.textContent = "キャンセル";

    const saveButton = document.createElement("button");
    saveButton.type = "button";
    saveButton.className = "x-post-archive-footer-button primary";
    saveButton.textContent = "保存";

    footer.append(cancelButton, saveButton);
    body.append(noteBox, sectionTitle, statusText, selectedList, showAddButton, addPanel, memoTitle, memoArea, footer);
    panel.append(header, body);
    root.appendChild(panel);
    document.body.appendChild(root);

    const setStatus = (text, kind = "") => {
      statusText.textContent = text;
      statusText.className = "x-post-archive-status";
      if (kind === "error") {
        statusText.classList.add("is-error");
      }
      if (kind === "success") {
        statusText.classList.add("is-success");
      }
    };

    const renderSelectedTags = () => {
      sectionCount.textContent = `${state.tags.length}/10`;
      selectedList.innerHTML = "";

      if (state.tags.length === 0) {
        const empty = document.createElement("div");
        empty.className = "x-post-archive-sheet-note-text";
        empty.textContent = "タグはまだありません。";
        selectedList.appendChild(empty);
        return;
      }

      for (const tag of state.tags) {
        const item = document.createElement("div");
        item.className = "x-post-archive-selected-item";

        const name = document.createElement("div");
        name.className = "x-post-archive-selected-name";
        name.textContent = `#${tag}`;

        const remove = document.createElement("button");
        remove.type = "button";
        remove.className = "x-post-archive-remove-button";
        remove.textContent = "×";
        remove.addEventListener("click", () => {
          state.tags = state.tags.filter((current) => current.toLowerCase() !== tag.toLowerCase());
          renderSelectedTags();
          renderSuggestions();
          setStatus(`#${tag} を外しました。`, "success");
        });

        item.append(name, remove);
        selectedList.appendChild(item);
      }
    };

    const addTag = (tag) => {
      const normalized = normalizeTag(tag);
      if (!normalized) {
        setStatus("タグ名を入力してください。", "error");
        return;
      }

      if (normalized.length > TAG_LIMIT) {
        setStatus(`タグは ${TAG_LIMIT} 文字以内で入力してください。`, "error");
        return;
      }

      if (state.tags.some((current) => current.toLowerCase() === normalized.toLowerCase())) {
        setStatus(`#${normalized} は追加済みです。`, "error");
        return;
      }

      if (state.tags.length >= 10) {
        setStatus("タグは最大10件までです。", "error");
        return;
      }

      state.tags = [...state.tags, normalized];
      state.query = "";
      tagInput.value = "";
      renderSelectedTags();
      renderSuggestions();
      setStatus(`#${normalized} を追加しました。`, "success");
    };

    const renderSuggestions = () => {
      tagCount.textContent = `${state.query.length}/${TAG_LIMIT}`;

      if (!state.showAddPanel) {
        addPanel.hidden = true;
        return;
      }

      addPanel.hidden = false;
      suggestionPanel.innerHTML = "";

      const suggestions = buildSuggestionItems(state.tagCatalog, state.tags, state.query);
      suggestionHeader.style.display = suggestions.length > 0 ? "" : "none";
      suggestionPanel.style.display = suggestions.length > 0 ? "" : "none";

      for (const item of suggestions) {
        const row = document.createElement("button");
        row.type = "button";
        row.className = "x-post-archive-suggestion-item";
        row.addEventListener("click", () => addTag(item.name));

        const name = document.createElement("div");
        name.className = "x-post-archive-suggestion-name";
        name.textContent = item.isCreateNew ? `#${item.name} を新規作成` : `#${item.name}`;

        const plus = document.createElement("div");
        plus.className = "x-post-archive-suggestion-plus";
        plus.textContent = "+";

        const count = document.createElement("div");
        count.className = "x-post-archive-suggestion-count";
        count.textContent = item.isCreateNew ? "新規" : `${item.count} 件`;

        row.append(name, plus, count);
        suggestionPanel.appendChild(row);
      }
    };

    showAddButton.addEventListener("click", () => {
      state.showAddPanel = true;
      renderSuggestions();
      tagInput.focus();
    });

    tagInput.addEventListener("input", () => {
      state.query = tagInput.value;
      renderSuggestions();
    });

    tagInput.addEventListener("keydown", (event) => {
      if (event.key !== "Enter") {
        return;
      }

      event.preventDefault();
      const suggestions = buildSuggestionItems(state.tagCatalog, state.tags, state.query);
      if (suggestions.length === 0) {
        return;
      }

      addTag(suggestions[0].name);
    });

    memoArea.addEventListener("input", () => {
      state.note = memoArea.value;
    });

    cancelButton.addEventListener("click", () => {
      root.remove();
      resolve(null);
    });

    saveButton.addEventListener("click", () => {
      root.remove();
      resolve({
        tags: [...state.tags],
        note: state.note.trim()
      });
    });

    root.addEventListener("click", (event) => {
      if (event.target === root) {
        root.remove();
        resolve(null);
      }
    });

    renderSelectedTags();
    renderSuggestions();
  });
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
  closeExistingModal();

  const { root, okButton, copyButton } = createDetailModal(title, detailText, isError, false);
  copyButton.addEventListener("click", async () => {
    await copyText(detailText);
  });
  await waitButton(okButton);
  root.remove();
}

async function showConfirmDialog(title, message, detailText) {
  closeExistingModal();

  const { root, body, okButton, cancelButton, copyButton } = createDetailModal(title, detailText, true, true);
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

function createDetailModal(title, detailText, isError, withCancel) {
  const root = document.createElement("div");
  root.id = MODAL_ID;
  root.className = "x-post-archive-modal-root";

  const panel = document.createElement("div");
  panel.className = "x-post-archive-detail-panel";

  const header = document.createElement("div");
  header.className = "x-post-archive-detail-header";

  const heading = document.createElement("h3");
  heading.textContent = title;
  heading.style.color = isError ? "#b42318" : "#0f5132";
  header.appendChild(heading);

  const body = document.createElement("div");
  body.className = "x-post-archive-detail-body";

  const detail = document.createElement("textarea");
  detail.readOnly = true;
  detail.value = detailText;
  detail.className = "x-post-archive-detail-textarea";
  body.appendChild(detail);

  const footer = document.createElement("div");
  footer.className = "x-post-archive-detail-footer";

  const copyButton = createDetailButton("コピー", "#e4e7ec", "#111");
  const okButton = createDetailButton(withCancel ? "再試行" : "OK", "#1d4ed8", "#fff");
  const cancelButton = withCancel ? createDetailButton("キャンセル", "#e4e7ec", "#111") : null;

  footer.appendChild(copyButton);
  if (cancelButton) {
    footer.appendChild(cancelButton);
  }
  footer.appendChild(okButton);

  panel.append(header, body, footer);
  root.appendChild(panel);
  document.body.appendChild(root);

  detail.focus();
  detail.select();

  return { root, body, okButton, cancelButton, copyButton };
}

function createDetailButton(label, background, color) {
  const button = document.createElement("button");
  button.type = "button";
  button.textContent = label;
  Object.assign(button.style, {
    border: "none",
    borderRadius: "8px",
    padding: "8px 12px",
    background,
    color,
    cursor: "pointer",
    fontWeight: "600"
  });
  return button;
}

function closeExistingModal() {
  const existing = document.getElementById(MODAL_ID);
  if (existing) {
    existing.remove();
  }
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
    window.prompt("コピーに失敗しました。必要であれば手動でコピーしてください:", text);
  }
}
