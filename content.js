const STYLE_ID = "x-post-archive-inline-style";
const BUTTON_CLASS = "x-post-archive-inline-save";
const BUTTON_HOST_ATTR = "data-x-post-archive-save-host";
const ARTICLE_BOUND_ATTR = "data-x-post-archive-bound";
const MODAL_ID = "x-post-archive-modal";
const TOAST_ID = "x-post-archive-toast";
const TAG_LIMIT = 30;
const TAG_MAX_COUNT = 10;

let scanScheduled = false;
const postStatusCache = new Map();
const postStatusInflight = new Map();

boot();

function boot() {
  installStyles();
  scanArticles();
  const observer = new MutationObserver(() => {
    if (scanScheduled) return;
    scanScheduled = true;
    requestAnimationFrame(() => {
      scanScheduled = false;
      scanArticles();
    });
  });
  observer.observe(document.documentElement, { childList: true, subtree: true });
}

function installStyles() {
  if (document.getElementById(STYLE_ID)) return;
  const style = document.createElement("style");
  style.id = STYLE_ID;
  style.textContent = `
    .${BUTTON_CLASS}{display:inline-flex;align-items:center;justify-content:center;min-width:104px;height:32px;border:none;border-radius:999px;padding:0 16px;background:#1d9bf0;color:#fff;font-size:13px;font-weight:700;cursor:pointer;transition:transform .12s ease,opacity .12s ease,background .12s ease;box-shadow:0 4px 12px rgba(29,155,240,.28)}
    .${BUTTON_CLASS}:hover{background:#1681cc;transform:translateY(-1px)}
    .${BUTTON_CLASS}:disabled{cursor:default;opacity:.72;transform:none;box-shadow:none}
    .x-post-archive-modal-root{position:fixed;inset:0;z-index:1000000;background:rgba(15,23,42,.46);display:flex;align-items:center;justify-content:center;padding:20px;box-sizing:border-box}
    .x-post-archive-modal-panel{width:min(460px,100%);max-height:min(720px,calc(100vh - 40px));background:#f6f7fb;color:#111827;border-radius:20px;box-shadow:0 24px 64px rgba(15,23,42,.26);display:flex;flex-direction:column;overflow:hidden;font-family:"Segoe UI","Hiragino Sans","Yu Gothic UI",sans-serif}
    .x-post-archive-sheet-header{padding:22px 22px 14px;font-size:28px;font-weight:800;line-height:1.2}
    .x-post-archive-sheet-body{padding:0 22px 22px;overflow:auto}
    .x-post-archive-sheet-note{background:#eef1f6;border-radius:16px;padding:14px 16px;margin-bottom:16px}
    .x-post-archive-sheet-note-title{font-size:15px;font-weight:700;margin-bottom:6px}
    .x-post-archive-sheet-note-text{font-size:13px;color:#667085;line-height:1.6;white-space:pre-wrap}
    .x-post-archive-section-title{display:flex;justify-content:space-between;align-items:center;font-size:15px;font-weight:800;margin-bottom:10px}
    .x-post-archive-section-count{color:#6b7280;font-size:14px;font-weight:700}
    .x-post-archive-status{min-height:22px;margin-bottom:10px;font-size:13px;line-height:1.5;color:#6b7280;white-space:pre-wrap}
    .x-post-archive-status.is-error{color:#b42318}.x-post-archive-status.is-success{color:#15803d}
    .x-post-archive-selected-list{display:flex;flex-direction:column;gap:10px}
    .x-post-archive-selected-item{display:grid;grid-template-columns:1fr auto;gap:10px;align-items:center;border:1px solid #e5e7eb;background:#fff;border-radius:14px;padding:12px}
    .x-post-archive-selected-name{font-size:18px;line-height:1.4;word-break:break-word}
    .x-post-archive-remove-button{width:28px;height:28px;border:none;border-radius:999px;background:transparent;color:#6b7280;font-size:18px;cursor:pointer}
    .x-post-archive-link-button{margin-top:8px;border:none;background:transparent;color:#3b82f6;font-size:18px;font-weight:800;padding:0;cursor:pointer;text-align:left}
    .x-post-archive-suggestion-title{margin:12px 0 10px;font-size:14px;font-weight:700;color:#6b7280}
    .x-post-archive-suggestion-panel{border:1px solid #e5e7eb;border-radius:14px;background:#fff;overflow:hidden;margin-bottom:10px}
    .x-post-archive-suggestion-item{width:100%;border:none;background:transparent;padding:12px 14px;display:grid;grid-template-columns:1fr auto auto;gap:12px;align-items:center;cursor:pointer;text-align:left;font:inherit;color:inherit}
    .x-post-archive-suggestion-item + .x-post-archive-suggestion-item{border-top:1px solid #edf0f5}
    .x-post-archive-suggestion-item:hover{background:#f8fafc}
    .x-post-archive-suggestion-name{font-size:18px;line-height:1.4;word-break:break-word}
    .x-post-archive-suggestion-plus{color:#3b82f6;font-size:20px;font-weight:800;width:20px;text-align:center}
    .x-post-archive-suggestion-count{color:#6b7280;font-size:15px;white-space:nowrap}
    .x-post-archive-input-shell{border:2px solid #bfdbfe;border-radius:12px;background:#fff;padding:10px 12px;display:grid;grid-template-columns:1fr auto;gap:10px;align-items:center}
    .x-post-archive-tag-input{border:none;outline:none;background:transparent;font-size:18px;line-height:1.4;color:#111827;width:100%;min-width:0}
    .x-post-archive-tag-count{color:#6b7280;font-size:15px;white-space:nowrap}
    .x-post-archive-note-area{width:100%;min-height:110px;border:1px solid #d0d5dd;border-radius:14px;resize:vertical;padding:12px 14px;box-sizing:border-box;font:inherit;line-height:1.6;color:#111827;background:#fff;margin-top:8px}
    .x-post-archive-footer{display:flex;gap:10px;margin-top:16px}
    .x-post-archive-footer-button{flex:1 1 0;height:46px;border:none;border-radius:999px;display:inline-flex;align-items:center;justify-content:center;text-align:center;font-size:16px;font-weight:800;line-height:1;padding:0 18px;cursor:pointer}
    .x-post-archive-footer-button.secondary{background:#e5e7eb;color:#111827}.x-post-archive-footer-button.primary{background:#1d9bf0;color:#fff}
    .x-post-archive-detail-panel{width:min(760px,100%);max-height:90vh;background:#fff;color:#111827;border-radius:12px;box-shadow:0 10px 30px rgba(0,0,0,.25);display:flex;flex-direction:column;overflow:hidden;font-family:"Segoe UI","Hiragino Sans","Yu Gothic UI",sans-serif}
    .x-post-archive-detail-header{padding:16px 16px 8px}.x-post-archive-detail-header h3{margin:0;font-size:20px;line-height:1.3}
    .x-post-archive-detail-body{padding:8px 16px;overflow:auto;flex:1 1 auto}
    .x-post-archive-detail-textarea{width:100%;min-height:220px;resize:vertical;box-sizing:border-box;margin-top:8px;font-family:ui-monospace,SFMono-Regular,Menlo,monospace;font-size:12px;line-height:1.45;border:1px solid #d0d5dd;border-radius:8px;padding:8px}
    .x-post-archive-detail-footer{display:flex;justify-content:flex-end;gap:8px;padding:12px 16px 16px;border-top:1px solid #eaecf0;flex:0 0 auto}
    .x-post-archive-toast{position:fixed;top:20px;right:20px;z-index:1000001;min-width:440px;max-width:min(840px,calc(100vw - 40px));background:rgba(15,118,110,.96);color:#fff;border-radius:20px;padding:24px 28px;box-shadow:0 20px 40px rgba(15,23,42,.24);font-family:"Segoe UI","Hiragino Sans","Yu Gothic UI",sans-serif;opacity:0;transform:translateY(-10px);transition:opacity .18s ease,transform .18s ease;pointer-events:none}
    .x-post-archive-toast.is-visible{opacity:1;transform:translateY(0)}
    .x-post-archive-toast-title{font-size:24px;font-weight:800;line-height:1.3}
    .x-post-archive-toast-text{margin-top:8px;font-size:20px;line-height:1.5;opacity:.92;white-space:pre-wrap}
  `;
  document.documentElement.appendChild(style);
}

function scanArticles() {
  for (const article of document.querySelectorAll("article")) bindArticle(article);
}

function bindArticle(article) {
  if (!(article instanceof HTMLElement)) return;
  const tweetId = resolveTweetIdFromArticle(article);
  if (!tweetId) return;
  if (article.getAttribute(ARTICLE_BOUND_ATTR) === tweetId) return;
  article.setAttribute(ARTICLE_BOUND_ATTR, tweetId);
  article.querySelector(`[${BUTTON_HOST_ATTR}]`)?.remove();
  const actionBar = findActionBar(article);
  if (!actionBar) return;
  const host = document.createElement("div");
  host.setAttribute(BUTTON_HOST_ATTR, "true");
  Object.assign(host.style, { display: "flex", justifyContent: "flex-end", marginTop: "8px" });
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
  host.addEventListener("click", (event) => event.stopPropagation());
  host.appendChild(button);
  actionBar.insertAdjacentElement("afterend", host);
  void syncButtonState(tweetId, button);
}

async function syncButtonState(tweetId, button) {
  try {
    applyButtonStatus(button, await loadPostStatus(tweetId));
  } catch {
    setButtonState(button, "保存", false);
  }
}

function applyButtonStatus(button, status) {
  const exists = status?.exists === true;
  setButtonState(button, exists ? "上書き保存" : "保存", false);
  button.dataset.saved = exists ? "true" : "false";
  button.setAttribute("aria-label", exists ? "この投稿を上書き保存" : "この投稿を保存");
}

async function loadPostStatus(tweetId, force = false) {
  if (!force && postStatusCache.has(tweetId)) return postStatusCache.get(tweetId);
  if (!force && postStatusInflight.has(tweetId)) return await postStatusInflight.get(tweetId);
  const task = (async () => {
    const result = await chrome.runtime.sendMessage({ type: "GET_POST_STATUS", tweetId });
    if (!result?.ok) throw new Error(`POST_STATUS_FETCH_FAILED:${result?.status ?? 0}`);
    const status = { exists: result.body?.exists === true, post: result.body?.post || null };
    postStatusCache.set(tweetId, status);
    return status;
  })();
  postStatusInflight.set(tweetId, task);
  try {
    return await task;
  } finally {
    postStatusInflight.delete(tweetId);
  }
}

async function onInlineSaveClick(article, button) {
  const tweetId = resolveTweetIdFromArticle(article);
  const originalLabel = button.textContent;
  try {
    setButtonState(button, "準備中...", true);
    const payload = await extractPostData(article);
    const postStatus = await loadPostStatus(payload.tweet_id, true);
    const tagCatalogResult = await chrome.runtime.sendMessage({ type: "GET_TAG_CATALOG" });
    if (!tagCatalogResult?.ok) {
      await showResultDialog("タグ一覧の取得に失敗しました", buildErrorDetail(tagCatalogResult, { step: "GET_TAG_CATALOG", tweet_id: payload.tweet_id, url: payload.url }), true);
      return;
    }
    const formResult = await openSaveFormModal({
      tagCatalog: normalizeTagCatalog(tagCatalogResult.body?.tags),
      initialTags: postStatus.post?.tags ?? [],
      initialNote: postStatus.post?.note ?? ""
    });
    if (!formResult) {
      applyButtonStatus(button, postStatus);
      return;
    }
    payload.tags = formResult.tags;
    payload.note = formResult.note;
    if (postStatus.exists) {
      await saveOrUpdate("UPDATE_POST", payload, button, "上書き保存中...", "上書き保存しました", "投稿内容、メモ、タグを更新しました。");
      postStatusCache.set(payload.tweet_id, { exists: true, post: { ...payload } });
      flashSaved(button, true);
      return;
    }
    await saveOrUpdate("SAVE_POST", payload, button, "保存中...", "保存しました", "投稿を保存しました。");
    postStatusCache.set(payload.tweet_id, { exists: true, post: { ...payload } });
    flashSaved(button, true);
  } catch (error) {
    await showResultDialog("予期しないエラー", `Unhandled Error\n\n${String(error)}\n\n${error?.stack || ""}`, true);
  } finally {
    if (tweetId && button.textContent !== "保存" && button.textContent !== "上書き保存") {
      const cached = postStatusCache.get(tweetId);
      if (cached) applyButtonStatus(button, cached);
      else setButtonState(button, originalLabel || "保存", false);
    }
  }
}

async function saveOrUpdate(type, payload, button, progressLabel, successTitle, successText) {
  setButtonState(button, progressLabel, true);
  const result = await chrome.runtime.sendMessage({ type, payload });
  if (!result?.ok) {
    const body = result?.body || {};
    const detail = buildErrorDetail(result, payload);
    if (body.can_retry === true) {
      const retry = await showConfirmDialog(type === "UPDATE_POST" ? "上書き保存に失敗しました" : "保存に失敗しました", `${body.message || "リクエストに失敗しました。"}\n\n再試行しますか？`, detail);
      if (retry) {
        setButtonState(button, "再試行中...", true);
        const retryResult = await chrome.runtime.sendMessage({ type, payload });
        if (!retryResult?.ok) {
          await showResultDialog("再試行に失敗しました", buildErrorDetail(retryResult, payload), true);
          throw new Error("RETRY_FAILED");
        }
        showSuccessToast(successTitle, successText);
        return;
      }
    }
    await showResultDialog(type === "UPDATE_POST" ? "上書き保存に失敗しました" : "保存に失敗しました", detail, true);
    throw new Error("SAVE_OR_UPDATE_FAILED");
  }
  showSuccessToast(successTitle, successText);
}

function normalizeTagCatalog(tags) {
  if (!Array.isArray(tags)) return [];
  return tags.map((item) => ({ name: normalizeTag(item?.name || ""), count: Number(item?.count || 0) })).filter((item) => item.name).sort((a, b) => b.count - a.count || a.name.localeCompare(b.name, "ja"));
}

function flashSaved(button, existsAfterSave) {
  setButtonState(button, "保存済み", true);
  button.style.background = "#0f766e";
  button.style.boxShadow = "none";
  window.setTimeout(() => {
    button.style.background = "#1d9bf0";
    button.style.boxShadow = "0 4px 12px rgba(29, 155, 240, 0.28)";
    setButtonState(button, existsAfterSave ? "上書き保存" : "保存", false);
  }, 1800);
}

function setButtonState(button, label, disabled) {
  button.textContent = label;
  button.disabled = disabled;
}

async function extractPostData(article) {
  const tweetId = resolveTweetIdFromArticle(article);
  if (!tweetId) throw new Error("tweet_id を取得できませんでした。");
  const url = resolveCanonicalUrl(article, tweetId);
  const createdAt = article.querySelector("time")?.getAttribute("datetime") || "";
  if (!createdAt) throw new Error("created_at が見つかりませんでした。");
  const text = article.querySelector("div[data-testid='tweetText']")?.innerText?.trim() || "";
  const handleEl = article.querySelector(`a[href*="/status/${tweetId}"]`) || article.querySelector("a[href*='/status/']");
  const handle = resolveHandle(handleEl?.getAttribute("href"));
  const authorName = article.querySelector("div[dir='ltr'] span")?.textContent?.trim() || handle;
  const imageUrls = [...article.querySelectorAll("img")].map((img) => img.getAttribute("src") || "").filter((src) => src.includes("twimg.com/media")).map((urlValue) => ({ url: urlValue })).slice(0, 10);
  return { tweet_id: tweetId, url, author: { handle, name: authorName }, created_at: createdAt, text, tags: [], note: "", images: imageUrls };
}

function resolveTweetIdFromArticle(article) {
  for (const link of article.querySelectorAll("a[href*='/status/']")) {
    const match = (link.getAttribute("href") || "").match(/status\/(\d+)/);
    if (match) return match[1];
  }
  return "";
}

function resolveCanonicalUrl(article, tweetId) {
  const href = article.querySelector(`a[href*="/status/${tweetId}"]`)?.getAttribute("href") || article.querySelector("a[href*='/status/']")?.getAttribute("href") || `/i/status/${tweetId}`;
  return new URL(href, location.origin).toString();
}

function findActionBar(article) {
  for (const group of article.querySelectorAll("div[role='group']")) {
    if (group.querySelector("button[data-testid='reply']") || group.querySelector("button[data-testid='like']")) return group;
  }
  return article.querySelector("div[role='group']");
}

function resolveHandle(href) {
  const match = String(href || "").match(/^\/([^/]+)\/status\//);
  return match ? `@${match[1]}` : "@unknown";
}

function normalizeTag(rawTag) {
  return String(rawTag || "").trim();
}

function buildSuggestionItems(catalog, currentTags, query) {
  const normalizedQuery = normalizeTag(query);
  const selected = new Set(currentTags.map((tag) => tag.toLowerCase()));
  const list = [];
  for (const item of catalog) {
    if (!item.name) continue;
    if (normalizedQuery && !item.name.toLowerCase().includes(normalizedQuery.toLowerCase())) continue;
    if (selected.has(item.name.toLowerCase())) continue;
    list.push({ name: item.name, count: item.count, isCreateNew: false });
  }
  const exactMatch = catalog.some((item) => item.name.toLowerCase() === normalizedQuery.toLowerCase());
  if (normalizedQuery && !selected.has(normalizedQuery.toLowerCase()) && !exactMatch) list.unshift({ name: normalizedQuery, count: 0, isCreateNew: true });
  return list;
}

function openSaveFormModal({ tagCatalog, initialTags, initialNote }) {
  closeExistingModal();
  const state = { tags: [...new Set((initialTags || []).map(normalizeTag).filter(Boolean))], note: initialNote || "", query: "", showAddPanel: false, tagCatalog: tagCatalog || [] };
  return new Promise((resolve) => {
    const root = document.createElement("div");
    root.id = MODAL_ID;
    root.className = "x-post-archive-modal-root";
    const panel = document.createElement("div");
    panel.className = "x-post-archive-modal-panel";
    panel.innerHTML = `
      <div class="x-post-archive-sheet-header">保存内容を編集</div>
      <div class="x-post-archive-sheet-body">
        <div class="x-post-archive-sheet-note">
          <div class="x-post-archive-sheet-note-title">保存前にタグとメモを追加できます</div>
          <div class="x-post-archive-sheet-note-text">タグはこの投稿と一緒に保存されます。候補から追加するか、新しいタグ名を入力して保存できます。</div>
        </div>
        <div class="x-post-archive-section-title"><span>投稿タグ</span><span class="x-post-archive-section-count"></span></div>
        <div class="x-post-archive-status"></div>
        <div class="x-post-archive-selected-list"></div>
        <button type="button" class="x-post-archive-link-button">タグを追加</button>
        <div data-add-panel hidden>
          <div class="x-post-archive-suggestion-title">候補から追加</div>
          <div class="x-post-archive-suggestion-panel"></div>
          <div class="x-post-archive-input-shell">
            <input type="text" class="x-post-archive-tag-input" placeholder="タグを追加" maxlength="${TAG_LIMIT}">
            <div class="x-post-archive-tag-count"></div>
          </div>
        </div>
        <div class="x-post-archive-section-title" style="margin-top:16px">メモ</div>
        <textarea class="x-post-archive-note-area" placeholder="メモを入力"></textarea>
        <div class="x-post-archive-footer">
          <button type="button" class="x-post-archive-footer-button secondary">キャンセル</button>
          <button type="button" class="x-post-archive-footer-button primary">保存</button>
        </div>
      </div>`;
    root.appendChild(panel);
    document.body.appendChild(root);

    const countEl = panel.querySelector(".x-post-archive-section-count");
    const statusEl = panel.querySelector(".x-post-archive-status");
    const listEl = panel.querySelector(".x-post-archive-selected-list");
    const addButton = panel.querySelector(".x-post-archive-link-button");
    const addPanel = panel.querySelector("[data-add-panel]");
    const suggestionTitle = panel.querySelector(".x-post-archive-suggestion-title");
    const suggestionPanel = panel.querySelector(".x-post-archive-suggestion-panel");
    const input = panel.querySelector(".x-post-archive-tag-input");
    const countText = panel.querySelector(".x-post-archive-tag-count");
    const memo = panel.querySelector(".x-post-archive-note-area");
    const cancel = panel.querySelector(".x-post-archive-footer-button.secondary");
    const save = panel.querySelector(".x-post-archive-footer-button.primary");
    memo.value = state.note;

    const setStatus = (text, kind = "") => {
      statusEl.textContent = text;
      statusEl.className = "x-post-archive-status";
      if (kind) statusEl.classList.add(`is-${kind}`);
    };

    const renderTags = () => {
      countEl.textContent = `${state.tags.length}/${TAG_MAX_COUNT}`;
      listEl.innerHTML = "";
      if (state.tags.length === 0) {
        const empty = document.createElement("div");
        empty.className = "x-post-archive-sheet-note-text";
        empty.textContent = "タグはまだありません。";
        listEl.appendChild(empty);
        return;
      }
      for (const tag of state.tags) {
        const item = document.createElement("div");
        item.className = "x-post-archive-selected-item";
        item.innerHTML = `<div class="x-post-archive-selected-name">#${escapeHtml(tag)}</div><button type="button" class="x-post-archive-remove-button">×</button>`;
        item.querySelector("button").addEventListener("click", () => {
          state.tags = state.tags.filter((x) => x.toLowerCase() !== tag.toLowerCase());
          renderTags();
          renderSuggestions();
          setStatus(`#${tag} を外しました。`, "success");
        });
        listEl.appendChild(item);
      }
    };

    const addTag = (tag) => {
      const normalized = normalizeTag(tag);
      if (!normalized) return setStatus("タグ名を入力してください。", "error");
      if (normalized.length > TAG_LIMIT) return setStatus(`タグは ${TAG_LIMIT} 文字以内で入力してください。`, "error");
      if (state.tags.some((x) => x.toLowerCase() === normalized.toLowerCase())) return setStatus(`#${normalized} は追加済みです。`, "error");
      if (state.tags.length >= TAG_MAX_COUNT) return setStatus(`タグは最大${TAG_MAX_COUNT}件までです。`, "error");
      state.tags = [...state.tags, normalized];
      state.query = "";
      input.value = "";
      renderTags();
      renderSuggestions();
      setStatus(`#${normalized} を追加しました。`, "success");
    };

    const renderSuggestions = () => {
      countText.textContent = `${state.query.length}/${TAG_LIMIT}`;
      if (!state.showAddPanel) {
        addPanel.hidden = true;
        return;
      }
      addPanel.hidden = false;
      suggestionPanel.innerHTML = "";
      const suggestions = buildSuggestionItems(state.tagCatalog, state.tags, state.query);
      suggestionTitle.style.display = suggestions.length ? "" : "none";
      suggestionPanel.style.display = suggestions.length ? "" : "none";
      for (const item of suggestions) {
        const row = document.createElement("button");
        row.type = "button";
        row.className = "x-post-archive-suggestion-item";
        row.innerHTML = `<div class="x-post-archive-suggestion-name">${escapeHtml(item.isCreateNew ? `#${item.name} を新規作成` : `#${item.name}`)}</div><div class="x-post-archive-suggestion-plus">+</div><div class="x-post-archive-suggestion-count">${item.isCreateNew ? "新規" : `${item.count} 件`}</div>`;
        row.addEventListener("click", () => addTag(item.name));
        suggestionPanel.appendChild(row);
      }
    };

    addButton.addEventListener("click", () => {
      state.showAddPanel = true;
      renderSuggestions();
      input.focus();
    });
    input.addEventListener("input", () => {
      state.query = input.value;
      renderSuggestions();
    });
    input.addEventListener("keydown", (event) => {
      if (event.key !== "Enter") return;
      event.preventDefault();
      const suggestions = buildSuggestionItems(state.tagCatalog, state.tags, state.query);
      if (suggestions.length) addTag(suggestions[0].name);
    });
    memo.addEventListener("input", () => {
      state.note = memo.value;
    });
    cancel.addEventListener("click", () => {
      root.remove();
      resolve(null);
    });
    save.addEventListener("click", () => {
      root.remove();
      resolve({ tags: [...state.tags], note: state.note.trim() });
    });
    root.addEventListener("click", (event) => {
      if (event.target === root) {
        root.remove();
        resolve(null);
      }
    });

    renderTags();
    renderSuggestions();
  });
}

function buildErrorDetail(result, payload) {
  return [`status: ${result?.status ?? "unknown"}`, `ok: ${result?.ok === true}`, "", "response.body:", JSON.stringify(result?.body || {}, null, 2), "", "request.payload:", JSON.stringify(payload || {}, null, 2)].join("\n");
}

async function showResultDialog(title, detailText, isError) {
  closeExistingModal();
  const { root, okButton, copyButton } = createDetailModal(title, detailText, isError, false);
  copyButton.addEventListener("click", async () => copyText(detailText));
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
  copyButton.addEventListener("click", async () => copyText(detailText));
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
  panel.innerHTML = `<div class="x-post-archive-detail-header"><h3 style="color:${isError ? "#b42318" : "#0f5132"}">${escapeHtml(title)}</h3></div><div class="x-post-archive-detail-body"><textarea readonly class="x-post-archive-detail-textarea"></textarea></div><div class="x-post-archive-detail-footer"></div>`;
  panel.querySelector("textarea").value = detailText;
  const footer = panel.querySelector(".x-post-archive-detail-footer");
  const copyButton = createDetailButton("コピー", "#e4e7ec", "#111");
  const okButton = createDetailButton(withCancel ? "再試行" : "OK", "#1d4ed8", "#fff");
  const cancelButton = withCancel ? createDetailButton("キャンセル", "#e4e7ec", "#111") : null;
  footer.append(copyButton);
  if (cancelButton) footer.append(cancelButton);
  footer.append(okButton);
  root.appendChild(panel);
  document.body.appendChild(root);
  const textarea = panel.querySelector("textarea");
  textarea.focus();
  textarea.select();
  return { root, body: panel.querySelector(".x-post-archive-detail-body"), okButton, cancelButton, copyButton };
}

function createDetailButton(label, background, color) {
  const button = document.createElement("button");
  button.type = "button";
  button.textContent = label;
  Object.assign(button.style, { border: "none", borderRadius: "8px", padding: "8px 12px", background, color, cursor: "pointer", fontWeight: "600" });
  return button;
}

function closeExistingModal() {
  document.getElementById(MODAL_ID)?.remove();
}

function showSuccessToast(title, message) {
  document.getElementById(TOAST_ID)?.remove();
  const toast = document.createElement("div");
  toast.id = TOAST_ID;
  toast.className = "x-post-archive-toast";
  toast.innerHTML = `<div class="x-post-archive-toast-title">${escapeHtml(title)}</div><div class="x-post-archive-toast-text">${escapeHtml(message)}</div>`;
  document.body.appendChild(toast);
  requestAnimationFrame(() => toast.classList.add("is-visible"));
  window.setTimeout(() => {
    toast.classList.remove("is-visible");
    window.setTimeout(() => toast.remove(), 180);
  }, 2200);
}

function waitButton(button) {
  return new Promise((resolve) => button.addEventListener("click", () => resolve(), { once: true }));
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

function escapeHtml(value) {
  return String(value ?? "").replaceAll("&", "&amp;").replaceAll("<", "&lt;").replaceAll(">", "&gt;").replaceAll('"', "&quot;");
}

