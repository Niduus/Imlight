"""Signed Hive Mind file (Niduus, 2026-11-22: "All the bots MUST run off a signed Hive Mind file ... This isn't to be used on
live realm, only on Ahziael").

Builds hive-mind.ahzhive from a folder laid out like the server's logs folder (bots/, ultimus/, ...): only the learned-brain
files below go in. The file is a zip with:
  manifest.json  what it is, who made it, when, and the SHA-256 of every brain file
  signature.bin  an ECDSA P-256 / SHA-256 signature (DER) over the exact bytes of manifest.json
  brain/<path>   the brain files themselves
The bots check it with the public key built into them (bots/BotShared/BotHive.cs). The private key is Niduus's and is never
put in any repository: it lives in the project's private files (.keys/hive-signing-key.pem).

  python3 tools/hive-sign/sign.py --source <logs-like folder> --key <private key .pem> --out hive-mind.ahzhive
  python3 tools/hive-sign/sign.py --repo --source <ahziael-bots checkout> --key <private key .pem> --out hive-mind.ahzhive
  python3 tools/hive-sign/sign.py --verify hive-mind.ahzhive --public <public key .pem>
"""

import argparse
import hashlib
import json
import os
import sys
import zipfile
from datetime import datetime, timezone

from cryptography.exceptions import InvalidSignature
from cryptography.hazmat.primitives import hashes, serialization
from cryptography.hazmat.primitives.asymmetric import ec

FORMAT = "ahziael-hive-mind/1"
PURPOSE = ("The Hive Mind of the Ahziael (Imlight) bots, signed by Niduus. For bots on an Imlight server only; "
           "not for the official game.")

# The learned brain every questing bot shares (paths relative to the server's logs folder).
BRAIN = [
    "bots/knowledge.json", "bots/mistakes.json", "bots/stuck-patterns.json", "bots/training-experience.json",
    "bots/use-guards.json", "bots/bot-puzzles.json",
    "ultimus/fight-styles.json", "ultimus/step-memory.json", "ultimus/stuck-solutions.json", "ultimus/quest-atlas.json",
    "ultimus/quest-tree.json", "ultimus/npc-book.json", "ultimus/zone-transfers.json", "ultimus/pads.json",
    "ultimus/deck-mix.json", "ultimus/learned.jsonl", "ultimus/foes.json", "ultimus/drop-memory.json",
    "ultimus/abnormal-fixes.json", "ultimus/guided-runs.json", "ultimus/playbook.txt",
]


def sha256(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


# Niduus/ahziael-bots keeps the same files elsewhere: shared/now = logs/bots, bots/ultimus/now = logs/ultimus.
REPO_FOLDERS = {"bots": "shared/now", "ultimus": "bots/ultimus/now"}


def locate(source: str, rel: str, repo: bool) -> str:
    top, rest = rel.split("/", 1)
    if repo:
        return os.path.join(source, *REPO_FOLDERS[top].split("/"), *rest.split("/"))
    return os.path.join(source, top, *rest.split("/"))


def build(source: str, key_path: str, out: str, repo: bool = False) -> int:
    with open(key_path, "rb") as f:
        key = serialization.load_pem_private_key(f.read(), password=None)

    files, blobs = [], {}
    for rel in BRAIN:
        path = locate(source, rel, repo)
        if not os.path.isfile(path):
            continue
        with open(path, "rb") as f:
            data = f.read()
        blobs[rel] = data
        files.append({"path": rel, "size": len(data), "sha256": sha256(data)})

    if not files:
        print(f"No brain files found under {source}.", file=sys.stderr)
        return 1

    manifest = json.dumps({
        "format": FORMAT,
        "purpose": PURPOSE,
        "by": "Niduus",
        "made": datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
        "files": files,
    }, indent=1).encode("utf-8")
    signature = key.sign(manifest, ec.ECDSA(hashes.SHA256()))

    tmp = out + ".tmp"
    with zipfile.ZipFile(tmp, "w", zipfile.ZIP_DEFLATED) as z:
        z.writestr("manifest.json", manifest)
        z.writestr("signature.bin", signature)
        for rel, data in blobs.items():
            z.writestr("brain/" + rel, data)
    os.replace(tmp, out)
    print(f"Signed {len(files)} brain file(s) into {out}.")
    return 0


def verify(pack: str, public_path: str) -> int:
    with open(public_path, "rb") as f:
        public = serialization.load_pem_public_key(f.read())
    with zipfile.ZipFile(pack) as z:
        manifest = z.read("manifest.json")
        signature = z.read("signature.bin")
        try:
            public.verify(signature, manifest, ec.ECDSA(hashes.SHA256()))
        except InvalidSignature:
            print("NOT VALID: the signature does not match.")
            return 1
        info = json.loads(manifest)
        if info.get("format") != FORMAT:
            print("NOT VALID: unknown format.")
            return 1
        for entry in info["files"]:
            if sha256(z.read("brain/" + entry["path"])) != entry["sha256"]:
                print(f"NOT VALID: {entry['path']} was changed after signing.")
                return 1
    print(f"Valid: signed by {info['by']} at {info['made']}, {len(info['files'])} brain file(s).")
    return 0


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--source")
    parser.add_argument("--key")
    parser.add_argument("--out", default="hive-mind.ahzhive")
    parser.add_argument("--repo", action="store_true", help="--source is a Niduus/ahziael-bots checkout")
    parser.add_argument("--verify")
    parser.add_argument("--public")
    args = parser.parse_args()
    if args.verify:
        sys.exit(verify(args.verify, args.public))
    if not args.source or not args.key:
        parser.error("--source and --key are needed to sign")
    sys.exit(build(args.source, args.key, args.out, args.repo))
