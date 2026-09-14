from __future__ import annotations

import os
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]

# These are repository-governance blockers, not secret-detection signatures.
FORBIDDEN_SUFFIXES = {
    ".pfx", ".p12", ".key", ".pem", ".dmp", ".bak", ".dump", ".sqlite"
}
FORBIDDEN_PATH_PARTS = {
    "private-evidence",
    "customer-data",
    "secrets",
}
FORBIDDEN_PREFIXES = {
    "hr-export",
    "oracle-export",
}

# Allow documentation to mention these words; block only unsafe paths/files.
violations: list[str] = []

for path in ROOT.rglob("*"):
    if not path.is_file():
        continue
    rel = path.relative_to(ROOT)
    parts_lower = {p.lower() for p in rel.parts}
    name_lower = rel.name.lower()

    if ".git" in parts_lower:
        continue

    if parts_lower.intersection(FORBIDDEN_PATH_PARTS):
        violations.append(f"forbidden path: {rel}")

    if rel.suffix.lower() in FORBIDDEN_SUFFIXES:
        violations.append(f"forbidden file type: {rel}")

    if any(name_lower.startswith(prefix) for prefix in FORBIDDEN_PREFIXES):
        violations.append(f"forbidden data export filename: {rel}")

if violations:
    print("EIMS repository policy check: FAIL")
    for item in sorted(set(violations)):
        print(f" - {item}")
    sys.exit(1)

print("EIMS repository policy check: PASS")
print("No prohibited evidence/data file paths were detected.")
