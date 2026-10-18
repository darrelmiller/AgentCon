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
  ownText = "";
  set textContent(value) { this.ownText = value; this.children = []; }
  get textContent() { return this.ownText + this.children.map(child => child.textContent).join(""); }
  scrollTop = 0;
  scrollHeight = 0;
  clientHeight = 0;
  isConnected = true;
  classes = new Set();
  classList = { add: value => this.classes.add(value), remove: value => this.classes.delete(value) };
  append(...elements) { this.children.push(...elements); }
  replaceChildren(...elements) { this.ownText = ""; this.children = elements; }
  setAttribute(key, value) { this.attributes[key] = value; }
  addEventListener(name, handler) { this.listeners[name] = handler; }
  showModal() { this.open = true; }
  close() { this.open = false; this.listeners.close?.(); }
}

async function dashboard() {
  let copied;
  let intercept;
  const elements = new Map([...html.matchAll(/\bid="([^"]+)"/g)].map(match => [match[1], new Element()]));
  const document = {
    body: new Element(), listeners: {},
    getElementById: id => {
      assert.ok(elements.has(id), `Missing dashboard element: ${id}`);
      return elements.get(id);
    },
    createElement: tag => Object.assign(new Element(), { tagName: tag }),
    createTextNode: value => ({ textContent: value, children: [] }),
    addEventListener(name, handler) { this.listeners[name] = handler; }
  };
  const body = JSON.stringify({ result: { task: {
    status: { message: { metadata: { "agentcon.llmCalls": 0 }, parts: [{ text: "Quote ready." }] } },
    artifacts: [{ name: "quote", parts: [{ data: { quoteId: "q" }, mediaType: "application/vnd.test.quote+json" }] }]
  } } });
  const state = {
    capturedAt: "2026-10-11T18:00:00Z", buyer: { origin: "http://localhost:5100", status: "HTTP ready", nativeStructuredSupport: true,
      llm: { calls: 3, responseTimeMs: 1250, timedCalls: 3, complete: true } },
    sellers: [{ origin: "http://localhost:6000", identifier: "urn:wine", name: "Wine supplier", status: "HTTP ready",
      structuredDataParts: true, controlsEnabled: true, llm: { calls: 0, responseTimeMs: 0, timedCalls: 0, complete: true } }],
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
    document, URL, URLSearchParams, AbortController, location: { origin: "http://localhost:5100" },
    navigator: { clipboard: { writeText: async value => { copied = value; } } },
    fetch: async url => await intercept?.(url) ?? ({ ok: true, json: async () => url === "/demo/catalog" ?
      { specVersion: "1.0", entries: [{ identifier: "urn:test:buyer" }] } : state }),
    window: { setTimeout: handler => { refresh = handler; } }, setTimeout: () => {}
  });
  await new Promise(resolve => setImmediate(resolve));
  return { elements, document, state, clipboard: () => copied, refresh: async () => { await refresh(); },
    intercept: handler => { intercept = handler; } };
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
  assert.doesNotMatch(text(conversation), /LLM calls:/);
  assert.match(text(elements.get("buyer-llm")), /LLM calls: 3.*1.25 s/);
  assert.match(text(elements.get("seller-llm")), /LLM calls: 0.*0.00 s/);
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

test("HTTP+JSON exchanges show unwrapped parts, the operation and the binding", async () => {
  const { elements, state, refresh } = await dashboard();
  Object.assign(state.capture.exchanges[0], {
    endpoint: "http://localhost:6000/a2a/rest/message:send", restOperation: "SendMessage",
    request: JSON.stringify({ message: { parts: [{ text: "Quote wine." }] } }),
    response: JSON.stringify({ task: { status: { message: { parts: [{ text: "Quote ready." }] } },
      artifacts: [{ name: "quote", parts: [{ data: { quoteId: "q" } }] }] } })
  });
  state.capture.exchanges.push({ id: 2, endpoint: "http://localhost:6000/a2a/rest/tasks/t", restOperation: "GetTask",
    startedAt: "2026-10-11T18:00:02Z", status: "sending", request: "GET /a2a/rest/tasks/t", response: "" });
  await refresh();
  const conversation = elements.get("conversation");
  assert.match(conversation.children[0].title, /SendMessage/);
  assert.match(conversation.children[0].children[0].children[0].title, /HTTP\+JSON/);
  assert.match(text(conversation), /1 artifact/);
  assert.match(text(conversation), /HTTP · poll.*GET \/a2a\/rest\/tasks\/t/);
  conversation.children[1].listeners.click();
  assert.equal(elements.get("payload").textContent, "Quote ready.");
});

test("tasks show status text and stay earliest-to-latest regardless of activity and updates", async () => {
  const { elements, state, refresh } = await dashboard();
  const original = state.capture.tasks[0];
  original.startedAt = "2026-10-11T18:00:00Z";
  original.updatedAt = "2026-10-11T18:00:05Z";
  original.statusMessage = "Quote ready.\nPlease review.";
  state.capture.tasks.unshift({ ...original, id: "later", state: "TASK_STATE_WORKING",
    startedAt: "2026-10-11T18:00:02Z", updatedAt: "2026-10-11T18:00:03Z", statusMessage: "<b>Working</b>" });
  await refresh();
  const tasks = elements.get("seller-tasks");
  assert.match(text(tasks.children[0]), /full-supplier-task-id.*Quote ready\.\nPlease review\./);
  assert.match(text(tasks.children[1]), /later.*<b>Working<\/b>/);
  original.updatedAt = "2026-10-11T18:00:10Z";
  original.statusMessage = "Updated quote.";
  await refresh();
  assert.match(text(tasks.children[0]), /full-supplier-task-id.*Updated quote\./);
  assert.match(text(tasks.children[1]), /later/);
});

test("User Agent exchanges survive the seller filter and expose content type and rejected wire", async () => {
  const { elements, state, refresh } = await dashboard();
  state.capture.exchanges.unshift({
    id: 2, inbound: true, endpoint: "http://localhost:5100/a2a", httpMethod: "POST",
    requestContentType: "application/json; charset=utf-8", responseContentType: "application/json",
    startedAt: "2026-10-11T17:59:58Z", responseStartedAt: "2026-10-11T17:59:59Z",
    status: "complete", httpStatus: 200, durationMs: 5,
    request: JSON.stringify({ method: "SendMessage", params: { message: { parts: [
      { mediaType: "application/vnd.agentcon.demo.wine-rfq.v1+json", data: { kind: "rfq" } }
    ] } } }),
    response: JSON.stringify({ error: { code: -32005, message: "CONTENT_TYPE_NOT_SUPPORTED" } })
  });
  await refresh();
  const conversation = elements.get("conversation");
  assert.equal(conversation.children.length, 4);
  assert.match(text(conversation.children[0]), /User Agent → Buyer Agent/);
  assert.match(text(conversation.children[1]), /Buyer Agent → User Agent/);
  conversation.children[0].listeners.click();
  assert.match(text(elements.get("metadata")), /POST.*Content-Type: application\/json; charset=utf-8/);
  assert.match(text(elements.get("parts")), /application\/vnd.agentcon.demo.wine-rfq.v1\+json/);
  elements.get("raw-tab").listeners.click();
  assert.match(elements.get("payload").textContent, /"mediaType": "application\/vnd.agentcon.demo.wine-rfq.v1\+json"/);
  conversation.children[1].listeners.click();
  assert.match(elements.get("inspector-hint").textContent, /Buyer Agent to User Agent/);
  assert.match(elements.get("payload").textContent, /CONTENT_TYPE_NOT_SUPPORTED/);
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
  state.sellers[0].llm = { calls: 2, responseTimeMs: 500, timedCalls: 1, complete: false };
  await refresh();
  assert.match(text(elements.get("seller-llm")), /2 \(incomplete\).*0.50 s \(partial\)/);
  assert.match(text(elements.get("conversation")), /TRUNCATED/);
  assert.match(elements.get("seller-status").textContent, /Health endpoint unreachable/);
});

test("agent counters reset with Clear and unavailable metrics are not presented as zero", async () => {
  const { elements, state, refresh } = await dashboard();
  state.buyer.llm = { calls: 0, responseTimeMs: 0, timedCalls: 0, complete: true };
  state.capture.generation++;
  await elements.get("clear").listeners.click();
  assert.match(text(elements.get("buyer-llm")), /LLM calls: 0.*0.00 s/);
  state.sellers[0].llm = null;
  await refresh();
  assert.match(text(elements.get("seller-llm")), /LLM calls: not reported.*time: not reported/);
  state.buyer.llm = { calls: 1, responseTimeMs: 0, timedCalls: 0 };
  await refresh();
  assert.match(text(elements.get("buyer-llm")), /LLM calls: 1.*time: not reported/);
});

test("agent logos are inline theme-aware wine glass and wine crate vectors", () => {
  assert.match(html, /data-logo="wine-glass"/);
  assert.match(html, /data-logo="wine-crate"/);
  assert.match(html, /\.logo-wine \{ fill: var\(--cp-accent\)/);
  assert.match(html, /aria-hidden="true">\s*<svg viewBox="0 0 32 32" focusable="false"/);
  assert.ok(!html.includes('aria-hidden="true">B</span>'));
  assert.ok(!html.includes('aria-hidden="true">S</span>'));
});

test("changing the selected supplier displays that agent's totals", async () => {
  const { elements, state, refresh } = await dashboard();
  state.sellers.push({ ...state.sellers[0], origin: "http://localhost:6100", identifier: "urn:second",
    llm: { calls: 8, responseTimeMs: 3200, timedCalls: 8, complete: true } });
  await refresh();
  elements.get("seller-select").value = "http://localhost:6100";
  elements.get("seller-select").listeners.change();
  assert.match(text(elements.get("seller-llm")), /LLM calls: 8.*3.20 s/);
  assert.match(text(elements.get("buyer-llm")), /LLM calls: 3.*1.25 s/);
});

test("a poll started before Clear cannot restore old agent totals afterward", async () => {
  const { elements, state, refresh, intercept } = await dashboard();
  const old = structuredClone(state);
  let finish;
  intercept(url => url === "/demo/state" ? new Promise(resolve => { finish = resolve; }) : undefined);
  const pending = refresh();
  await elements.get("clear").listeners.click();
  finish({ ok: true, json: async () => old });
  await pending;
  assert.match(text(elements.get("buyer-llm")), /LLM calls: 0.*0.00 s/);
  assert.match(text(elements.get("seller-llm")), /LLM calls: 0.*0.00 s/);
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

const reply = value => ({ task: { status: { message: { parts: [{ text: value }] } } } });
const anchors = element => element.children.filter(child => child.tagName === "a");

async function inspectReply(value, rest = false) {
  const ui = await dashboard();
  Object.assign(ui.state.capture.exchanges[0], {
    restOperation: rest ? "SendMessage" : null,
    response: JSON.stringify(rest ? value : { result: value })
  });
  await ui.refresh();
  ui.elements.get("conversation").children[1].listeners.click();
  return ui;
}

test("reply URLs and Markdown links preserve whitespace, punctuation, safe targets and copy source", async () => {
  const source = 'Receipt:\n  https://example.com/receipt?id=1&view=full.\n[Order](https://example.com/order_(demo) "View order")';
  const ui = await inspectReply(reply(source));
  const payload = ui.elements.get("payload");
  assert.equal(payload.textContent, "Receipt:\n  https://example.com/receipt?id=1&view=full.\nOrder");
  assert.deepEqual(anchors(payload).map(a => a.href), [
    "https://example.com/receipt?id=1&view=full", "https://example.com/order_(demo)"
  ]);
  for (const a of anchors(payload)) {
    assert.equal(a.target, "_blank");
    assert.equal(a.rel, "noopener noreferrer");
  }
  await ui.elements.get("copy").listeners.click();
  assert.equal(ui.clipboard(), source);
});

test("unsafe schemes, malformed URLs, HTML, images and code remain literal", async () => {
  const source = '<img src=x onerror=alert(1)> [Run](javascript:alert(1)) [Data](data:text/html,bad) ' +
    '[File](file:///tmp/x) [Relative](/demo) [Protocol](//example.com) [Mail](mailto:x@example.com) ' +
    '[Bad](https://) ![Image](https://example.com/image) `https://example.com/code`\n' +
    '```html\nhttps://example.com/fenced\n```\n[Control](https://example.com/\u0000x)';
  const ui = await inspectReply(reply(source));
  const payload = ui.elements.get("payload");
  assert.equal(payload.textContent, source);
  assert.equal(anchors(payload).length, 0);
  assert.ok(payload.children.every(child => !child.tagName));
});

test("URL boundaries preserve balanced parentheses and trailing prose punctuation", async () => {
  const ui = await inspectReply(reply("See (HTTPS://example.com/a_(b)), http://localhost:6000/receipt! " +
    "[Receipt](<https://example.com/receipt?q=1&x=2>)"));
  assert.deepEqual(anchors(ui.elements.get("payload")).map(a => a.href), [
    "HTTPS://example.com/a_(b)", "http://localhost:6000/receipt", "https://example.com/receipt?q=1&x=2"
  ]);
  assert.equal(ui.elements.get("payload").textContent,
    "See (HTTPS://example.com/a_(b)), http://localhost:6000/receipt! Receipt");
});

test("JSON-RPC and HTTP+JSON replies, SSE status and artifacts linkify only text content", async () => {
  for (const rest of [false, true]) {
    const ui = await inspectReply({ message: { parts: [{ text: "https://example.com/message" }] } }, rest);
    assert.equal(anchors(ui.elements.get("payload"))[0].href, "https://example.com/message");
    const events = [
      { statusUpdate: { status: { message: { parts: [{ text: "https://example.com/status" }] } } } },
      { artifactUpdate: { artifact: { name: "Receipt", parts: [
        { text: "[Receipt](https://example.com/artifact)" }, { data: { url: "https://example.com/data" } }
      ] } } }
    ];
    const body = events.map(event => `data: ${JSON.stringify(rest ? event : { result: event })}\n\n`).join("");
    ui.state.capture.exchanges[0].response = body;
    await ui.refresh();
    const { elements } = ui;
    assert.equal(anchors(elements.get("payload"))[0].href, "https://example.com/status");
    elements.get("parts").children[1].listeners.click();
    assert.equal(anchors(elements.get("payload"))[0].href, "https://example.com/artifact");
    elements.get("parts").children[2].listeners.click();
    assert.equal(anchors(elements.get("payload")).length, 0);
    assert.equal(elements.get("payload").textContent, JSON.stringify({ url: "https://example.com/data" }, null, 2));
    elements.get("raw-tab").listeners.click();
    assert.equal(anchors(elements.get("payload")).length, 0);
    assert.equal(elements.get("payload").textContent, body);
    elements.get("content-tab").listeners.click();
    elements.get("parts").children[0].listeners.click();
    assert.equal(anchors(elements.get("payload")).length, 1);
    ui.state.capture.exchanges[0].request = JSON.stringify(rest
      ? { message: { parts: [{ text: "https://outgoing.example" }] } }
      : { params: { message: { parts: [{ text: "https://outgoing.example" }] } } });
    await ui.refresh();
    elements.get("conversation").children[0].listeners.click();
    assert.equal(anchors(elements.get("payload")).length, 0);
    assert.equal(elements.get("payload").textContent, "https://outgoing.example");
  }
});

test("raw JSON and cleared selections remove reply anchors", async () => {
  const ui = await inspectReply(reply("https://example.com/reply"));
  const { elements } = ui;
  elements.get("raw-tab").listeners.click();
  assert.equal(elements.get("payload").textContent,
    JSON.stringify(JSON.parse(ui.state.capture.exchanges[0].response), null, 2));
  assert.equal(anchors(elements.get("payload")).length, 0);
  elements.get("content-tab").listeners.click();
  assert.equal(anchors(elements.get("payload")).length, 1);
  await elements.get("clear").listeners.click();
  assert.equal(elements.get("payload").textContent, "No message selected.");
  assert.equal(anchors(elements.get("payload")).length, 0);
  assert.equal(elements.get("copy").disabled, true);
});
