import { createHash, createPrivateKey, createPublicKey, randomBytes, sign, verify } from 'node:crypto';
import { mkdir, readFile, writeFile } from 'node:fs/promises';
import { isAbsolute, relative, resolve, sep } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { exportJWK, generateKeyPair } from 'jose';
import { SDJwtVcInstance } from '@sd-jwt/sd-jwt-vc';

const configUrl = new URL('./age-credential.json', import.meta.url);
const repoRoot = fileURLToPath(new URL('../../', import.meta.url));

export async function createKey() {
  const { privateKey } = await generateKeyPair('ES256', { extractable: true });
  const privateJwk = await exportJWK(privateKey);
  const { d, ...publicJwk } = privateJwk;
  const did = `did:jwk:${Buffer.from(JSON.stringify(publicJwk)).toString('base64url')}`;
  publicJwk.kid = `${did}#0`;
  privateJwk.kid = publicJwk.kid;
  return { privateJwk, publicJwk };
}

export function credentialService(issuer) {
  const publicKey = createPublicKey({ key: issuer.publicJwk, format: 'jwk' });
  return new SDJwtVcInstance({
    signAlg: 'ES256',
    hashAlg: 'sha-256',
    hasher: (data, algorithm) => {
      if (algorithm !== 'sha-256') throw new Error(`Unsupported hash algorithm: ${algorithm}`);
      return createHash('sha256').update(typeof data === 'string' ? data : Buffer.from(data)).digest();
    },
    saltGenerator: (length) => randomBytes(length).toString('base64url'),
    signer: issuer.privateJwk
      ? (data) => sign('sha256', Buffer.from(data), {
        key: createPrivateKey({ key: issuer.privateJwk, format: 'jwk' }),
        dsaEncoding: 'ieee-p1363',
      }).toString('base64url')
      : undefined,
    verifier: (data, signature) => {
      const header = JSON.parse(Buffer.from(data.split('.')[0], 'base64url').toString());
      if (header.alg !== 'ES256' || header.kid !== issuer.publicJwk.kid) {
        throw new Error('Unexpected signing algorithm or issuer key ID');
      }
      return verify('sha256', Buffer.from(data), {
        key: publicKey,
        dsaEncoding: 'ieee-p1363',
      }, Buffer.from(signature, 'base64url'));
    },
  });
}

export async function issueCredential(issuer, holderPublicJwk, config, now = Math.floor(Date.now() / 1000)) {
  if (holderPublicJwk.kty !== 'EC' || holderPublicJwk.crv !== 'P-256' ||
      typeof holderPublicJwk.x !== 'string' || typeof holderPublicJwk.y !== 'string' ||
      holderPublicJwk.d !== undefined) {
    throw new Error('Expected a public P-256 holder key, never a private key');
  }
  if (config.demo_only !== true || config.over_21 !== true ||
      typeof config.vct !== 'string' || !config.vct.startsWith('urn:agentcon:demo:') ||
      typeof config.title !== 'string' || !config.title.startsWith('AgentCon Demo') ||
      !Number.isSafeInteger(config.validity_seconds) || config.validity_seconds <= 0 ||
      config.validity_seconds > 30 * 24 * 60 * 60) {
    throw new Error('Expected a labelled demo age credential with a positive validity of at most 30 days');
  }
  const service = credentialService(issuer);
  const iss = issuer.publicJwk.kid.split('#')[0];
  const credential = await service.issue({
    iss,
    iat: now,
    exp: now + config.validity_seconds,
    vct: config.vct,
    title: config.title,
    demo_only: true,
    cnf: { jwk: holderPublicJwk },
    over_21: true,
  }, { _sd: ['over_21'] }, { header: { kid: issuer.publicJwk.kid } });
  return { credential, iss };
}

export async function provision(stateDirectory) {
  if (!stateDirectory || !isAbsolute(stateDirectory)) {
    throw new Error('Set AGENTCON_CREDENTIAL_STATE_DIR to an absolute directory outside the repository');
  }
  const statePath = resolve(stateDirectory);
  const fromRepo = relative(repoRoot, statePath);
  if (!fromRepo || (!isAbsolute(fromRepo) && fromRepo !== '..' && !fromRepo.startsWith(`..${sep}`))) {
    throw new Error('Credential state must be outside the repository');
  }
  await mkdir(statePath, { recursive: true, mode: 0o700 });
  const issuer = await createKey();
  const holder = await createKey();
  const config = JSON.parse(await readFile(configUrl, 'utf8'));
  const { credential, iss } = await issueCredential(issuer, holder.publicJwk, config);
  const result = await credentialService({ publicJwk: issuer.publicJwk }).verify(credential, {
    expectedIssuer: iss,
    expectedVct: config.vct,
    requiredClaimKeys: ['over_21', 'demo_only', 'cnf'],
  });
  if (result.payload.over_21 !== true || result.payload.demo_only !== true) {
    throw new Error('Credential verification did not recover the expected demo age claims');
  }
  const files = {
    'issuer-private.jwk.json': issuer.privateJwk,
    'test-holder-private.jwk.json': holder.privateJwk,
    'issuer-public.jwks.json': { keys: [issuer.publicJwk] },
    'test-holder-public.jwk.json': holder.publicJwk,
    'credential-summary.json': {
      ...result.payload,
      warning: 'DEMO ONLY. Bound to a local test holder, not the phone wallet. No real age verification.',
    },
    'age-credential.sd-jwt': credential,
  };
  // Reserve a new run directory: never overwrite or rotate previously issued keys.
  const runPath = resolve(statePath, `issuance-${Date.now()}-${randomBytes(6).toString('hex')}`);
  await mkdir(runPath, { mode: 0o700 });
  for (const [name, contents] of Object.entries(files)) {
    await writeFile(resolve(runPath, name),
      typeof contents === 'string' ? `${contents}\n` : `${JSON.stringify(contents, null, 2)}\n`,
      { flag: 'wx', mode: 0o600 });
  }
  return runPath;
}

if (process.argv[1] && pathToFileURL(resolve(process.argv[1])).href === import.meta.url) {
  try {
    const runPath = await provision(process.env.AGENTCON_CREDENTIAL_STATE_DIR);
    console.log(`Signed and verified DEMO credential. Files: ${runPath}`);
    console.log('Private keys: issuer-private.jwk.json and test-holder-private.jwk.json');
    console.log('Not issued to a phone wallet. Do not use for actual age verification.');
  } catch (error) {
    console.error(`Credential issuance failed: ${error.message}`);
    process.exitCode = 1;
  }
}
