#!/usr/bin/env python3
"""
sts2-cli launcher: start a new game (character, ascension) or load a save, then run python/play.py.
"""
from __future__ import annotations

import argparse
import json
import os
import subprocess
import sys
from datetime import datetime

ROOT = os.path.dirname(os.path.abspath(__file__))
PLAY_PY = os.path.join(ROOT, "python", "play.py")
SAVE_DIR = os.path.join(ROOT, "saves")
LOC_CHARS = os.path.join(ROOT, "localization_eng", "characters.json")

# Names play.py --character accepts, in the game's character-select order
CLI_CHARACTERS = ["Ironclad", "Silent", "Defect", "Regent", "Necrobinder"]


def _load_char_titles() -> dict[str, str]:
    """Official character names (localization_eng/characters.json)."""
    titles: dict[str, str] = {}
    if not os.path.isfile(LOC_CHARS):
        return titles
    with open(LOC_CHARS, encoding="utf-8") as f:
        z = json.load(f)
    for key in ("IRONCLAD", "SILENT", "DEFECT", "REGENT", "NECROBINDER"):
        titles[key] = z.get(f"{key}.title", key)
    return titles


def _char_title(titles: dict[str, str], cli_name: str) -> str:
    return titles.get(cli_name.upper(), cli_name)


def _prompt_line(prompt: str) -> str:
    try:
        return input(prompt).strip()
    except (EOFError, KeyboardInterrupt):
        print()
        raise SystemExit(0) from None


def _pick_int(prompt: str, lo: int, hi: int, default: int | None = None) -> int:
    while True:
        raw = _prompt_line(prompt)
        if not raw and default is not None:
            return default
        try:
            v = int(raw)
        except ValueError:
            print(f"  Enter a whole number from {lo} to {hi}.")
            continue
        if lo <= v <= hi:
            return v
        print(f"  Enter a whole number from {lo} to {hi}.")


def _collect_save_entries() -> list[dict]:
    if not os.path.isdir(SAVE_DIR):
        return []
    out: list[dict] = []
    for name in os.listdir(SAVE_DIR):
        path = os.path.join(SAVE_DIR, name)
        if not os.path.isfile(path):
            continue
        st = os.stat(path)
        if name.endswith(".json"):
            try:
                with open(path, encoding="utf-8") as f:
                    d = json.load(f)
                out.append({
                    "kind": "replay",
                    "path": path,
                    "name": name,
                    "mtime": st.st_mtime,
                    "character": d.get("character", "?"),
                    "seed": d.get("seed", "?"),
                    "actions": len(d.get("actions", [])),
                })
            except (json.JSONDecodeError, OSError):
                pass
        elif name.endswith(".save"):
            try:
                with open(path, encoding="utf-8") as f:
                    data = json.load(f)
                seed = data.get("rng", {}).get("seed", "?")
                asc = data.get("ascension", 0)
                char_id = "?"
                pl = data.get("players", [])
                if pl:
                    char_id = pl[0].get("character_id", "?")
                out.append({
                    "kind": "native",
                    "path": path,
                    "name": name,
                    "mtime": st.st_mtime,
                    "seed": seed,
                    "ascension": asc,
                    "character_id": char_id,
                })
            except (json.JSONDecodeError, OSError):
                out.append({
                    "kind": "native",
                    "path": path,
                    "name": name,
                    "mtime": st.st_mtime,
                    "broken": True,
                })
    out.sort(key=lambda x: -x["mtime"])
    return out


def _format_entry(titles: dict[str, str], e: dict) -> str:
    ts = datetime.fromtimestamp(e["mtime"]).strftime("%Y-%m-%d %H:%M")
    if e["kind"] == "replay":
        ch = str(e.get("character", "?"))
        title = _char_title(titles, ch)
        return f"{e['name']}  |  {title}  |  seed {e['seed']}  |  {e['actions']} actions  |  {ts}"
    if e.get("broken"):
        return f"{e['name']}  |  (file is damaged or unreadable)  |  {ts}"
    cid = str(e.get("character_id", "?"))
    title = titles.get(cid.upper(), cid) if cid != "?" else "?"
    return (
        f"{e['name']}  |  {title}  |  ascension {e['ascension']}  |  seed {e['seed']}  |  {ts}"
    )


def _run_play(args: list[str]) -> int:
    cmd = [sys.executable, PLAY_PY, *args]
    # play.py resolves ROOT from its own path, not the cwd; run from the repo root so relative paths work.
    r = subprocess.run(cmd, cwd=ROOT)
    return r.returncode


def _menu_new_game(titles: dict[str, str]) -> None:
    print("\n── Choose a character ──")
    for i, cli in enumerate(CLI_CHARACTERS, 0):
        title = _char_title(titles, cli)
        print(f"  {i}  {title}  ({cli})")
    idx = _pick_int("\nEnter a number (0–4): ", 0, 4)
    character = CLI_CHARACTERS[idx]
    asc = _pick_int(
        "\nAscension 0–10 (0 is the standard game; press Enter for 0): ",
        0,
        10,
        default=0,
    )
    print(f"\nStarting: {_char_title(titles, character)}  |  ascension {asc}\n")
    _run_play(["--character", character, "--ascension", str(asc)])


def _menu_load_save(titles: dict[str, str]) -> None:
    entries = _collect_save_entries()
    if not entries:
        print("\n  No .save or .json saves in saves/. Save during a run, or choose to save when you quit.\n")
        return

    print("\n── Load a save (newest first) ──")
    print("  [continue] = the game's native .save")
    print("  [replay]   = .json action replay written by the in-game save command\n")
    for i, e in enumerate(entries, 1):
        tag = "continue" if e["kind"] == "native" else "replay"
        print(f"  {i:2}  [{tag}]  {_format_entry(titles, e)}")
    print("\n  0  Back")
    choice = _pick_int("\nEnter a number: ", 0, len(entries))
    if choice == 0:
        return
    sel = entries[choice - 1]
    rel = os.path.relpath(sel["path"], ROOT)
    if sel["kind"] == "native":
        print(f"\nContinuing from: {rel}\n")
        _run_play(["--continue", rel])
    else:
        print(f"\nReplaying: {rel}\n")
        _run_play(["--load", rel])


def _main_interactive() -> None:
    sys.path.insert(0, os.path.join(ROOT, "python"))
    import play as play_mod  # noqa: PLC0415

    play_mod.ensure_setup()
    titles = _load_char_titles()

    while True:
        print(
            """
╔══════════════════════════════════════╗
║       Slay the Spire 2  CLI          ║
╚══════════════════════════════════════╝

  1  New game
  2  Load save
  0  Quit
"""
        )
        c = _prompt_line("Choose (0–2): ").lower()
        if c in ("0", "q", "quit", "exit", ""):
            print("Goodbye.")
            break
        if c == "1":
            _menu_new_game(titles)
        elif c == "2":
            _menu_load_save(titles)
        else:
            print("  Invalid choice. Enter 0, 1 or 2.")


def main() -> None:
    parser = argparse.ArgumentParser(description="sts2-cli interactive launcher")
    parser.parse_args()
    _main_interactive()


if __name__ == "__main__":
    main()
