#!/usr/bin/env python3
from __future__ import annotations

import os
import queue
import shutil
import subprocess
import sys
import sysconfig
import tempfile
import threading
from dataclasses import dataclass, field
from datetime import datetime
from pathlib import Path
from typing import Dict, Iterable, List, Optional, Set, Tuple

import tkinter as tk
from tkinter import filedialog, messagebox, ttk

APP_NAME = "Git History File Cleaner"
VERSION = "1.1.0"
ZERO_OID = "0" * 40


@dataclass
class Event:
    commit: str
    unix_time: int
    iso_time: str
    action: str
    old_oid: str
    new_oid: str
    size: int = 0


@dataclass
class FileRecord:
    path: str
    first_unix: int
    first_iso: str
    last_unix: int
    last_iso: str
    first_commit: str
    last_commit: str
    events: List[Event] = field(default_factory=list)
    blob_ids: Set[str] = field(default_factory=set)
    historical_bytes: int = 0
    max_bytes: int = 0
    versions: int = 0
    in_head: bool = False

    @property
    def first_label(self) -> str:
        return self.first_iso[:10] if self.first_iso else "—"

    @property
    def last_label(self) -> str:
        return "present" if self.in_head else (self.last_iso[:10] if self.last_iso else "—")


@dataclass
class UnreachableObject:
    oid: str
    obj_type: str
    logical_size: int
    disk_size: int
    classification: str  # "orphan" or "reflog-only"
    known_path: str = ""


@dataclass
class StoreStats:
    loose_count: int = 0
    loose_kib: int = 0
    packed_count: int = 0
    packs: int = 0
    pack_kib: int = 0
    prune_packable: int = 0
    garbage_count: int = 0
    garbage_kib: int = 0

    @property
    def object_store_bytes(self) -> int:
        return (self.loose_kib + self.pack_kib + self.garbage_kib) * 1024


def human_bytes(n: int) -> str:
    n = max(0, int(n))
    units = ("B", "KB", "MB", "GB", "TB", "PB")
    v = float(n)
    for unit in units:
        if v < 1024 or unit == units[-1]:
            if unit == "B":
                return f"{int(v)} {unit}"
            if v >= 100:
                return f"{v:.0f} {unit}"
            if v >= 10:
                return f"{v:.1f} {unit}"
            return f"{v:.2f} {unit}"
        v /= 1024
    return f"{n} B"


def creation_flags() -> int:
    if os.name == "nt":
        return getattr(subprocess, "CREATE_NO_WINDOW", 0)
    return 0


def run_external(cmd: List[str], *, cwd: Optional[Path] = None,
                 input_bytes: Optional[bytes] = None,
                 check: bool = False) -> subprocess.CompletedProcess:
    cp = subprocess.run(
        [str(x) for x in cmd],
        cwd=str(cwd) if cwd else None,
        input=input_bytes,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        check=False,
        creationflags=creation_flags(),
    )
    if check and cp.returncode != 0:
        err = cp.stderr.decode("utf-8", errors="replace").strip()
        out = cp.stdout.decode("utf-8", errors="replace").strip()
        raise RuntimeError(err or out or f"Command failed with exit code {cp.returncode}")
    return cp


def run_git(repo: Path, args: List[str], *, check: bool = True,
            input_bytes: Optional[bytes] = None) -> subprocess.CompletedProcess:
    return run_external(["git", "-C", str(repo)] + args, input_bytes=input_bytes, check=check)


def git_available() -> Tuple[bool, str]:
    try:
        cp = run_external(["git", "--version"])
        text = (cp.stdout or cp.stderr).decode("utf-8", errors="replace").strip()
        return cp.returncode == 0, text
    except OSError as e:
        return False, str(e)


def repo_root(path: Path) -> Path:
    cp = run_git(path, ["rev-parse", "--show-toplevel"])
    text = cp.stdout.decode("utf-8", errors="replace").strip()
    if not text:
        raise RuntimeError("Not a non-bare Git working repository.")
    return Path(text).resolve()


def get_git_dir(repo: Path) -> Path:
    cp = run_git(repo, ["rev-parse", "--git-dir"])
    p = Path(cp.stdout.decode("utf-8", errors="replace").strip())
    if not p.is_absolute():
        p = repo / p
    return p.resolve()


def commit_count(repo: Path) -> int:
    cp = run_git(repo, ["rev-list", "--all", "--count"], check=False)
    try:
        return int(cp.stdout.decode("ascii", errors="replace").strip() or "0")
    except ValueError:
        return 0


def current_head_paths(repo: Path) -> Set[str]:
    cp = run_git(repo, ["-c", "core.quotepath=false", "ls-tree", "-r", "-z", "--name-only", "HEAD"], check=False)
    if cp.returncode != 0:
        return set()
    return {x.decode("utf-8", errors="replace") for x in cp.stdout.split(b"\0") if x}


def batch_object_info(repo: Path, oids: Iterable[str]) -> Dict[str, Tuple[str, int, int]]:
    unique = sorted(set(oids))
    if not unique:
        return {}

    payload = ("\n".join(unique) + "\n").encode("ascii")

    # objectsize:disk is available in modern Git and is especially useful for
    # giant loose objects. Fall back cleanly if the installed Git lacks it.
    cp = run_git(
        repo,
        ["cat-file", "--batch-check=%(objectname) %(objecttype) %(objectsize) %(objectsize:disk)"],
        check=False,
        input_bytes=payload,
    )

    result: Dict[str, Tuple[str, int, int]] = {}
    if cp.returncode == 0:
        for line in cp.stdout.decode("ascii", errors="replace").splitlines():
            parts = line.split()
            if len(parts) >= 4:
                try:
                    result[parts[0]] = (parts[1], int(parts[2]), int(parts[3]))
                except ValueError:
                    pass
        return result

    cp = run_git(
        repo,
        ["cat-file", "--batch-check=%(objectname) %(objecttype) %(objectsize)"],
        input_bytes=payload,
    )
    gitdir = get_git_dir(repo)
    for line in cp.stdout.decode("ascii", errors="replace").splitlines():
        parts = line.split()
        if len(parts) >= 3:
            try:
                oid, typ, logical = parts[0], parts[1], int(parts[2])
            except ValueError:
                continue
            loose = gitdir / "objects" / oid[:2] / oid[2:]
            disk = loose.stat().st_size if loose.is_file() else 0
            result[oid] = (typ, logical, disk)
    return result


def scan_history(repo: Path, progress_cb=None) -> List[FileRecord]:
    total = max(1, commit_count(repo))
    cmd = [
        "git", "-C", str(repo),
        "-c", "core.quotepath=false",
        "log", "--all", "--root", "--reverse", "--topo-order",
        "--format=@@@%H|%ct|%cI%x00",
        "--raw", "-z", "--no-abbrev", "--no-renames",
    ]
    proc = subprocess.Popen(
        cmd,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        creationflags=creation_flags(),
    )
    assert proc.stdout is not None

    records: Dict[str, FileRecord] = {}
    all_oids: Set[str] = set()
    current_commit = ""
    current_unix = 0
    current_iso = ""
    pending_meta: Optional[Tuple[str, str, str]] = None
    commits_seen = 0
    buffer = b""

    def consume(token: bytes):
        nonlocal current_commit, current_unix, current_iso, pending_meta, commits_seen
        token = token.lstrip(b"\n")
        if not token:
            return

        if token.startswith(b"@@@"):
            text = token[3:].decode("utf-8", errors="replace")
            parts = text.split("|", 2)
            if len(parts) == 3:
                current_commit = parts[0]
                try:
                    current_unix = int(parts[1])
                except ValueError:
                    current_unix = 0
                current_iso = parts[2]
                commits_seen += 1
                if progress_cb and (commits_seen == 1 or commits_seen % 50 == 0):
                    progress_cb(
                        min(0.58, commits_seen / total * 0.58),
                        f"Reading reachable history {commits_seen:,} / {total:,} commits…",
                    )
            pending_meta = None
            return

        if token.startswith(b":"):
            text = token.decode("ascii", errors="replace")
            parts = text.split()
            if len(parts) >= 5:
                pending_meta = (parts[2], parts[3], parts[4])
            else:
                pending_meta = None
            return

        if pending_meta is None or not current_commit:
            return

        path = token.decode("utf-8", errors="replace")
        old_oid, new_oid, action = pending_meta
        pending_meta = None

        rec = records.get(path)
        if rec is None:
            rec = FileRecord(
                path=path,
                first_unix=current_unix,
                first_iso=current_iso,
                last_unix=current_unix,
                last_iso=current_iso,
                first_commit=current_commit,
                last_commit=current_commit,
            )
            records[path] = rec
        else:
            if current_unix and (not rec.first_unix or current_unix < rec.first_unix):
                rec.first_unix = current_unix
                rec.first_iso = current_iso
                rec.first_commit = current_commit
            if current_unix >= rec.last_unix:
                rec.last_unix = current_unix
                rec.last_iso = current_iso
                rec.last_commit = current_commit

        rec.events.append(Event(
            commit=current_commit,
            unix_time=current_unix,
            iso_time=current_iso,
            action=action,
            old_oid=old_oid,
            new_oid=new_oid,
        ))

        if new_oid != ZERO_OID:
            rec.blob_ids.add(new_oid)
            all_oids.add(new_oid)
        if old_oid != ZERO_OID:
            all_oids.add(old_oid)

    while True:
        chunk = proc.stdout.read(1024 * 1024)
        if not chunk:
            break
        buffer += chunk
        parts = buffer.split(b"\0")
        buffer = parts.pop()
        for token in parts:
            consume(token)

    if buffer:
        consume(buffer)

    stderr = proc.stderr.read() if proc.stderr else b""
    rc = proc.wait()
    if rc != 0:
        raise RuntimeError(stderr.decode("utf-8", errors="replace").strip()
                           or f"git log failed with exit code {rc}")

    if progress_cb:
        progress_cb(0.60, f"Reading sizes for {len(all_oids):,} reachable objects…")

    infos = batch_object_info(repo, all_oids)
    head = current_head_paths(repo)

    for rec in records.values():
        sizes = [infos.get(oid, ("", 0, 0))[1] for oid in rec.blob_ids]
        rec.historical_bytes = sum(sizes)
        rec.max_bytes = max(sizes, default=0)
        rec.versions = len(rec.blob_ids)
        rec.in_head = rec.path in head

        for ev in rec.events:
            oid = ev.new_oid if ev.new_oid != ZERO_OID else ev.old_oid
            if oid != ZERO_OID:
                ev.size = infos.get(oid, ("", 0, 0))[1]

    return list(records.values())


def parse_fsck_unreachable(repo: Path, no_reflogs: bool) -> Dict[str, str]:
    args = ["fsck", "--full", "--unreachable"]
    if no_reflogs:
        args.append("--no-reflogs")

    cp = run_git(repo, args, check=False)
    # git fsck can use stderr for progress/diagnostics; useful object lines are
    # normally on stdout. Parse both in case a Git build differs.
    text = (
        cp.stdout.decode("utf-8", errors="replace")
        + "\n"
        + cp.stderr.decode("utf-8", errors="replace")
    )

    result: Dict[str, str] = {}
    for raw in text.splitlines():
        line = raw.strip()
        parts = line.split()
        if len(parts) >= 3 and parts[0] in ("unreachable", "dangling"):
            typ, oid = parts[1], parts[2]
            if len(oid) >= 40:
                result[oid[:40]] = typ
    return result


def reflog_object_paths(repo: Path) -> Dict[str, str]:
    cp = run_git(
        repo,
        ["-c", "core.quotepath=false", "rev-list", "--reflog", "--objects", "--all"],
        check=False,
    )
    paths: Dict[str, str] = {}
    for raw in cp.stdout.decode("utf-8", errors="replace").splitlines():
        if " " not in raw:
            continue
        oid, path = raw.split(" ", 1)
        if len(oid) == 40 and path:
            paths.setdefault(oid, path)
    return paths


def scan_unreachable(repo: Path, progress_cb=None) -> List[UnreachableObject]:
    if progress_cb:
        progress_cb(0.66, "Finding unreachable objects…")

    # Default fsck considers reflogs. These are genuinely orphan/unreachable
    # from both normal refs and reflog recovery.
    orphan = parse_fsck_unreachable(repo, no_reflogs=False)

    if progress_cb:
        progress_cb(0.74, "Finding objects kept alive only by reflogs…")

    # Ignoring reflogs additionally exposes objects reachable only from reflogs.
    without_reflogs = parse_fsck_unreachable(repo, no_reflogs=True)

    all_oids = set(without_reflogs) | set(orphan)
    infos = batch_object_info(repo, all_oids)

    # Recover a path where Git still has one through reflog-visible history.
    path_map = reflog_object_paths(repo)

    rows: List[UnreachableObject] = []
    for oid in all_oids:
        typ_from_fsck = without_reflogs.get(oid) or orphan.get(oid) or "object"
        typ, logical, disk = infos.get(oid, (typ_from_fsck, 0, 0))
        classification = "orphan" if oid in orphan else "reflog-only"
        rows.append(UnreachableObject(
            oid=oid,
            obj_type=typ,
            logical_size=logical,
            disk_size=disk,
            classification=classification,
            known_path=path_map.get(oid, ""),
        ))

    rows.sort(key=lambda x: (x.disk_size, x.logical_size), reverse=True)
    return rows


def object_store_stats(repo: Path) -> StoreStats:
    cp = run_git(repo, ["count-objects", "-v"], check=False)
    vals: Dict[str, int] = {}
    for raw in cp.stdout.decode("ascii", errors="replace").splitlines():
        if ":" not in raw:
            continue
        key, value = raw.split(":", 1)
        try:
            vals[key.strip()] = int(value.strip())
        except ValueError:
            pass

    return StoreStats(
        loose_count=vals.get("count", 0),
        loose_kib=vals.get("size", 0),
        packed_count=vals.get("in-pack", 0),
        packs=vals.get("packs", 0),
        pack_kib=vals.get("size-pack", 0),
        prune_packable=vals.get("prune-packable", 0),
        garbage_count=vals.get("garbage", 0),
        garbage_kib=vals.get("size-garbage", 0),
    )


def working_tree_clean(repo: Path) -> Tuple[bool, str]:
    cp = run_git(repo, ["status", "--porcelain", "--untracked-files=no"], check=False)
    text = cp.stdout.decode("utf-8", errors="replace").strip()
    return cp.returncode == 0 and not text, text


def make_backup_bundle(repo: Path) -> Path:
    stamp = datetime.now().strftime("%Y%m%d-%H%M%S")
    dest = repo.parent / f"{repo.name}-before-history-clean-{stamp}.bundle"
    cp = run_git(repo, ["bundle", "create", str(dest), "--all"], check=False)
    if cp.returncode != 0:
        err = cp.stderr.decode("utf-8", errors="replace").strip()
        raise RuntimeError(err or "Could not create backup bundle.")
    return dest


def find_filter_repo(repo: Path) -> Tuple[Optional[List[str]], str]:
    candidates: List[List[str]] = [["git", "-C", str(repo), "filter-repo"]]

    direct = shutil.which("git-filter-repo")
    if direct:
        candidates.append([direct])

    possible: List[Path] = []
    try:
        scripts = sysconfig.get_path("scripts")
        if scripts:
            possible.append(Path(scripts) / ("git-filter-repo.exe" if os.name == "nt" else "git-filter-repo"))
    except Exception:
        pass

    exe_dir = Path(sys.executable).resolve().parent
    possible.extend([
        exe_dir / ("git-filter-repo.exe" if os.name == "nt" else "git-filter-repo"),
        exe_dir / "Scripts" / ("git-filter-repo.exe" if os.name == "nt" else "git-filter-repo"),
    ])

    try:
        cp = run_external(["git", "--exec-path"])
        ep = cp.stdout.decode("utf-8", errors="replace").strip()
        if ep:
            possible.extend([
                Path(ep) / "git-filter-repo",
                Path(ep) / "git-filter-repo.exe",
            ])
    except Exception:
        pass

    for p in possible:
        if p.is_file():
            candidates.append([str(p)])

    seen = set()
    errors = []
    for prefix in candidates:
        key = tuple(prefix)
        if key in seen:
            continue
        seen.add(key)
        try:
            cp = run_external(prefix + ["--version"], cwd=repo)
            text = (cp.stdout or cp.stderr).decode("utf-8", errors="replace").strip()
            if cp.returncode == 0:
                return prefix, text
            if text:
                errors.append(text)
        except OSError as e:
            errors.append(str(e))

    return None, "\n".join(errors[-3:]) or "git-filter-repo not found."


class CleanerApp(tk.Tk):
    def __init__(self):
        super().__init__()
        self.title(f"{APP_NAME} {VERSION}")
        self.geometry("1240x760")
        self.minsize(900, 560)

        self.repo: Optional[Path] = None
        self.records: List[FileRecord] = []
        self.by_path: Dict[str, FileRecord] = {}
        self.selected: Set[str] = set()
        self.filtered_paths: List[str] = []
        self.unreachable: List[UnreachableObject] = []
        self.store_stats = StoreStats()

        self.sort_col = "hist"
        self.sort_reverse = True
        self.unreachable_sort_col = "disk"
        self.unreachable_sort_reverse = True

        self.worker_queue: "queue.Queue[tuple]" = queue.Queue()
        self.busy = False

        self._build_ui()
        self.after(100, self._poll_worker)

    def _build_ui(self):
        top = ttk.Frame(self, padding=(10, 10, 10, 4))
        top.pack(fill="x")

        ttk.Label(top, text="Repository:").pack(side="left")
        self.repo_var = tk.StringVar()
        ttk.Entry(top, textvariable=self.repo_var).pack(side="left", fill="x", expand=True, padx=(8, 6))
        ttk.Button(top, text="Browse…", command=self.choose_repo).pack(side="left", padx=(0, 6))
        self.scan_btn = ttk.Button(top, text="Scan history", command=self.start_scan)
        self.scan_btn.pack(side="left")

        self.disk_summary_var = tk.StringVar(value="Scan a repository to inspect reachable history and unreachable Git objects.")
        ttk.Label(
            self,
            textvariable=self.disk_summary_var,
            padding=(10, 2, 10, 6),
        ).pack(fill="x")

        self.notebook = ttk.Notebook(self)
        self.notebook.pack(fill="both", expand=True, padx=10, pady=(0, 4))

        self.history_tab = ttk.Frame(self.notebook)
        self.unreachable_tab = ttk.Frame(self.notebook)
        self.notebook.add(self.history_tab, text="Historical paths")
        self.notebook.add(self.unreachable_tab, text="Unreachable objects")

        self._build_history_tab()
        self._build_unreachable_tab()

        bottom = ttk.Frame(self, padding=(10, 4, 10, 10))
        bottom.pack(fill="x")
        self.progress = ttk.Progressbar(bottom, mode="determinate", maximum=100)
        self.progress.pack(side="left", fill="x", expand=True)
        self.status_var = tk.StringVar(value="Ready")
        ttk.Label(bottom, textvariable=self.status_var, width=48).pack(side="left", padx=(8, 0))

    def _build_history_tab(self):
        filters = ttk.Frame(self.history_tab, padding=(0, 6, 0, 4))
        filters.pack(fill="x")

        ttk.Label(filters, text="Find:").pack(side="left")
        self.search_var = tk.StringVar()
        ttk.Entry(filters, textvariable=self.search_var, width=32).pack(side="left", padx=(6, 12))
        self.search_var.trace_add("write", lambda *_: self.refresh_history_tree())

        self.deleted_only_var = tk.BooleanVar(value=False)
        ttk.Checkbutton(
            filters,
            text="Deleted from HEAD only",
            variable=self.deleted_only_var,
            command=self.refresh_history_tree,
        ).pack(side="left", padx=(0, 12))

        ttk.Label(filters, text="Min max-version size:").pack(side="left")
        self.min_mb_var = tk.StringVar(value="0")
        ttk.Entry(filters, textvariable=self.min_mb_var, width=7).pack(side="left", padx=(6, 3))
        ttk.Label(filters, text="MB").pack(side="left")
        self.min_mb_var.trace_add("write", lambda *_: self.refresh_history_tree())

        actions = ttk.Frame(self.history_tab, padding=(0, 0, 0, 5))
        actions.pack(fill="x")
        ttk.Button(actions, text="Select filtered", command=self.select_filtered).pack(side="left")
        ttk.Button(actions, text="Clear selection", command=self.clear_selection).pack(side="left", padx=(6, 0))
        ttk.Button(actions, text="View path history", command=self.view_history).pack(side="left", padx=(18, 0))

        self.history_summary_var = tk.StringVar(value="0 shown • 0 selected")
        ttk.Label(actions, textvariable=self.history_summary_var).pack(side="right")

        table_frame = ttk.Frame(self.history_tab)
        table_frame.pack(fill="both", expand=True)

        cols = ("pick", "path", "hist", "max", "versions", "first", "last", "head")
        self.tree = ttk.Treeview(table_frame, columns=cols, show="headings", selectmode="browse")
        self.tree.heading("pick", text="✓")
        self.tree.heading("path", text="Historical path", command=lambda: self.sort_history("path"))
        self.tree.heading("hist", text="Version bytes", command=lambda: self.sort_history("hist"))
        self.tree.heading("max", text="Largest version", command=lambda: self.sort_history("max"))
        self.tree.heading("versions", text="Versions", command=lambda: self.sort_history("versions"))
        self.tree.heading("first", text="First seen", command=lambda: self.sort_history("first"))
        self.tree.heading("last", text="Until / last event", command=lambda: self.sort_history("last"))
        self.tree.heading("head", text="HEAD", command=lambda: self.sort_history("head"))

        self.tree.column("pick", width=42, anchor="center", stretch=False)
        self.tree.column("path", width=500, minwidth=220, anchor="w")
        self.tree.column("hist", width=115, anchor="e", stretch=False)
        self.tree.column("max", width=115, anchor="e", stretch=False)
        self.tree.column("versions", width=72, anchor="e", stretch=False)
        self.tree.column("first", width=100, anchor="center", stretch=False)
        self.tree.column("last", width=125, anchor="center", stretch=False)
        self.tree.column("head", width=75, anchor="center", stretch=False)

        vs = ttk.Scrollbar(table_frame, orient="vertical", command=self.tree.yview)
        hs = ttk.Scrollbar(table_frame, orient="horizontal", command=self.tree.xview)
        self.tree.configure(yscrollcommand=vs.set, xscrollcommand=hs.set)
        self.tree.grid(row=0, column=0, sticky="nsew")
        vs.grid(row=0, column=1, sticky="ns")
        hs.grid(row=1, column=0, sticky="ew")
        table_frame.rowconfigure(0, weight=1)
        table_frame.columnconfigure(0, weight=1)

        self.tree.bind("<Button-1>", self._history_click)
        self.tree.bind("<space>", self._toggle_focused)
        self.tree.bind("<Double-1>", lambda e: self.view_history())

        foot = ttk.Frame(self.history_tab, padding=(0, 6, 0, 0))
        foot.pack(fill="x")
        self.remove_btn = ttk.Button(
            foot,
            text="Preview & remove selected…",
            command=self.preview_remove,
            state="disabled",
        )
        self.remove_btn.pack(side="right")

    def _build_unreachable_tab(self):
        info = ttk.Label(
            self.unreachable_tab,
            text=(
                "These objects are not reachable from current branches/tags. "
                "'orphan' is not even protected by reflogs; 'reflog-only' survives only because reflogs still reference it."
            ),
            wraplength=1100,
            padding=(0, 7, 0, 6),
        )
        info.pack(fill="x")

        actions = ttk.Frame(self.unreachable_tab, padding=(0, 0, 0, 5))
        actions.pack(fill="x")

        self.unreachable_summary_var = tk.StringVar(value="No scan yet.")
        ttk.Label(actions, textvariable=self.unreachable_summary_var).pack(side="left")

        self.purge_btn = ttk.Button(
            actions,
            text="Purge unreachable objects…",
            command=self.preview_purge,
            state="disabled",
        )
        self.purge_btn.pack(side="right")

        frame = ttk.Frame(self.unreachable_tab)
        frame.pack(fill="both", expand=True)

        cols = ("class", "type", "disk", "logical", "oid", "path")
        self.utree = ttk.Treeview(frame, columns=cols, show="headings", selectmode="browse")
        self.utree.heading("class", text="Class", command=lambda: self.sort_unreachable("class"))
        self.utree.heading("type", text="Type", command=lambda: self.sort_unreachable("type"))
        self.utree.heading("disk", text="On-disk size", command=lambda: self.sort_unreachable("disk"))
        self.utree.heading("logical", text="Logical size", command=lambda: self.sort_unreachable("logical"))
        self.utree.heading("oid", text="Object ID", command=lambda: self.sort_unreachable("oid"))
        self.utree.heading("path", text="Known historical path", command=lambda: self.sort_unreachable("path"))

        self.utree.column("class", width=100, anchor="center", stretch=False)
        self.utree.column("type", width=75, anchor="center", stretch=False)
        self.utree.column("disk", width=120, anchor="e", stretch=False)
        self.utree.column("logical", width=120, anchor="e", stretch=False)
        self.utree.column("oid", width=310, anchor="w", stretch=False)
        self.utree.column("path", width=420, minwidth=200, anchor="w")

        vs = ttk.Scrollbar(frame, orient="vertical", command=self.utree.yview)
        hs = ttk.Scrollbar(frame, orient="horizontal", command=self.utree.xview)
        self.utree.configure(yscrollcommand=vs.set, xscrollcommand=hs.set)
        self.utree.grid(row=0, column=0, sticky="nsew")
        vs.grid(row=0, column=1, sticky="ns")
        hs.grid(row=1, column=0, sticky="ew")
        frame.rowconfigure(0, weight=1)
        frame.columnconfigure(0, weight=1)

    def choose_repo(self):
        p = filedialog.askdirectory(title="Choose Git repository")
        if p:
            self.repo_var.set(p)

    def start_scan(self):
        if self.busy:
            return

        ok, text = git_available()
        if not ok:
            messagebox.showerror(APP_NAME, f"Git was not found.\n\n{text}")
            return

        raw = self.repo_var.get().strip()
        if not raw:
            messagebox.showwarning(APP_NAME, "Choose a repository first.")
            return

        try:
            repo = repo_root(Path(raw))
        except Exception as e:
            messagebox.showerror(APP_NAME, f"Could not open repository:\n\n{e}")
            return

        self.repo = repo
        self.repo_var.set(str(repo))
        self.records.clear()
        self.by_path.clear()
        self.selected.clear()
        self.unreachable.clear()
        self.tree.delete(*self.tree.get_children())
        self.utree.delete(*self.utree.get_children())
        self._set_busy(True)
        self.progress["value"] = 0
        self.status_var.set("Starting scan…")

        def progress(frac, text):
            self.worker_queue.put(("progress", frac, text))

        def work():
            try:
                records = scan_history(repo, progress)
                progress(0.82, "Reading unreachable object sizes…")
                unreachable = scan_unreachable(repo, progress)
                progress(0.96, "Reading Git object-store usage…")
                stats = object_store_stats(repo)
                progress(1.0, "Scan complete.")
                self.worker_queue.put(("scan_done", records, unreachable, stats))
            except Exception as e:
                self.worker_queue.put(("error", str(e)))

        threading.Thread(target=work, daemon=True).start()

    def _set_busy(self, busy: bool):
        self.busy = busy
        self.scan_btn.configure(state="disabled" if busy else "normal")
        if busy:
            self.remove_btn.configure(state="disabled")
            self.purge_btn.configure(state="disabled")
        else:
            self.remove_btn.configure(state="normal" if self.selected else "disabled")
            self.purge_btn.configure(state="normal" if self.unreachable else "disabled")

    def _poll_worker(self):
        try:
            while True:
                msg = self.worker_queue.get_nowait()
                kind = msg[0]
                if kind == "progress":
                    _, frac, text = msg
                    self.progress["value"] = float(frac) * 100
                    self.status_var.set(text)

                elif kind == "scan_done":
                    _, records, unreachable, stats = msg
                    self.records = records
                    self.by_path = {r.path: r for r in records}
                    self.unreachable = unreachable
                    self.store_stats = stats
                    self._set_busy(False)
                    self.refresh_history_tree()
                    self.refresh_unreachable_tree()
                    self.refresh_disk_summary()
                    self.progress["value"] = 100
                    self.status_var.set(
                        f"Scan complete: {len(records):,} paths, {len(unreachable):,} unreachable objects"
                    )

                elif kind == "rewrite_done":
                    _, text = msg
                    self._set_busy(False)
                    messagebox.showinfo(APP_NAME, text)
                    self.start_scan()

                elif kind == "purge_done":
                    _, text = msg
                    self._set_busy(False)
                    messagebox.showinfo(APP_NAME, text)
                    self.start_scan()

                elif kind == "error":
                    _, text = msg
                    self._set_busy(False)
                    self.status_var.set("Error")
                    messagebox.showerror(APP_NAME, text)
        except queue.Empty:
            pass
        self.after(100, self._poll_worker)

    def _parse_min_bytes(self) -> int:
        t = self.min_mb_var.get().strip().replace(",", ".")
        if not t:
            return 0
        try:
            return int(max(0.0, float(t)) * 1024 * 1024)
        except ValueError:
            return 0

    def _history_key(self, r: FileRecord):
        return {
            "path": r.path.casefold(),
            "hist": r.historical_bytes,
            "max": r.max_bytes,
            "versions": r.versions,
            "first": r.first_unix,
            "last": r.last_unix,
            "head": r.in_head,
        }.get(self.sort_col, r.historical_bytes)

    def sort_history(self, col: str):
        if self.sort_col == col:
            self.sort_reverse = not self.sort_reverse
        else:
            self.sort_col = col
            self.sort_reverse = col in ("hist", "max", "versions", "first", "last")
        self.refresh_history_tree()

    def refresh_history_tree(self):
        if not self.records:
            self.history_summary_var.set("0 shown • 0 selected")
            return

        q = self.search_var.get().strip().casefold()
        deleted_only = self.deleted_only_var.get()
        min_bytes = self._parse_min_bytes()

        rows = []
        for r in self.records:
            if q and q not in r.path.casefold():
                continue
            if deleted_only and r.in_head:
                continue
            if r.max_bytes < min_bytes:
                continue
            rows.append(r)

        rows.sort(key=self._history_key, reverse=self.sort_reverse)
        self.filtered_paths = [r.path for r in rows]

        self.tree.delete(*self.tree.get_children())
        for r in rows:
            self.tree.insert(
                "", "end", iid=r.path,
                values=(
                    "☑" if r.path in self.selected else "☐",
                    r.path,
                    human_bytes(r.historical_bytes),
                    human_bytes(r.max_bytes),
                    f"{r.versions:,}",
                    r.first_label,
                    r.last_label,
                    "yes" if r.in_head else "deleted",
                ),
            )

        selected_bytes = sum(
            self.by_path[p].historical_bytes for p in self.selected if p in self.by_path
        )
        self.history_summary_var.set(
            f"{len(rows):,} shown • {len(self.selected):,} selected"
            + (f" • {human_bytes(selected_bytes)} path-version bytes" if self.selected else "")
        )
        if not self.busy:
            self.remove_btn.configure(state="normal" if self.selected else "disabled")

    def _history_click(self, event):
        if self.tree.identify_region(event.x, event.y) != "cell":
            return
        if self.tree.identify_column(event.x) != "#1":
            return
        row = self.tree.identify_row(event.y)
        if row:
            self.after_idle(lambda: self.toggle_path(row))

    def _toggle_focused(self, event=None):
        row = self.tree.focus()
        if row:
            self.toggle_path(row)
        return "break"

    def toggle_path(self, path: str):
        if path in self.selected:
            self.selected.remove(path)
        else:
            self.selected.add(path)
        self.refresh_history_tree()

    def select_filtered(self):
        self.selected.update(self.filtered_paths)
        self.refresh_history_tree()

    def clear_selection(self):
        self.selected.clear()
        self.refresh_history_tree()

    def view_history(self):
        path = self.tree.focus()
        rec = self.by_path.get(path)
        if rec is None:
            messagebox.showinfo(APP_NAME, "Select a path first.")
            return

        win = tk.Toplevel(self)
        win.title(f"Path history — {rec.path}")
        win.geometry("900x460")

        ttk.Label(
            win, text=rec.path, font=("", 10, "bold"), padding=(10, 10, 10, 5)
        ).pack(fill="x")

        cols = ("date", "action", "size", "commit")
        tv = ttk.Treeview(win, columns=cols, show="headings")
        for col, title, width, anchor in [
            ("date", "Commit time", 220, "w"),
            ("action", "Change", 80, "center"),
            ("size", "Blob size", 110, "e"),
            ("commit", "Commit", 150, "w"),
        ]:
            tv.heading(col, text=title)
            tv.column(col, width=width, anchor=anchor)

        sb = ttk.Scrollbar(win, orient="vertical", command=tv.yview)
        tv.configure(yscrollcommand=sb.set)
        tv.pack(side="left", fill="both", expand=True, padx=(10, 0), pady=(0, 10))
        sb.pack(side="left", fill="y", padx=(0, 10), pady=(0, 10))

        for ev in sorted(rec.events, key=lambda x: x.unix_time):
            tv.insert("", "end", values=(
                ev.iso_time,
                ev.action,
                human_bytes(ev.size),
                ev.commit[:12],
            ))

    def _unreachable_key(self, r: UnreachableObject):
        return {
            "class": r.classification,
            "type": r.obj_type,
            "disk": r.disk_size,
            "logical": r.logical_size,
            "oid": r.oid,
            "path": r.known_path.casefold(),
        }.get(self.unreachable_sort_col, r.disk_size)

    def sort_unreachable(self, col: str):
        if self.unreachable_sort_col == col:
            self.unreachable_sort_reverse = not self.unreachable_sort_reverse
        else:
            self.unreachable_sort_col = col
            self.unreachable_sort_reverse = col in ("disk", "logical")
        self.refresh_unreachable_tree()

    def refresh_unreachable_tree(self):
        self.utree.delete(*self.utree.get_children())

        rows = sorted(
            self.unreachable,
            key=self._unreachable_key,
            reverse=self.unreachable_sort_reverse,
        )
        for i, r in enumerate(rows):
            self.utree.insert(
                "", "end", iid=f"u{i}",
                values=(
                    r.classification,
                    r.obj_type,
                    human_bytes(r.disk_size),
                    human_bytes(r.logical_size),
                    r.oid,
                    r.known_path or "—",
                ),
            )

        orphan_count = sum(1 for r in rows if r.classification == "orphan")
        reflog_count = len(rows) - orphan_count
        disk_bytes = sum(r.disk_size for r in rows)
        logical_bytes = sum(r.logical_size for r in rows)
        self.unreachable_summary_var.set(
            f"{len(rows):,} objects • {orphan_count:,} orphan • "
            f"{reflog_count:,} reflog-only • "
            f"{human_bytes(disk_bytes)} attributed on disk • {human_bytes(logical_bytes)} logical"
        )
        if not self.busy:
            self.purge_btn.configure(state="normal" if rows else "disabled")

    def refresh_disk_summary(self):
        reachable_versions = sum(r.historical_bytes for r in self.records)
        unreachable_disk = sum(r.disk_size for r in self.unreachable)
        s = self.store_stats
        self.disk_summary_var.set(
            f"Git object store ≈ {human_bytes(s.object_store_bytes)}  •  "
            f"loose {s.loose_count:,} / {human_bytes(s.loose_kib * 1024)}  •  "
            f"packed {s.packed_count:,} / {human_bytes(s.pack_kib * 1024)}  •  "
            f"reachable path-version bytes {human_bytes(reachable_versions)}  •  "
            f"unreachable attributed on disk {human_bytes(unreachable_disk)}"
        )

    def preview_remove(self):
        if not self.repo or not self.selected:
            return

        clean, details = working_tree_clean(self.repo)
        if not clean:
            messagebox.showerror(
                APP_NAME,
                "The tracked working tree is not clean.\n\n"
                "Commit or stash tracked changes first.\n\n" + details[:2000],
            )
            return

        filter_cmd, version = find_filter_repo(self.repo)
        if filter_cmd is None:
            messagebox.showerror(
                APP_NAME,
                "git-filter-repo is not installed or could not be found.\n\n"
                "Install it with:\n\n"
                "    py -m pip install git-filter-repo\n\n"
                f"Detection result:\n{version}",
            )
            return

        paths = sorted(self.selected, key=str.casefold)

        win = tk.Toplevel(self)
        win.title("Confirm history rewrite")
        win.transient(self)
        win.grab_set()
        win.geometry("760x610")

        ttk.Label(
            win,
            text=f"Remove {len(paths):,} path(s) from reachable history",
            font=("", 11, "bold"),
            padding=(12, 12, 12, 5),
        ).pack(fill="x")

        ttk.Label(
            win,
            text=(
                "Affected commit IDs will necessarily change because their trees change. "
                "The rewrite requests that commits which become empty are retained."
            ),
            wraplength=720,
            justify="left",
            padding=(12, 0, 12, 8),
        ).pack(fill="x")

        txt = tk.Text(win, wrap="none", height=14)
        txt.pack(fill="both", expand=True, padx=12, pady=(0, 8))
        txt.insert("1.0", "\n".join(paths))
        txt.configure(state="disabled")

        backup_var = tk.BooleanVar(value=True)
        ttk.Checkbutton(
            win,
            text="Create a .bundle backup of all current refs first",
            variable=backup_var,
        ).pack(anchor="w", padx=12)

        cleanup_var = tk.BooleanVar(value=True)
        ttk.Checkbutton(
            win,
            text=(
                "After rewrite: expire reflogs and run git gc --prune=now "
                "(also purges pre-existing unreachable objects)"
            ),
            variable=cleanup_var,
        ).pack(anchor="w", padx=12, pady=(5, 0))

        ttk.Label(
            win,
            text=(
                "The cleanup option permanently removes reflog/undo history and orphan objects. "
                "A normal Git bundle cannot preserve a standalone orphan blob."
            ),
            wraplength=720,
            justify="left",
            padding=(32, 2, 12, 8),
        ).pack(fill="x")

        row = ttk.Frame(win, padding=(12, 5, 12, 12))
        row.pack(fill="x")
        ttk.Label(row, text="Type REWRITE:").pack(side="left")
        confirm = tk.StringVar()
        entry = ttk.Entry(row, textvariable=confirm, width=12)
        entry.pack(side="left", padx=(8, 12))

        cancel = ttk.Button(row, text="Cancel", command=win.destroy)
        cancel.pack(side="right", padx=(6, 0))
        go = ttk.Button(row, text="Rewrite history", state="disabled")
        go.pack(side="right")

        confirm.trace_add(
            "write",
            lambda *_: go.configure(state="normal" if confirm.get() == "REWRITE" else "disabled"),
        )

        def execute():
            win.destroy()
            self._begin_rewrite(paths, backup_var.get(), cleanup_var.get(), filter_cmd)

        go.configure(command=execute)
        entry.focus_set()

    def _begin_rewrite(self, paths: List[str], make_backup: bool,
                       cleanup_after: bool, filter_cmd: List[str]):
        assert self.repo is not None
        repo = self.repo
        self._set_busy(True)
        self.progress["value"] = 0

        def progress(frac, text):
            self.worker_queue.put(("progress", frac, text))

        def work():
            paths_file = None
            backup = None
            try:
                if any("\n" in p or "\r" in p for p in paths):
                    raise RuntimeError("A selected path contains a newline; refusing to rewrite it.")

                if make_backup:
                    progress(0.05, "Creating bundle backup…")
                    backup = make_backup_bundle(repo)

                tmp = tempfile.NamedTemporaryFile(
                    mode="w", encoding="utf-8", newline="\n",
                    prefix="git-history-cleaner-", suffix=".txt", delete=False,
                )
                paths_file = tmp.name
                with tmp:
                    for p in paths:
                        tmp.write("literal:" + p + "\n")

                progress(0.15, "Rewriting history with git-filter-repo…")
                args = [
                    "--force",
                    "--invert-paths",
                    "--paths-from-file", paths_file,
                    "--prune-empty", "never",
                    "--prune-degenerate", "never",
                    "--preserve-commit-hashes",
                    "--preserve-commit-encoding",
                ]
                cp = run_external(filter_cmd + args, cwd=repo)
                if cp.returncode != 0:
                    err = cp.stderr.decode("utf-8", errors="replace").strip()
                    out = cp.stdout.decode("utf-8", errors="replace").strip()
                    raise RuntimeError(err or out or f"git-filter-repo exited {cp.returncode}")

                cleanup_text = ""
                if cleanup_after:
                    progress(0.75, "Expiring reflogs…")
                    cp = run_git(
                        repo,
                        ["reflog", "expire", "--expire=now", "--expire-unreachable=now", "--all"],
                        check=False,
                    )
                    if cp.returncode != 0:
                        raise RuntimeError(
                            cp.stderr.decode("utf-8", errors="replace").strip()
                            or "git reflog expire failed"
                        )

                    progress(0.82, "Garbage-collecting unreachable Git objects…")
                    cp = run_git(repo, ["gc", "--prune=now"], check=False)
                    if cp.returncode != 0:
                        raise RuntimeError(
                            cp.stderr.decode("utf-8", errors="replace").strip()
                            or "git gc failed"
                        )
                    cleanup_text = "\n\nReflogs were expired and unreachable objects were garbage-collected."

                progress(1.0, "Rewrite complete.")
                msg = f"Removed {len(paths):,} historical path(s)." + cleanup_text
                if backup:
                    msg += f"\n\nBackup bundle:\n{backup}"
                msg += "\n\nInspect the rewritten history before force-pushing."
                self.worker_queue.put(("rewrite_done", msg))

            except Exception as e:
                extra = f"\n\nBackup bundle:\n{backup}" if backup else ""
                self.worker_queue.put(("error", str(e) + extra))
            finally:
                if paths_file:
                    try:
                        os.unlink(paths_file)
                    except OSError:
                        pass

        threading.Thread(target=work, daemon=True).start()

    def preview_purge(self):
        if not self.repo or not self.unreachable:
            return

        orphan = [r for r in self.unreachable if r.classification == "orphan"]
        reflog = [r for r in self.unreachable if r.classification == "reflog-only"]
        disk = sum(r.disk_size for r in self.unreachable)

        win = tk.Toplevel(self)
        win.title("Purge unreachable Git objects")
        win.transient(self)
        win.grab_set()
        win.geometry("690x430")

        ttk.Label(
            win,
            text="Permanently purge unreachable Git objects",
            font=("", 11, "bold"),
            padding=(12, 12, 12, 6),
        ).pack(fill="x")

        ttk.Label(
            win,
            text=(
                f"Current scan found {len(orphan):,} orphan object(s) and "
                f"{len(reflog):,} reflog-only object(s), with about "
                f"{human_bytes(disk)} attributed on disk.\n\n"
                "Git garbage collection works repository-wide, not object-by-object. "
                "Anything eligible when GC runs can be deleted."
            ),
            wraplength=650,
            justify="left",
            padding=(12, 0, 12, 10),
        ).pack(fill="x")

        expire_var = tk.BooleanVar(value=True)
        ttk.Checkbutton(
            win,
            text="Expire all reflogs first (also removes reflog-only recovery history)",
            variable=expire_var,
        ).pack(anchor="w", padx=12, pady=(0, 6))

        ttk.Label(
            win,
            text=(
                "With reflog expiration enabled the tool runs:\n"
                "  git reflog expire --expire=now --expire-unreachable=now --all\n"
                "  git gc --prune=now\n\n"
                "Without it, the tool runs only git gc --prune=now; objects still protected "
                "by reflogs should remain."
            ),
            justify="left",
            padding=(12, 0, 12, 12),
        ).pack(fill="x")

        ttk.Label(
            win,
            text=(
                "WARNING: a .bundle backup cannot preserve a completely standalone orphan blob "
                "that is not reachable from any ref. Purging is irreversible."
            ),
            wraplength=650,
            justify="left",
            padding=(12, 0, 12, 12),
        ).pack(fill="x")

        row = ttk.Frame(win, padding=(12, 4, 12, 12))
        row.pack(fill="x")
        ttk.Label(row, text="Type PURGE:").pack(side="left")
        confirm = tk.StringVar()
        entry = ttk.Entry(row, textvariable=confirm, width=12)
        entry.pack(side="left", padx=(8, 12))

        ttk.Button(row, text="Cancel", command=win.destroy).pack(side="right", padx=(6, 0))
        go = ttk.Button(row, text="Purge now", state="disabled")
        go.pack(side="right")

        confirm.trace_add(
            "write",
            lambda *_: go.configure(state="normal" if confirm.get() == "PURGE" else "disabled"),
        )

        def execute():
            win.destroy()
            self._begin_purge(expire_var.get())

        go.configure(command=execute)
        entry.focus_set()

    def _begin_purge(self, expire_reflogs: bool):
        assert self.repo is not None
        repo = self.repo
        before = object_store_stats(repo)
        self._set_busy(True)
        self.progress["value"] = 0

        def progress(frac, text):
            self.worker_queue.put(("progress", frac, text))

        def work():
            try:
                if expire_reflogs:
                    progress(0.20, "Expiring reflogs…")
                    cp = run_git(
                        repo,
                        ["reflog", "expire", "--expire=now", "--expire-unreachable=now", "--all"],
                        check=False,
                    )
                    if cp.returncode != 0:
                        raise RuntimeError(
                            cp.stderr.decode("utf-8", errors="replace").strip()
                            or "git reflog expire failed"
                        )

                progress(0.45, "Running git gc --prune=now…")
                cp = run_git(repo, ["gc", "--prune=now"], check=False)
                if cp.returncode != 0:
                    raise RuntimeError(
                        cp.stderr.decode("utf-8", errors="replace").strip()
                        or "git gc failed"
                    )

                progress(0.93, "Measuring object store…")
                after = object_store_stats(repo)
                reclaimed = max(0, before.object_store_bytes - after.object_store_bytes)

                progress(1.0, "Purge complete.")
                msg = (
                    f"Git object store before: {human_bytes(before.object_store_bytes)}\n"
                    f"Git object store after:  {human_bytes(after.object_store_bytes)}\n"
                    f"Approx. reclaimed:        {human_bytes(reclaimed)}"
                )
                if expire_reflogs:
                    msg += "\n\nReflogs were expired before garbage collection."
                self.worker_queue.put(("purge_done", msg))
            except Exception as e:
                self.worker_queue.put(("error", str(e)))

        threading.Thread(target=work, daemon=True).start()


def main():
    app = CleanerApp()
    if len(sys.argv) > 1:
        p = Path(sys.argv[1])
        if p.exists():
            app.repo_var.set(str(p.resolve()))
    app.mainloop()


if __name__ == "__main__":
    main()
