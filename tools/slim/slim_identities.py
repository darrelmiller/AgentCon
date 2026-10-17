"""Per-agent SLIM identities for the AgentCon demo: one Ed25519 key per agent, named by a DID.

Each agent signs its SLIM (MLS) credential as an EdDSA JWT whose `sub` (and `kid`) is its DID; every
agent verifies peers against one JWKS of all demo agents, so only listed keys are accepted.

Two kinds of DID:
  did:key (default)  self-certifying: the DID is the public key and `iss = sub = DID`. Trust comes only
                     from the JWKS file being shared.
  did:web            anchored on a domain: did:web:<host>:<path>:<agent> resolves to
                     https://<host>/<path>/<agent>/did.json. Every agent uses `iss = https://<host>`
                     (or --issuer), and the domain publishes the JWKS, so whoever controls the domain
                     says which key belongs to which agent.

Usage: slim_identities.py [--did-web HOST[/PATH]] [--issuer URL] [--verifier jwks|autoresolve]
                          OUT_DIR CLIENT_JSON ORG_NS AGENT [AGENT ...]
  Writes, under OUT_DIR (keys are created once and reused):
    keys/<agent>.pem     Ed25519 private key (PKCS#8 PEM, mode 0600)
    <agent>.json         SLIM config: {"client": CLIENT_JSON, "app": {name, identity_provider, identity_verifier}}
    trust.jwks.json      did:key only: public keys of all agents, kid = DID
    site/                did:web only: the files to publish at the issuer (public keys only):
                           .well-known/openid-configuration, .well-known/jwks.json, <path>/<agent>/did.json
  Prints "<agent> <DID>" per agent.

  --verifier (did:web only):
    jwks         (default) verify against the JWKS file in site/, with subject_binding enforced: a token's
                 `sub` must be the DID of the key that signed it. Publish the same file at the issuer.
    autoresolve  fetch keys from the issuer's discovery document. SLIM does not apply subject_binding to
                 fetched keys, so any listed key could claim another agent's DID.
"""

import argparse
import base64
import json
import os
import re
import subprocess
import urllib.parse

B58 = "123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz"
AUDIENCE = "agntcy/agentcon"
# Agent names and did:web path segments become file and URL path components: no '.', '..' or separators.
NAME = re.compile(r"^[A-Za-z0-9][A-Za-z0-9._-]*$")
HOST = re.compile(r"^[A-Za-z0-9]([A-Za-z0-9.-]*[A-Za-z0-9])?(:[0-9]{1,5})?$")


def base58btc(data: bytes) -> str:
    number = int.from_bytes(data, "big")
    encoded = ""
    while number:
        number, rem = divmod(number, 58)
        encoded = B58[rem] + encoded
    return "1" * (len(data) - len(data.lstrip(b"\0"))) + encoded


def b64url(data: bytes) -> str:
    return base64.urlsafe_b64encode(data).rstrip(b"=").decode()


def ed25519_public_key(pem_path: str) -> bytes:
    der = subprocess.run(["openssl", "pkey", "-in", pem_path, "-pubout", "-outform", "DER"],
                         check=True, capture_output=True).stdout
    return der[-32:]  # SubjectPublicKeyInfo ends with the 32-byte raw key


def did_web_base(location: str) -> tuple[str, list[str], str]:
    """Split HOST[:PORT][/PATH] into (host, path segments, DID prefix) per the did:web method."""
    host, _, path = location.strip("/").partition("/")
    segments = [segment for segment in path.split("/") if segment]
    if not HOST.match(host) or not all(NAME.match(segment) for segment in segments):
        raise SystemExit(f"--did-web {location!r}: expected HOST[:PORT][/PATH] with plain path segments")
    # A port is percent-encoded in the method-specific id; path segments are joined with ':'.
    prefix = "did:web:" + ":".join([urllib.parse.quote(host, safe=""), *segments])
    return host, segments, prefix


def jwk(raw: bytes, did: str) -> dict:
    return {"kty": "OKP", "crv": "Ed25519", "x": b64url(raw), "kid": did, "alg": "EdDSA", "use": "sig"}


def write_json(path: str, value: object, mode: int = 0o600) -> None:
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "w") as file:
        json.dump(value, file, indent=2)
        file.write("\n")
    os.chmod(path, mode)


def did_document(did: str, raw: bytes) -> dict:
    method = f"{did}#key-1"
    return {
        "@context": ["https://www.w3.org/ns/did/v1", "https://w3id.org/security/suites/jws-2020/v1"],
        "id": did,
        "verificationMethod": [{"id": method, "type": "JsonWebKey2020", "controller": did,
                                "publicKeyJwk": {"kty": "OKP", "crv": "Ed25519", "x": b64url(raw)}}],
        "authentication": [method],
        "assertionMethod": [method],
    }


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--did-web", metavar="HOST[/PATH]", help="name agents with did:web under this location")
    parser.add_argument("--issuer", help="did:web: JWT issuer and site URL (default https://HOST)")
    parser.add_argument("--verifier", choices=["jwks", "autoresolve"], default="jwks", help="did:web: how peers are verified")
    parser.add_argument("out_dir")
    parser.add_argument("client_json")
    parser.add_argument("org_ns")
    parser.add_argument("agents", nargs="+")
    args = parser.parse_args()
    if (args.issuer or args.verifier != "jwks") and not args.did_web:
        parser.error("--issuer and --verifier apply only with --did-web")
    if bad := [agent for agent in args.agents if not NAME.match(agent)]:
        parser.error(f"invalid agent names: {bad}")

    os.makedirs(os.path.join(args.out_dir, "keys"), mode=0o700, exist_ok=True)
    os.umask(0o077)

    issuer, segments, prefix = None, [], None
    if args.did_web:
        host, segments, prefix = did_web_base(args.did_web)
        issuer = (args.issuer or f"https://{host}").rstrip("/")

    identities = {}
    for agent in args.agents:
        pem = os.path.join(args.out_dir, "keys", f"{agent}.pem")
        if not os.path.exists(pem):
            subprocess.run(["openssl", "genpkey", "-algorithm", "ed25519", "-out", pem], check=True)
        raw = ed25519_public_key(pem)
        # did:key: multibase 'z' + base58btc(multicodec ed25519-pub 0xed01 + key)
        did = f"{prefix}:{agent}" if prefix else "did:key:z" + base58btc(b"\xed\x01" + raw)
        identities[agent] = (pem, raw, did)
    keys = {"keys": [jwk(raw, did) for _, raw, did in identities.values()]}

    if issuer:
        # Public files, served as-is at the issuer: world-readable, no private material.
        site = os.path.join(args.out_dir, "site")
        jwks_file = os.path.join(site, ".well-known", "jwks.json")
        write_json(jwks_file, keys, 0o644)
        write_json(os.path.join(site, ".well-known", "openid-configuration"),
                   {"issuer": issuer, "jwks_uri": f"{issuer}/.well-known/jwks.json"}, 0o644)
        for agent, (_, raw, did) in identities.items():
            write_json(os.path.join(site, *segments, agent, "did.json"), did_document(did, raw), 0o644)
        for directory, _, _ in os.walk(site):
            os.chmod(directory, 0o755)
        verifier = {"type": "jwt", "claims": {"issuer": issuer, "audience": [AUDIENCE]}, "duration": "1h"}
        if args.verifier == "jwks":
            verifier |= {"key": {"type": "decoding", "algorithm": "EdDSA", "format": "jwks", "key": {"file": jwks_file}},
                         "subject_binding": "enforce"}
        else:
            verifier |= {"key": {"type": "autoresolve"}}
    else:
        trust = os.path.join(args.out_dir, "trust.jwks.json")
        write_json(trust, keys)
        verifier = {"type": "jwt", "claims": {"audience": [AUDIENCE]}, "duration": "1h",
                    "key": {"type": "decoding", "algorithm": "EdDSA", "format": "jwks", "key": {"file": trust}}}

    for agent, (pem, _, did) in identities.items():
        # did:key is self-issued (iss = sub = DID; SLIM requires iss). did:web is issued under the domain.
        claims = {"issuer": issuer or did, "subject": did, "audience": [AUDIENCE]}
        provider = {"type": "jwt", "claims": claims, "duration": "1h",
                    "key": {"type": "encoding", "algorithm": "EdDSA", "format": "pem", "key": {"file": pem}}}
        write_json(os.path.join(args.out_dir, f"{agent}.json"),
                   {"client": json.loads(args.client_json),
                    "app": {"name": f"{args.org_ns}/{agent}", "identity_provider": provider, "identity_verifier": verifier}})
        print(agent, did)


if __name__ == "__main__":
    main()
