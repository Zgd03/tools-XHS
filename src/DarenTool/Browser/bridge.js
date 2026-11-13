(() => {
  'use strict';
  if (window.top !== window || window.__darenBridge) return;
  const trusted = location.hostname === 'www.xiaohongshu.com' || location.hostname === 'xiaohongshu.com' || location.hostname === 'pgy.xiaohongshu.com';
  if (!trusted) return;
  window.__darenBridge = true;
  const post = data => { try { window.chrome.webview.postMessage(data); } catch (_) {} };
  const parse = data => { try { return typeof data === 'string' ? JSON.parse(data) : data; } catch (_) { return null; } };
  const commonAlphabet = 'ZmserbBoHQtNP+wOcza/LpngG8yJq42KWYj0DSfdikx3VT16IlUAFM97hECvuRX5';
  const standardAlphabet = 'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/';
  let commonState = null, commonCounter = 0;
  function rememberHeaders(input) {
    try {
      const headers = new Headers(input);
      const common = headers.get('x-s-common');
      if (!common) return;
      const standard = [...common].map(c => c === '=' ? c : standardAlphabet[commonAlphabet.indexOf(c)]).join('');
      const decoded = JSON.parse(new TextDecoder().decode(Uint8Array.from(atob(standard), c => c.charCodeAt(0))));
      if (decoded.x3 !== 'xhs-pc-web' || !decoded.x5 || !decoded.x12) return;
      commonState = decoded;
      commonCounter = Math.max(commonCounter, Number(decoded.x10) || 0);
    } catch (_) {}
  }
  function freshCommon() {
    if (!commonState) throw new Error('网页签名状态尚未就绪，请先登录小红书并刷新首页，等待笔记加载后重试');
    const value = {...commonState, x10: ++commonCounter};
    const bytes = new TextEncoder().encode(JSON.stringify(value));
    const standard = btoa(Array.from(bytes, c => String.fromCharCode(c)).join(''));
    return [...standard].map(c => c === '=' ? c : commonAlphabet[standardAlphabet.indexOf(c)]).join('');
  }
  const observe = (url, body, response, status) => {
    try {
      const u = new URL(url, location.href);
      const isPgy = u.hostname === 'pgy.xiaohongshu.com' && u.pathname === '/api/solar/content/note/list';
      const isNote = u.hostname.endsWith('.xiaohongshu.com') && u.pathname === '/api/sns/web/v1/feed';
      if (!isPgy && !isNote) return;
      const value = parse(response);
      if (!value || typeof value !== 'object') return;
      post({kind: isPgy ? 'report' : 'note', request: parse(body), response: value, status});
    } catch (_) {}
  };
  const nativeFetch = window.fetch;
  window.fetch = async function(input, init) {
    rememberHeaders(init?.headers || (input instanceof Request ? input.headers : undefined));
    let body = init?.body;
    if (body == null && input instanceof Request) {
      try { body = await input.clone().text(); } catch (_) {}
    }
    const response = await nativeFetch.apply(this, arguments);
    const url = typeof input === 'string' ? input : input?.url;
    if (url && /\/api\/(?:solar\/content\/note\/list|sns\/web\/v1\/feed)(?:\?|$)/.test(url)) {
      response.clone().text().then(text => observe(url, body, text, response.status)).catch(() => {});
    }
    return response;
  };
  const originalOpen = XMLHttpRequest.prototype.open;
  const originalSend = XMLHttpRequest.prototype.send;
  const originalHeader = XMLHttpRequest.prototype.setRequestHeader;
  const requests = new WeakMap();
  const activeCommands = new Map();
  XMLHttpRequest.prototype.open = function(method, url) {
    requests.set(this, {url: String(url), headers: {}});
    return originalOpen.apply(this, arguments);
  };
  XMLHttpRequest.prototype.setRequestHeader = function(name, value) {
    const info = requests.get(this);
    if (info) info.headers[String(name).toLowerCase()] = String(value);
    return originalHeader.apply(this, arguments);
  };
  XMLHttpRequest.prototype.send = function(body) {
    const info = requests.get(this);
    if (info) rememberHeaders(info.headers);
    if (info) this.addEventListener('load', () => {
      try { observe(info.url, body, this.responseType === 'json' ? this.response : this.responseText, this.status); } catch (_) {}
    }, {once: true});
    return originalSend.apply(this, arguments);
  };
  function signature(path, data) {
    // Use the live official page's signer. Never reuse a captured X-s/X-t pair.
    const signers = [window._webmsxyw, window.seccore_signv2].filter(x => typeof x === 'function');
    for (const signer of signers) {
      try {
        const result = signer(path, data);
        if (!result || typeof result !== 'object') continue;
        const xs = result['X-s'] || result['x-s'] || result.xs;
        const xt = result['X-t'] || result['x-t'] || result.xt;
        if (!xs || !xt) continue;
        const headers = {'x-s': String(xs), 'x-t': String(xt)};
        const common = result['X-s-common'] || result['x-s-common'] || result.xs_common;
        if (common) headers['x-s-common'] = String(common);
        else if (location.hostname === 'www.xiaohongshu.com') headers['x-s-common'] = freshCommon();
        return headers;
      } catch (_) {}
    }
    throw new Error('当前官方页面未提供可调用的签名能力。请刷新并完成登录后重试；若持续失败，请反馈版本兼容问题。');
  }
  window.__daren = {
    cancel(id) { activeCommands.get(id)?.abort(); },
    run(id, path, data, endpoint, timeoutMs = 25000) {
      (async () => {
        const allowed = ['/api/sns/web/short_url', '/api/sns/web/v1/feed'].includes(path) && location.hostname === 'www.xiaohongshu.com' && endpoint === 'https://edith.xiaohongshu.com';
        const reportAllowed = path === '/api/solar/content/note/list' && location.hostname === 'pgy.xiaohongshu.com' && endpoint === location.origin;
        if (!allowed && !reportAllowed) throw new Error('请求来源或接口不符');
        const headers = signature(path, data);
        headers['content-type'] = 'application/json;charset=UTF-8';
        headers['x-b3-traceid'] = crypto.randomUUID().replaceAll('-', '').slice(0, 16);
        const ctrl = new AbortController();
        activeCommands.set(id, ctrl);
        const timer = setTimeout(() => ctrl.abort(), Math.min(25000, Math.max(1000, timeoutMs)));
        try {
          const response = await window.fetch(endpoint + path, {method: 'POST', credentials: 'include', headers, body: JSON.stringify(data), signal: ctrl.signal});
          const text = await response.text();
          const value = parse(text);
          if (!response.ok) throw new Error('接口 HTTP ' + response.status);
          if (!value) throw new Error('接口返回了非 JSON 内容，可能需要登录或验证');
          post({kind: 'command', id, result: value});
        } finally { clearTimeout(timer); activeCommands.delete(id); }
      })().catch(error => post({kind: 'command', id, error: String(error.message || error)}));
    },
    clickPage(action) {
      const visible = e => e && e.getClientRects().length > 0 && !e.disabled && e.getAttribute('aria-disabled') !== 'true' && !/disabled/.test(e.className || '');
      let candidates;
      if (action === 'query') {
        candidates = [...document.querySelectorAll('button,[role="button"],input[type="submit"]')].filter(e => (e.innerText || e.value || '').trim() === '查询' && visible(e));
      } else if (action === 'first') {
        candidates = [...document.querySelectorAll('.ant-pagination-item-1,.el-pager .number,.arco-pagination-item,[aria-label="第 1 页"],[title="1"]')]
          .filter(e => visible(e) && (e.textContent || '').trim() === '1');
      } else {
        candidates = [...document.querySelectorAll('.ant-pagination-next:not(.ant-pagination-disabled) button,.ant-pagination-next:not(.ant-pagination-disabled),.el-pagination .btn-next,.arco-pagination-item-next,[aria-label="Next Page"],[aria-label="下一页"],[title="下一页"]')].filter(visible);
        // Parent LI and its button can both match. Prefer the inner control.
        candidates = candidates.filter(e => !candidates.some(other => other !== e && e.contains(other)));
      }
      if (candidates.length !== 1) return false;
      candidates[0].click(); return true;
    },
    diagnostic() {
      return {path: location.pathname, readyState: document.readyState,
        hasInitialState: !!window.__INITIAL_STATE__,
        detailCount: Object.keys(window.__INITIAL_STATE__?.note?.noteDetailMap || {}).length};
    },
    noteState(id) {
      const state = window.__INITIAL_STATE__;
      const detail = state?.note?.noteDetailMap?.[id];
      const card = detail?.note || detail;
      if (!card) return null;
      const actual = String(card.noteId || card.note_id || card.id || '').toLowerCase();
      if (actual !== id) return null;
      const info = card.interactInfo || card.interact_info;
      if (!info) return null;
      return {id: actual, info};
    }
  };
})();
