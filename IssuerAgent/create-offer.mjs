import { isMain } from './config.mjs';

export async function createOffer(env = process.env, request = fetch) {
  let origin;
  try {
    const url = new URL(env.ISSUER_BASE_URL);
    if (url.protocol !== 'https:' || url.pathname !== '/' || url.username ||
        url.password || url.search || url.hash) throw new Error();
    origin = url.origin;
  } catch {
    throw new Error('ISSUER_BASE_URL must be an HTTPS origin');
  }
  if (!/^[A-Za-z0-9_-]{32,256}$/.test(env.ISSUER_ADMIN_TOKEN ?? '')) {
    throw new Error('ISSUER_ADMIN_TOKEN must be 32–256 base64url characters');
  }
  let response;
  try {
    response = await request(`${origin}/admin/offers`, {
      method: 'POST',
      redirect: 'error',
      signal: AbortSignal.timeout(15_000),
      headers: {
        Authorization: `Bearer ${env.ISSUER_ADMIN_TOKEN}`,
        'Content-Type': 'application/json',
        Accept: 'application/json',
      },
      body: '{}',
    });
  } catch {
    throw new Error('Offer request failed; check the HTTPS issuer connection');
  }
  if (response.status !== 201) {
    throw new Error(`Offer creation failed (HTTP ${response.status}); check authorization and issuer availability`);
  }
  try {
    const result = await response.json();
    const page = new URL(result.offer_page);
    if (page.origin !== origin || !/^\/offers\/[A-Za-z0-9_-]{43}$/.test(page.pathname) ||
        page.username || page.password || page.search || page.hash) throw new Error();
    return page.href;
  } catch {
    throw new Error('Issuer returned an invalid offer page');
  }
}

if (isMain(import.meta.url)) {
  try {
    // The page URL is a one-use capability. Never print the admin token or raw offer.
    console.log(await createOffer());
  } catch (error) {
    console.error(error.message);
    process.exitCode = 1;
  }
}
