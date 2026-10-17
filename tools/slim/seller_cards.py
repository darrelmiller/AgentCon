"""Agent Cards for the sellers that run in the cluster, to publish on the did:web host next to their did.json.

Each card comes from the supplier binary itself (`agentcon-supplier publish-card`), so it matches what the seller
serves. Each seller's origin is BASE_URL/<seller>: the card lists its JSON-RPC interface there (`/a2a`, behind the
OIDC gateway) and its SLIMRPC interface, requires an OIDC bearer token, and links the schema published next to it.

Usage: seller_cards.py SUPPLIER_BIN SITE_DIR [BASE_URL] [ORG_NS] [OIDC_DISCOVERY_URL]
  Defaults: https://agents.slim.outshift.io/agentcon, agntcy/agentcon,
  https://dev.idp.ads.outshift.io/.well-known/openid-configuration.
  Writes SITE_DIR/<path of BASE_URL>/<seller>/agent-card.json and .../<seller>/schemas/wine-v1.schema.json.
"""

import json
import os
import shutil
import subprocess
import sys
import urllib.parse

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
SELLERS = {  # SLIM app name -> inventory
    "barolo-supplier": "barolo.json",
    "sardinian-supplier": "sardinia.json",
    "french-supplier": "france.json",
}


def main(binary: str, site: str, base_url: str, org_ns: str, oidc: str) -> None:
    base_url = base_url.rstrip("/")
    path = urllib.parse.urlparse(base_url).path.strip("/")
    out = os.path.join(site, *[segment for segment in path.split("/") if segment])
    schema_source = os.path.join(ROOT, "SupplierAgent", "schemas", "wine-v1.schema.json")
    for seller, inventory in SELLERS.items():
        env = dict(os.environ, SUPPLIER_INVENTORY=os.path.join(ROOT, "SupplierAgent", "inventories", inventory))
        card = subprocess.run([binary, "publish-card", f"{base_url}/{seller}", f"slim://{org_ns}/{seller}",
                               "--http", "--oidc", oidc], env=env, check=True, capture_output=True, text=True).stdout
        target = os.path.join(out, seller, "agent-card.json")
        os.makedirs(os.path.join(out, seller, "schemas"), exist_ok=True)
        with open(target, "w") as file:
            json.dump(json.loads(card), file, indent=2, ensure_ascii=False)
            file.write("\n")
        shutil.copyfile(schema_source, os.path.join(out, seller, "schemas", "wine-v1.schema.json"))
        print(target)


if __name__ == "__main__":
    if len(sys.argv) < 3:
        sys.exit(__doc__)
    args = sys.argv[3:] + [None] * 3
    main(sys.argv[1], sys.argv[2],
         args[0] or "https://agents.slim.outshift.io/agentcon",
         args[1] or "agntcy/agentcon",
         args[2] or "https://dev.idp.ads.outshift.io/.well-known/openid-configuration")
