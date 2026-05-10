#!/usr/bin/env python3
"""
Sprint 25.2 — auto-generate stable instruction IDs for the 8086 spec.

Strategy: existing format `name` already encodes operand shape in CamelCase
(e.g. "AddRm8R8", "OrAlImm8", "ImulRm16"). For each instruction we
generate ID = "<MNEMONIC>_<format_name_camel_to_snake>". Format with
multiple instructions distinguished by `selector` get the selector value
appended.

Usage:
    python tools/spec_id_audit.py [--apply]

Without --apply: just dumps proposed IDs + collisions to stdout.
With --apply:    writes IDs back into the JSON files in-place.
"""
import json
import re
import sys
from collections import Counter
from pathlib import Path

SPEC_DIR = Path("spec/cpu/x86-16/i8086/groups")

def camel_to_snake(name: str) -> str:
    # "AddRm8R8" -> "add_rm8_r8"; "AddRmW16Imm16" -> "add_rm_w16_imm16"
    s = re.sub(r"([A-Z]+)([A-Z][a-z])", r"\1_\2", name)
    s = re.sub(r"([a-z0-9])([A-Z])", r"\1_\2", s)
    return s.lower()

def make_id(mnemonic: str, format_name: str, selector_value: str | None) -> str:
    # ADD + add_rm8_r8 -> ADD_rm8_r8 (drop the redundant mnemonic prefix from the format snake)
    snake = camel_to_snake(format_name)
    mnemonic_lower = mnemonic.lower()

    # If the snake starts with the mnemonic_lower, strip it.
    parts = snake.split("_")
    while parts and parts[0] == mnemonic_lower:
        parts.pop(0)
    operand_shape = "_".join(parts) if parts else "noargs"

    base = f"{mnemonic.upper()}_{operand_shape}"
    if selector_value is not None:
        # Strip "0b" or "0x" prefix from selector value, keep the bits.
        sv = selector_value.replace("0b", "").replace("0x", "")
        base = f"{base}_sel{sv}"
    return base

def collect_proposals():
    proposals = []  # list of (file_path, group_idx, format_idx, instr_idx, mnemonic, format_name, selector_value, proposed_id)
    for path in sorted(SPEC_DIR.glob("*.json")):
        with open(path, encoding="utf-8") as f:
            doc = json.load(f)
        formats = doc.get("formats", [])
        for fi, fmt in enumerate(formats):
            fname = fmt.get("name", f"_anon_{fi}")
            instrs = fmt.get("instructions", [])
            for ii, instr in enumerate(instrs):
                mnemonic = instr.get("mnemonic", "?")
                sel = instr.get("selector", {})
                sel_value = sel.get("value") if isinstance(sel, dict) and sel else None
                prop_id = make_id(mnemonic, fname, sel_value)
                existing_id = instr.get("id")
                proposals.append({
                    "file": str(path),
                    "fi": fi,
                    "ii": ii,
                    "mnemonic": mnemonic,
                    "format": fname,
                    "selector": sel_value,
                    "id": prop_id,
                    "existing_id": existing_id,
                })
    return proposals

def detect_collisions(proposals):
    counts = Counter(p["id"] for p in proposals)
    return {k: v for k, v in counts.items() if v > 1}

def apply_proposals(proposals):
    """Write proposed IDs back into JSON files (in-place, deterministic order)."""
    by_file = {}
    for p in proposals:
        by_file.setdefault(p["file"], []).append(p)

    for path, ps in by_file.items():
        with open(path, encoding="utf-8") as f:
            doc = json.load(f)
        for p in ps:
            instr = doc["formats"][p["fi"]]["instructions"][p["ii"]]
            instr["id"] = p["id"]
        with open(path, "w", encoding="utf-8") as f:
            json.dump(doc, f, indent=2, ensure_ascii=False)
            f.write("\n")
        print(f"  wrote {path}")

def main():
    apply = "--apply" in sys.argv

    proposals = collect_proposals()
    collisions = detect_collisions(proposals)

    print(f"Total instructions: {len(proposals)}")
    print(f"Unique IDs: {len(set(p['id'] for p in proposals))}")

    if collisions:
        print(f"\n=== COLLISIONS ({len(collisions)} IDs) ===")
        for cid, n in sorted(collisions.items()):
            print(f"  {cid}  ({n} instructions):")
            for p in proposals:
                if p["id"] == cid:
                    print(f"    {p['file']} format={p['format']} mnemonic={p['mnemonic']} selector={p['selector']}")

    if not apply:
        print("\n=== Sample proposals (first 30) ===")
        for p in proposals[:30]:
            print(f"  {p['mnemonic']:8s} {p['format']:30s} -> {p['id']}")
        print(f"\nTo apply: python tools/spec_id_audit.py --apply")
        return 0

    if collisions:
        print("\nABORTING: cannot apply with collisions present.")
        return 1

    print("\n=== Applying ===")
    apply_proposals(proposals)
    return 0

if __name__ == "__main__":
    sys.exit(main())
