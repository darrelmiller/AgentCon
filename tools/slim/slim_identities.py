"""Per-agent SLIM identities for the AgentCon demo: one Ed25519 key and did:key per agent.

Each agent signs its SLIM (MLS) credential as a self-issued EdDSA JWT whose `iss`, `sub` (and
`kid`) are its did:key; every agent verifies peers against one JWKS of all demo agents (the trust list), so
only listed keys are accepted and every credential names the agent that signed it.

Usage: slim_identities.py OUT_DIR CLIENT_JSON ORG_NS AGENT [AGENT ...]
  Writes, under OUT_DIR (keys are created once and reused):
    keys/<agent>.pem     Ed25519 private key (PKCS#8 PEM, mode 0600)
    trust.jwks.json      public keys of all agents, kid = did:key
    <agent>.json         SLIM config: {"client": CLIENT_JSON, "app": {name, identity_provider, identity_verifier}}
  Prints "<agent> <did:key>" per agent.
"""

import base64
import json
import os
import subprocess
import sys

B58 = "123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz"
AUDIENCE = "agntcy/agentcon"


def base58btc(data: bytes) -> str:
    number = int.from_bytes(data, "big")
    encoded = ""
    while number:
        number, rem = divmod(number, 58)
        encoded = B58[rem] + encoded
    return "1" * (len(data) - len(data.lstrip(b"\0"))) + encoded


def ed25519_public_key(pem_path: str) -> bytes:
    der = subprocess.run(["openssl", "pkey", "-in", pem_path, "-pubout", "-outform", "DER"],
                         check=True, capture_output=True).stdout
    return der[-32:]  # SubjectPublicKeyInfo ends with the 32-byte raw key


def main(out_dir: str, client_json: str, org_ns: str, agents: list[str]) -> None:
    os.makedirs(os.path.join(out_dir, "keys"), mode=0o700, exist_ok=True)
    os.umask(0o077)
    identities = {}
    for agent in agents:
        pem = os.path.join(out_dir, "keys", f"{agent}.pem")
        if not os.path.exists(pem):
            subprocess.run(["openssl", "genpkey", "-algorithm", "ed25519", "-out", pem], check=True)
        raw = ed25519_public_key(pem)
        # did:key: multibase 'z' + base58btc(multicodec ed25519-pub 0xed01 + key)
        identities[agent] = (pem, raw, "did:key:z" + base58btc(b"\xed\x01" + raw))

    trust = os.path.join(out_dir, "trust.jwks.json")
    with open(trust, "w") as file:
        json.dump({"keys": [
            {"kty": "OKP", "crv": "Ed25519", "x": base64.urlsafe_b64encode(raw).rstrip(b"=").decode(),
             "kid": did, "alg": "EdDSA", "use": "sig"}
            for _, raw, did in identities.values()]}, file, indent=2)

    verifier = {"type": "jwt", "claims": {"audience": [AUDIENCE]}, "duration": "1h",
                "key": {"type": "decoding", "algorithm": "EdDSA", "format": "jwks", "key": {"file": trust}}}
    for agent, (pem, _, did) in identities.items():
        # Self-issued: iss = sub = the agent's did:key (SLIM requires iss); trust comes from the JWKS.
        provider = {"type": "jwt", "claims": {"issuer": did, "subject": did, "audience": [AUDIENCE]}, "duration": "1h",
                    "key": {"type": "encoding", "algorithm": "EdDSA", "format": "pem", "key": {"file": pem}}}
        with open(os.path.join(out_dir, f"{agent}.json"), "w") as file:
            json.dump({"client": json.loads(client_json),
                       "app": {"name": f"{org_ns}/{agent}", "identity_provider": provider,
                               "identity_verifier": verifier}}, file, indent=2)
        print(agent, did)


if __name__ == "__main__":
    if len(sys.argv) < 5:
        sys.exit(__doc__)
    main(sys.argv[1], sys.argv[2], sys.argv[3], sys.argv[4:])
