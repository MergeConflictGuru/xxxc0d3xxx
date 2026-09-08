#!/usr/bin/env python3
"""
Download songs from YouTube based on clipboard content.

Reads lines from the system clipboard, each formatted as "Artist - Song".
For each line, uses yt-dlp to search YouTube and download the first result.
Prefers Opus audio (WebM container) directly; if not available, downloads AAC (M4A).
No conversion is performed. Files are saved in a timestamped directory next to this script.

Improved to handle SABR experiment and 403 errors by trying multiple player clients.
"""

import os
import sys
import subprocess
import datetime
from pathlib import Path


_YT_DLP_UPDATE_CHECKED_THIS_RUN = False

try:
    import pyperclip
except ImportError:
    print("Error: 'pyperclip' is not installed.")
    print("Install it with: pip install pyperclip")
    sys.exit(1)

def yt_dlp_update_marker() -> Path:
    """Return a user-level marker used to limit self-updates to once per day."""
    if os.name == "nt":
        base = Path(os.environ.get("LOCALAPPDATA", ""))
        if not str(base):
            base = Path.home() / "AppData" / "Local"
    else:
        base = Path(os.environ.get("XDG_CACHE_HOME", ""))
        if not str(base):
            base = Path.home() / ".cache"
    return base / "music-tools" / "yt-dlp-update-date"


def maybe_update_yt_dlp() -> None:
    """Run ``yt-dlp -U`` at most once per local calendar day."""
    global _YT_DLP_UPDATE_CHECKED_THIS_RUN
    if _YT_DLP_UPDATE_CHECKED_THIS_RUN:
        return
    _YT_DLP_UPDATE_CHECKED_THIS_RUN = True

    today = datetime.date.today().isoformat()
    marker = yt_dlp_update_marker()
    try:
        if marker.read_text(encoding="utf-8").strip() == today:
            print(f"yt-dlp update: already checked today ({today}).")
            return
    except (FileNotFoundError, OSError):
        pass

    # Record the attempt before starting it, so a failed/self-replacing process
    # cannot cause every invocation to retry the update on the same day.
    try:
        marker.parent.mkdir(parents=True, exist_ok=True)
        marker.write_text(today + "\n", encoding="utf-8")
    except OSError as error:
        print(f"Warning: could not save the yt-dlp update marker: {error}")

    print("Checking for a yt-dlp update...")
    try:
        result = subprocess.run(
            ["yt-dlp", "-U"],
            check=False,
            capture_output=True,
            text=True,
        )
    except FileNotFoundError:
        return

    output = "\n".join(
        part.strip()
        for part in (result.stdout, result.stderr)
        if part and part.strip()
    )
    if output:
        print(output)
    if result.returncode != 0:
        print("Warning: yt-dlp self-update failed; continuing with the installed version.")


def ensure_yt_dlp_available() -> None:
    """Fail with the original friendly message if yt-dlp is unavailable."""
    try:
        subprocess.run(
            ["yt-dlp", "--version"],
            check=True,
            capture_output=True,
            text=True,
        )
    except FileNotFoundError:
        print("Error: 'yt-dlp' is not found in PATH.")
        print("Install it with: pip install yt-dlp")
        sys.exit(1)
    except subprocess.CalledProcessError:
        print("Error: 'yt-dlp' is installed but returned an error.")
        sys.exit(1)

def create_output_directory():
    """Create a timestamped directory next to the script file."""
    script_dir = Path(__file__).resolve().parent
    timestamp = datetime.datetime.now().strftime("%Y-%m-%d_%H-%M-%S")
    output_dir = script_dir / timestamp
    output_dir.mkdir(exist_ok=True)
    return output_dir

def get_clipboard_lines():
    """Read clipboard text and return list of non-empty lines."""
    content = pyperclip.paste()
    if not content:
        print("Clipboard is empty.")
        return []
    lines = [line.strip() for line in content.splitlines() if line.strip()]
    return lines

def download_song(query, output_dir):
    """
    Download the first YouTube result for the given query.
    Prefers Opus audio directly; if not available, downloads AAC.
    No conversion is done.
    Tries multiple player clients to avoid SABR/403 errors.
    Returns True on success, False on failure.
    """
    maybe_update_yt_dlp()
    # Base command with format selection and retries
    base_cmd = [
        "yt-dlp",
        "-f", "bestaudio[acodec=opus]/bestaudio[acodec=aac]/bestaudio",
        "--no-playlist",
        "--output", str(output_dir / "%(title)s.%(ext)s"),
        "--retries", "3",
        "--fragment-retries", "3",
        "--user-agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36",
    ]

    # Optional: use cookies from a browser (uncomment and adjust browser name if desired)
    # base_cmd += ["--cookies-from-browser", "chrome"]

    # Try a list of player clients in a single yt-dlp run (yt-dlp will try them in order)
    # This often works better than separate attempts because yt-dlp handles the switching internally.
    cmd = base_cmd + [
        "--extractor-args", "youtube:player_client=web_embedded,android,tv,ios,default",
        f"ytsearch1:{query}"
    ]

    print(f"\nDownloading: {query}")
    try:
        result = subprocess.run(cmd, check=True, text=True, capture_output=True)
        print(f"Success: {query}")
        # Show which format was downloaded (optional)
        # print(result.stdout)
        return True
    except subprocess.CalledProcessError as e:
        print(f"Failed: {query}")
        if e.stderr:
            # Filter out long warnings to keep output concise
            error_lines = [line for line in e.stderr.splitlines() if line.startswith("ERROR")]
            if error_lines:
                print("Error details:")
                for line in error_lines:
                    print(line)
        return False

def main():
    songs = get_clipboard_lines()
    if not songs:
        print("No valid lines in clipboard. Each line should be 'Artist - Song'.")
        sys.exit(1)

    maybe_update_yt_dlp()
    ensure_yt_dlp_available()

    print(f"Found {len(songs)} song(s) in clipboard.")
    output_dir = create_output_directory()
    print(f"Saving to: {output_dir}")

    success_count = 0
    for song in songs:
        if download_song(song, output_dir):
            success_count += 1

    print(f"\nDone. {success_count}/{len(songs)} downloaded successfully.")
    print(f"Files are in: {output_dir}")

if __name__ == "__main__":
    main()
