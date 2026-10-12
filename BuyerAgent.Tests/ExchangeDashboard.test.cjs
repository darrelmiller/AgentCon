const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");

const html = fs.readFileSync(path.join(__dirname, "..", "BuyerAgent", "ExchangeDashboard.html"), "utf8");
const script = [...html.matchAll(/<script[^>]*>([\s\S]*?)<\/script>/g)].at(-1)[1];

class Element {
  children = [];
  dataset = {};
  listeners = {};
  attributes = {};
  textContent = "";
  scrollTop = 0;
  scrollHeight = 0;
  clientHeight = 0;
  isConnected = true;
  classes = new Set();
  classList = { add: value => this.classes.add(value), remove: value => this.classes.delete(value) };
  append(...elements) { this.children.push(...elements); }
  replaceChildren(...elements) { this.children = elements; }
  setAttribute(key, value) { this.attributes[key] = value; }
  addEventListener(name, handler) { this.listeners[name] = handler; }
  showModal() { this.open = true; }
  close() { this.open = false; this.listeners.close?.(); }
}

async function dashboard() {
  let copied;
  const elements = new Map([...html.matchAll(/\bid="([^"]+)"/g)].map(match => [match[1], new Element()]));
  const document = {
    body: new Element(), listeners: {},
    getElementById: id => {
      assert.ok(elements.has(id), `Missing dashboard element: ${id}`);
      return elements.get(id);
    },
    createElement: () => new Element(),
    addEventListener(name, handler) { this.listeners[name] = handler; }
  };
  const body = JSON.stringify({ result: { task: {
    status: { message: { metadata: { "agentcon.llmCalls": 0 }, parts: [{ text: "Quote ready." }] } },
    artifacts: [{ name: "quote", parts: [{ data: { quoteId: "q" }, mediaType: "application/vnd.test.quote+json" }] }]
  } } });
  const state = {
    capturedAt: "2026-10-11T18:00:00Z", buyer: { origin: "http://localhost:5100", status: "HTTP ready", nativeStructuredSupport: true },
    sellers: [{ origin: "http://localhost:6000", identifier: "urn:wine", name: "Wine supplier", status: "HTTP ready",
      structuredDataParts: true, controlsEnabled: true }],
    warnings: [],
    capture: { generation: 1, maxExchanges: 64, maxBodyBytes: 32768, droppedExchanges: 2,
      tasks: [{ agent: "seller", endpoint: "http://localhost:6000/a2a", id: "full-supplier-task-id",
        contextId: "full-context-id", state: "TASK_STATE_COMPLETED", updatedAt: "2026-10-11T18:00:00Z" }],
      exchanges: [{ id: 1, endpoint: "http://localhost:6000/a2a", startedAt: "2026-10-11T18:00:00Z",
        responseStartedAt: "2026-10-11T18:00:01Z", status: "complete", httpStatus: 200, durationMs: 20,
        traceId: "opaque-trace-id", request: JSON.stringify({ method: "SendMessage",
          params: { message: { parts: [{ text: "Quote wine." }], metadata: { "agentcon.llmCalls": 3 } } } }),
        response: body }]
    }
  };
  let refresh;
  vm.runInNewContext(script, {
    document, URLSearchParams, AbortController, location: { origin: "http://localhost:5100" },
    navigator: { clipboard: { writeText: async value => { copied = value; } } },
    fetch: async url => ({ ok: true, json: async () => url === "/demo/catalog" ?
      { specVersion: "1.0", entries: [{ identifier: "urn:test:buyer" }] } : state }),
    window: { setTimeout: handler => { refresh = handler; } }, setTimeout: () => {}
  });
  await new Promise(resolve => setImmediate(resolve));
  return { elements, document, state, clipboard: () => copied, refresh: async () => { await refresh(); } };
}

function text(element) {
  return [element.textContent, ...element.children.map(text)].join(" ");
}

test("minimal dashboard preserves states, counts, artifacts, task details and inspector", async () => {
  const { elements, state, refresh } = await dashboard();
  assert.equal(elements.get("connection").textContent, "Live");
  assert.match(elements.get("connection").title, /Last updated/);
  assert.match(elements.get("limits").textContent, /2 older exchanges evicted/);
  assert.equal(elements.get("buyer-native").checked, true);
  assert.equal(elements.get("seller-structures").disabled, false);
  const conversation = elements.get("conversation");
  assert.match(text(conversation), /LLM calls: 3/);
  assert.match(text(conversation), /LLM calls: 0/);
  assert.match(text(conversation), /1 artifact/);
  assert.match(conversation.children[0].title, /SendMessage.*HTTP 200/);
  const task = elements.get("seller-tasks").children[0];
  assert.match(text(task), /full-supplier-task-id.*full-context-id/);
  task.open = true;
  task.listeners.toggle();
  await refresh();
  assert.equal(elements.get("seller-tasks").children[0], task);
  state.capture.tasks[0].updatedAt = "2026-10-11T18:00:02Z";
  await refresh();
  assert.equal(elements.get("seller-tasks").children[0].open, true);
  conversation.children[1].listeners.click();
  assert.equal(elements.get("payload").textContent, "Quote ready.");
  assert.equal(elements.get("copy").disabled, false);
  assert.match(elements.get("metadata").children.at(-1).title, /opaque-trace-id/);
  elements.get("raw-tab").listeners.click();
  assert.match(elements.get("payload").textContent, /quoteId/);
});

test("tips are focus-accessible and dismissible; failures remain visible", async () => {
  const { elements, document, state, refresh } = await dashboard();
  assert.ok(!html.includes("Demo content capture is ON."));
  assert.match(html, /\.tip:focus-within \.tip-text/);
  assert.match(html, /aria-describedby="capture-tip"/);
  document.listeners.keydown({ key: "Escape" });
  assert.ok(document.body.classes.has("tips-dismissed"));
  document.listeners.focusin();
  assert.ok(!document.body.classes.has("tips-dismissed"));
  state.capture.exchanges[0].responseTruncated = true;
  state.capture.exchanges[0].response = JSON.stringify({ result: { message: {
    parts: [{ text: "Failed" }], metadata: { "agentcon.llmCalls": 1, "agentcon.llmCallsComplete": false }
  } } });
  state.sellers[0].problem = "Health endpoint unreachable.";
  await refresh();
  assert.match(text(elements.get("conversation")), /incomplete/);
  assert.match(text(elements.get("conversation")), /TRUNCATED/);
  assert.match(elements.get("seller-status").textContent, /Health endpoint unreachable/);
});

test("agent logos are inline theme-aware wine glass and wine crate vectors", () => {
  assert.match(html, /data-logo="wine-glass"/);
  assert.match(html, /data-logo="wine-crate"/);
  assert.match(html, /\.logo-wine \{ fill: var\(--cp-accent\)/);
  assert.match(html, /aria-hidden="true">\s*<svg viewBox="0 0 32 32" focusable="false"/);
  assert.ok(!html.includes('aria-hidden="true">B</span>'));
  assert.ok(!html.includes('aria-hidden="true">S</span>'));
});

test("right-aligned header AI Catalog button opens current JSON in a closable popup", async () => {
  assert.match(html, /header > \.row \{ margin-left: auto; justify-content: flex-end; \}/);
  assert.match(html, /<div class="row"><span id="connection"[^>]*>[^<]*<\/span><button id="catalog-link" type="button" aria-haspopup="dialog"[^>]*>AI Catalog<\/button><button id="pause"/);
  const { elements, clipboard } = await dashboard();
  let prevented = false;
  await elements.get("catalog-link").listeners.click({ preventDefault() { prevented = true; } });
  assert.ok(prevented);
  assert.equal(elements.get("agent-card-dialog").open, true);
  assert.equal(elements.get("agent-card-title").textContent, "AI Catalog");
  assert.match(elements.get("agent-card-json").textContent, /urn:test:buyer/);
  assert.equal(elements.get("agent-card-json").attributes["aria-label"], "Full AI Catalog JSON");
  assert.equal(elements.get("agent-card-source").href, "http://localhost:5100/demo/catalog");
  await elements.get("agent-card-copy-link").listeners.click();
  assert.equal(clipboard(), "http://localhost:5100/demo/catalog");
  assert.equal(elements.get("agent-card-copy-link").textContent, "Copied");
  elements.get("agent-card-close").listeners.click();
  assert.equal(elements.get("agent-card-dialog").open, false);
});
