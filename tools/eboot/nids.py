"""PS4 NID (name identifier) computation and the verified NID->name table.

PS4 modules import functions by NID, not by name:

    digest = SHA1(name_bytes + SUFFIX)          SUFFIX = 51 8D 64 A6 35 DE D8 C1
                                                         E6 B0 39 B1 C3 E5 52 30
    nid    = base64(reverse(digest[0:8]))[:11], with '/' replaced by '-'

(verified: malloc -> gQX+4GDQjpM, free -> tIhsqj0qsFE, sceAvPlayerInit -> aS66RI0gGgo,
matching the names shadPS4 prints for the same NIDs.)

The hash is 64 bits, so a candidate name whose NID equals an imported NID is a
confirmed resolution (no false positives in practice).  `nid_db.tsv` next to this
file holds only names verified that way against CUSA47999's eboot.bin.  Names that
could not be recovered are reported as "?<nid>".
"""
from __future__ import annotations

import base64
import hashlib
import re
import sys
from pathlib import Path
from typing import Dict, Iterable, List, Set

SUFFIX = bytes.fromhex("518D64A635DED8C1E6B039B1C3E55230")
DB_PATH = Path(__file__).with_name("nid_db.tsv")


def nid(name: str) -> str:
    """NID of a symbol name (11 characters)."""
    h = hashlib.sha1(name.encode("utf-8") + SUFFIX).digest()
    return base64.b64encode(h[:8][::-1]).decode("ascii")[:11].replace("/", "-")


def load_db(path: Path = DB_PATH) -> Dict[str, str]:
    """NID -> name from the verified table (tab separated: nid, library, name)."""
    out: Dict[str, str] = {}
    if not path.is_file():
        return out
    for line in path.read_text(encoding="utf-8").splitlines():
        if not line or line.startswith("#"):
            continue
        parts = line.split("\t")
        if len(parts) < 3:
            raise ValueError(f"{path}: bad line {line!r}")
        n, name = parts[0], parts[2]
        if nid(name) != n:
            raise ValueError(f"{path}: {name} does not hash to {n}")
        out[n] = name
    return out


_TOKEN = re.compile(r"[A-Za-z_][A-Za-z0-9_]*")


def candidates_from_text(text: str) -> Set[str]:
    """Identifier-like tokens from free text (candidate list files, source dumps)."""
    return set(_TOKEN.findall(text))


def resolve(targets: Iterable[str], cands: Iterable[str]) -> Dict[str, str]:
    """Match candidate names against target NIDs; returns nid -> name."""
    want = set(targets)
    out: Dict[str, str] = {}
    for c in cands:
        n = nid(c)
        if n in want and n not in out:
            out[n] = c
    return out


def _main(argv: List[str]) -> int:
    import argparse

    ap = argparse.ArgumentParser(description="Compute NIDs or resolve names")
    ap.add_argument("names", nargs="*", help="symbol names to hash")
    a = ap.parse_args(argv)
    for n in a.names:
        print(f"{nid(n)}\t{n}")
    return 0


if __name__ == "__main__":
    sys.exit(_main(sys.argv[1:]))
