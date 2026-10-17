import QRCode from 'qrcode';

export const escapeHtml = value => String(value).replace(/[&<>"']/g,
  char => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[char]);

const theme = `
:root {
  color-scheme: light;
  --cp-bg: #f7f4ef;
  --cp-bg-elevated: #fcfbf8;
  --cp-surface: #ffffff;
  --cp-surface-soft: #f5f5f5;
  --cp-border: #dedede;
  --cp-border-strong: #919191;
  --cp-text: #242424;
  --cp-text-muted: #5c5c5c;
  --cp-text-soft: #6f6f6f;
  --cp-accent: #b11f4b;
  --cp-accent-hover: #9a1a41;
  --cp-accent-soft: rgba(177, 31, 75, 0.08);
  --cp-accent-fg: #ffffff;
  --cp-success: #16a34a;
  --cp-danger: #dc2626;
  --cp-warning: #f59e0b;
  --cp-link: #0078d4;
  --cp-shadow: 0 18px 48px rgba(0, 0, 0, 0.12);
  --cp-overlay: rgba(255, 255, 255, 0.8);
  --cp-panel: rgba(255, 255, 255, 0.86);
  --cp-panel-strong: rgba(255, 255, 255, 0.96);
  --cp-sheen: rgba(255, 255, 255, 0.55);
  --cp-highlight: rgba(177, 31, 75, 0.12);
}
html[data-theme="dark"] {
  color-scheme: dark;
  --cp-bg: #3d3b3a;
  --cp-bg-elevated: #343231;
  --cp-surface: #292929;
  --cp-surface-soft: #2e2e2e;
  --cp-border: #474747;
  --cp-border-strong: #5f5f5f;
  --cp-text: #dedede;
  --cp-text-muted: #919191;
  --cp-text-soft: #b0b0b0;
  --cp-accent: #fd8ea1;
  --cp-accent-hover: #fb7b91;
  --cp-accent-soft: rgba(253, 142, 161, 0.14);
  --cp-accent-fg: #1a1a1a;
  --cp-success: #4ade80;
  --cp-danger: #f87171;
  --cp-warning: #fbbf24;
  --cp-link: #4da6ff;
  --cp-shadow: 0 18px 48px rgba(0, 0, 0, 0.32);
  --cp-overlay: rgba(41, 41, 41, 0.88);
  --cp-panel: rgba(41, 41, 41, 0.72);
  --cp-panel-strong: rgba(41, 41, 41, 0.96);
  --cp-sheen: rgba(255, 255, 255, 0.04);
  --cp-highlight: rgba(253, 142, 161, 0.12);
}`;

function page(content, title = 'AgentCon DEMO issuer') {
  return `<!doctype html>
<html lang="en"><head><meta charset="UTF-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>${escapeHtml(title)}</title>
<script>
  (() => {
    const param = new URLSearchParams(window.location.search).get("scoutTheme");
    const theme =
      param || (window.matchMedia("(prefers-color-scheme: dark)").matches ? "dark" : "light");
    document.documentElement.setAttribute("data-theme", theme);
  })();
</script>
<style>${theme}
* { box-sizing: border-box; }
body { margin: 0; padding: 32px 16px; background: var(--cp-bg); color: var(--cp-text);
  font-family: "Segoe UI", Aptos, Calibri, -apple-system, BlinkMacSystemFont, sans-serif; line-height: 1.5; }
main { max-width: 560px; margin: auto; padding: 24px; border: 1px solid var(--cp-border);
  border-radius: 16px; background: var(--cp-surface); }
h1 { margin-top: 0; }
.warning { padding: 16px; border-radius: 0.625rem; background: var(--cp-accent-soft); color: var(--cp-text); }
.muted { color: var(--cp-text-muted); }
.qr { display: block; width: 320px; max-width: 100%; height: auto; margin: 24px auto; border-radius: 0.625rem; }
a { color: var(--cp-link); overflow-wrap: anywhere; }
.button { display: block; padding: 12px 16px; text-align: center; border-radius: 0.625rem;
  background: var(--cp-accent); color: var(--cp-accent-fg); text-decoration: none; }
.button:hover { background: var(--cp-accent-hover); }
code { font-family: Consolas, "Courier New", Courier, monospace; }
</style></head><body><main>
<h1>${escapeHtml(title)}</h1>
<p class="warning"><strong>DEMO ONLY — no real age validation.</strong> This issuer sets
<code>over_21: true</code> for every authorized test issuance. Never use this credential for an age-restricted service.</p>
${content}
</main></body></html>`;
}

export function rootPage() {
  return page(`<p>This test issuer provides signed, wallet-key-bound demo credentials
for compatible OpenID4VCI wallets. The demo claim is selectively disclosable and expires after 14 days.</p>
<p>Issuance requires an administrator-created, private, single-use offer.
Ask the demo operator for an offer QR code. There is no public offer creation on this page.</p>
<p class="muted">No real identity or age checks are performed. Phone acceptance is
not verified until an offer is scanned and confirmed in the wallet.</p>`);
}

export async function offerPage(link, expiresAt) {
  const qr = await QRCode.toDataURL(link, { width: 320, margin: 4, errorCorrectionLevel: 'M' });
  return page(`
<p>Scan this QR in an OpenID4VCI wallet supporting SD-JWT VC, or open the wallet link on your phone.
The credential will be signed for your wallet's key, not a software test holder.</p>
<img class="qr" src="${escapeHtml(qr)}" alt="QR code for this single-use demo credential offer">
<a class="button" href="${escapeHtml(link)}">Open in wallet</a>
<p class="muted">Offer expires at ${escapeHtml(new Date(expiresAt * 1000).toISOString())}.
The credential expires after 14 days. Keep this page private: its link grants one test issuance.</p>
<p class="muted">Phone issuance is not verified until you scan and confirm it in the wallet.</p>
`);
}

export async function presentationPage(link, issuer, expiresAt) {
  const qr = await QRCode.toDataURL(link, { width: 320, margin: 4, errorCorrectionLevel: 'M' });
  return page(`
<p>Scan with a compatible OpenID4VP wallet, select the credential issued by ${escapeHtml(issuer)},
and approve disclosure of <code>over_21</code>. No birth date is requested.</p>
<img class="qr" alt="Wallet presentation QR code" src="${escapeHtml(qr)}">
<a class="button" href="${escapeHtml(link)}">Open in wallet</a>
<p>After approving in your wallet, return to the buyer and ask it to continue the pending purchase.</p>
<p class="muted">Request expires at ${escapeHtml(new Date(expiresAt * 1000).toISOString())}.
Keep this page private. This authorizes only a simulated purchase.</p>
`, 'Authorize the AgentCon purchase');
}
