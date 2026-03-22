(() => {
  const EVENT_NAME = "__x_post_archive_api_payload__";
  const MESSAGE_TYPE = "__x_post_archive_api_payload__";
  if (window.__xPostArchivePageHookInstalled) return;
  window.__xPostArchivePageHookInstalled = true;

  const originalFetch = window.fetch;
  window.fetch = async (...args) => {
    const response = await originalFetch(...args);
    tryProcessApiResponse(args[0], response);
    return response;
  };

  const originalOpen = XMLHttpRequest.prototype.open;
  const originalSend = XMLHttpRequest.prototype.send;
  XMLHttpRequest.prototype.open = function(method, url, ...rest) {
    this.__xPostArchiveUrl = url;
    return originalOpen.call(this, method, url, ...rest);
  };
  XMLHttpRequest.prototype.send = function(...args) {
    this.addEventListener("load", () => {
      try {
        const url = this.__xPostArchiveUrl || "";
        if (!isInterestingApiUrl(url)) return;
        const text = this.responseType && this.responseType !== "text" && this.responseType !== "" ? "" : this.responseText;
        if (!text) return;
        const json = JSON.parse(text);
        dispatchPayload(json);
      } catch {
      }
    });
    return originalSend.apply(this, args);
  };

  function tryProcessApiResponse(input, response) {
    try {
      const url = typeof input === "string" ? input : input?.url || "";
      if (!isInterestingApiUrl(url)) return;
      response.clone().json().then(dispatchPayload).catch(() => {});
    } catch {
    }
  }

  function isInterestingApiUrl(url) {
    const value = String(url || "");
    return value.includes("/TweetDetail")
      || value.includes("/UserTweets")
      || value.includes("/HomeTimeline")
      || value.includes("/SearchTimeline")
      || value.includes("/Bookmarks")
      || value.includes("/Likes");
  }

  function dispatchPayload(payload) {
    try {
      window.dispatchEvent(new CustomEvent(EVENT_NAME, { detail: payload }));
    } catch {
    }
    try {
      window.postMessage({ type: MESSAGE_TYPE, payload }, location.origin);
    } catch {
    }
  }
})();
