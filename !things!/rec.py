#!/usr/bin/env python3
"""
Screen Region Recorder for Windows

Default hotkey:
    Ctrl + Alt + R

Behavior:
    1. Press the hotkey.
    2. Drag a rectangle on the screen.
    3. Release the mouse to begin recording that region.
    4. Press the hotkey again to stop and save an MP4 in Downloads.
    5. The saved MP4 is copied to the clipboard as a file.
    6. Press Esc while selecting to cancel.

Requires:
    - Windows 10/11
    - Python 3.10+
    - FFmpeg available on PATH
"""

from __future__ import annotations

import ctypes
from ctypes import wintypes
import datetime as dt
import os
from pathlib import Path
import queue
import shutil
import subprocess
import sys
import threading
import time
import tkinter as tk
from tkinter import messagebox
from typing import Optional


# ----------------------------- Configuration -----------------------------

FPS = 60
HOTKEY_MODIFIERS = 0x0001 | 0x0002  # MOD_ALT | MOD_CONTROL
HOTKEY_VK = ord("R")
HOTKEY_TEXT = "Ctrl+Alt+R"

VIDEO_ENCODER = "libx264"
VIDEO_PRESET = "ultrafast"
CRF = 18
CAPTURE_MOUSE = True

# Set to True to automatically open the output folder after recording.
OPEN_FOLDER_AFTER_SAVE = False


# ------------------------------- Win32 API -------------------------------

user32 = ctypes.windll.user32
kernel32 = ctypes.windll.kernel32
shell32 = ctypes.windll.shell32
ole32 = ctypes.windll.ole32

WM_HOTKEY = 0x0312
WM_QUIT = 0x0012
HOTKEY_ID = 1

SM_XVIRTUALSCREEN = 76
SM_YVIRTUALSCREEN = 77
SM_CXVIRTUALSCREEN = 78
SM_CYVIRTUALSCREEN = 79

PROCESS_PER_MONITOR_DPI_AWARE = 2

CF_HDROP = 15
GMEM_MOVEABLE = 0x0002
GMEM_ZEROINIT = 0x0040


class GUID(ctypes.Structure):
    _fields_ = [
        ("Data1", wintypes.DWORD),
        ("Data2", wintypes.WORD),
        ("Data3", wintypes.WORD),
        ("Data4", ctypes.c_ubyte * 8),
    ]


class DROPFILES(ctypes.Structure):
    _fields_ = [
        ("pFiles", wintypes.DWORD),
        ("pt", wintypes.POINT),
        ("fNC", wintypes.BOOL),
        ("fWide", wintypes.BOOL),
    ]


FOLDERID_DOWNLOADS = GUID(
    0x374DE290,
    0x123F,
    0x4565,
    (ctypes.c_ubyte * 8)(0x91, 0x64, 0x39, 0xC4, 0x92, 0x5E, 0x46, 0x7B),
)

shell32.SHGetKnownFolderPath.argtypes = [
    ctypes.POINTER(GUID),
    wintypes.DWORD,
    wintypes.HANDLE,
    ctypes.POINTER(ctypes.c_wchar_p),
]
shell32.SHGetKnownFolderPath.restype = ctypes.c_long
ole32.CoTaskMemFree.argtypes = [ctypes.c_void_p]
ole32.CoTaskMemFree.restype = None

kernel32.GlobalAlloc.argtypes = [wintypes.UINT, ctypes.c_size_t]
kernel32.GlobalAlloc.restype = wintypes.HGLOBAL
kernel32.GlobalLock.argtypes = [wintypes.HGLOBAL]
kernel32.GlobalLock.restype = ctypes.c_void_p
kernel32.GlobalUnlock.argtypes = [wintypes.HGLOBAL]
kernel32.GlobalUnlock.restype = wintypes.BOOL
kernel32.GlobalFree.argtypes = [wintypes.HGLOBAL]
kernel32.GlobalFree.restype = wintypes.HGLOBAL

user32.OpenClipboard.argtypes = [wintypes.HWND]
user32.OpenClipboard.restype = wintypes.BOOL
user32.EmptyClipboard.argtypes = []
user32.EmptyClipboard.restype = wintypes.BOOL
user32.SetClipboardData.argtypes = [wintypes.UINT, wintypes.HANDLE]
user32.SetClipboardData.restype = wintypes.HANDLE
user32.CloseClipboard.argtypes = []
user32.CloseClipboard.restype = wintypes.BOOL


def get_downloads_directory() -> Path:
    """Return the Downloads folder registered with Windows Explorer."""
    raw_path = ctypes.c_wchar_p()
    result = shell32.SHGetKnownFolderPath(
        ctypes.byref(FOLDERID_DOWNLOADS),
        0,
        None,
        ctypes.byref(raw_path),
    )
    if result < 0:
        raise OSError(
            "SHGetKnownFolderPath(FOLDERID_Downloads) failed: "
            f"0x{result & 0xFFFFFFFF:08X}"
        )

    try:
        return Path(raw_path.value)
    finally:
        ole32.CoTaskMemFree(ctypes.cast(raw_path, ctypes.c_void_p))


def copy_file_to_clipboard(path: Path) -> None:
    """Put a file on the Windows clipboard as a pasteable file item."""
    path = path.resolve()
    file_list = (str(path) + "\0\0").encode("utf-16le")
    dropfiles = DROPFILES(
        pFiles=ctypes.sizeof(DROPFILES),
        pt=wintypes.POINT(0, 0),
        fNC=False,
        fWide=True,
    )

    memory_handle = kernel32.GlobalAlloc(
        GMEM_MOVEABLE | GMEM_ZEROINIT,
        ctypes.sizeof(DROPFILES) + len(file_list),
    )
    if not memory_handle:
        raise ctypes.WinError()

    clipboard_owns_memory = False
    try:
        memory = kernel32.GlobalLock(memory_handle)
        if not memory:
            raise ctypes.WinError()
        try:
            ctypes.memmove(memory, ctypes.byref(dropfiles), ctypes.sizeof(dropfiles))
            ctypes.memmove(memory + ctypes.sizeof(dropfiles), file_list, len(file_list))
        finally:
            kernel32.GlobalUnlock(memory_handle)

        for _attempt in range(20):
            if user32.OpenClipboard(None):
                break
            time.sleep(0.05)
        else:
            raise OSError("Could not open the Windows clipboard.")

        try:
            if not user32.EmptyClipboard():
                raise ctypes.WinError()
            if not user32.SetClipboardData(CF_HDROP, memory_handle):
                raise ctypes.WinError()
            clipboard_owns_memory = True
        finally:
            user32.CloseClipboard()
    finally:
        if not clipboard_owns_memory:
            kernel32.GlobalFree(memory_handle)


def enable_dpi_awareness() -> None:
    """Keep Tk coordinates aligned with physical capture pixels."""
    try:
        ctypes.windll.shcore.SetProcessDpiAwareness(PROCESS_PER_MONITOR_DPI_AWARE)
    except Exception:
        try:
            user32.SetProcessDPIAware()
        except Exception:
            pass


def virtual_screen_bounds() -> tuple[int, int, int, int]:
    x = user32.GetSystemMetrics(SM_XVIRTUALSCREEN)
    y = user32.GetSystemMetrics(SM_YVIRTUALSCREEN)
    w = user32.GetSystemMetrics(SM_CXVIRTUALSCREEN)
    h = user32.GetSystemMetrics(SM_CYVIRTUALSCREEN)
    return x, y, w, h


# --------------------------- Global hotkey thread -------------------------

class HotkeyListener(threading.Thread):
    def __init__(self, event_queue: queue.Queue[str]) -> None:
        super().__init__(name="GlobalHotkeyListener", daemon=True)
        self.event_queue = event_queue
        self.thread_id: Optional[int] = None
        self.registration_error: Optional[str] = None

    def run(self) -> None:
        self.thread_id = kernel32.GetCurrentThreadId()

        if not user32.RegisterHotKey(
            None,
            HOTKEY_ID,
            HOTKEY_MODIFIERS,
            HOTKEY_VK,
        ):
            error_code = ctypes.get_last_error()
            self.registration_error = (
                f"Could not register {HOTKEY_TEXT}. "
                f"Another application may already use it. "
                f"Windows error: {error_code}"
            )
            self.event_queue.put("hotkey_error")
            return

        msg = wintypes.MSG()
        try:
            while user32.GetMessageW(ctypes.byref(msg), None, 0, 0) > 0:
                if msg.message == WM_HOTKEY and msg.wParam == HOTKEY_ID:
                    self.event_queue.put("hotkey")
        finally:
            user32.UnregisterHotKey(None, HOTKEY_ID)

    def stop(self) -> None:
        if self.thread_id is not None:
            user32.PostThreadMessageW(self.thread_id, WM_QUIT, 0, 0)


# ------------------------------ Selection UI -----------------------------

class SelectionOverlay:
    def __init__(
        self,
        root: tk.Tk,
        on_selected,
        on_cancelled,
    ) -> None:
        self.root = root
        self.on_selected = on_selected
        self.on_cancelled = on_cancelled

        self.screen_x, self.screen_y, self.screen_w, self.screen_h = (
            virtual_screen_bounds()
        )

        self.window = tk.Toplevel(root)
        self.window.withdraw()
        self.window.overrideredirect(True)
        self.window.attributes("-topmost", True)
        self.window.attributes("-alpha", 0.28)
        self.window.configure(bg="black")
        self.window.geometry(
            f"{self.screen_w}x{self.screen_h}"
            f"{self.screen_x:+d}{self.screen_y:+d}"
        )

        self.canvas = tk.Canvas(
            self.window,
            bg="black",
            highlightthickness=0,
            cursor="crosshair",
        )
        self.canvas.pack(fill="both", expand=True)

        self.start_canvas: Optional[tuple[int, int]] = None
        self.start_screen: Optional[tuple[int, int]] = None
        self.rectangle_id: Optional[int] = None
        self.size_text_id: Optional[int] = None
        self.active = False

        self.canvas.bind("<ButtonPress-1>", self._mouse_down)
        self.canvas.bind("<B1-Motion>", self._mouse_move)
        self.canvas.bind("<ButtonRelease-1>", self._mouse_up)
        self.window.bind("<Escape>", lambda _event: self.cancel())

    def show(self) -> None:
        if self.active:
            return

        self.active = True
        self.start_canvas = None
        self.start_screen = None
        self.rectangle_id = None
        self.size_text_id = None
        self.canvas.delete("all")

        self.window.deiconify()
        self.window.lift()
        self.window.focus_force()
        self.window.grab_set_global()

        self.canvas.create_text(
            self.screen_w // 2,
            36,
            text=(
                "Drag to select a recording area"
                f"    •    Esc cancels    •    {HOTKEY_TEXT} cancels"
            ),
            fill="white",
            font=("Segoe UI", 15, "bold"),
        )

    def cancel(self) -> None:
        if not self.active:
            return

        self.active = False
        try:
            self.window.grab_release()
        except tk.TclError:
            pass
        self.window.withdraw()
        self.on_cancelled()

    def _mouse_down(self, event: tk.Event) -> None:
        self.start_canvas = (event.x, event.y)
        self.start_screen = (event.x_root, event.y_root)

        if self.rectangle_id is not None:
            self.canvas.delete(self.rectangle_id)
        if self.size_text_id is not None:
            self.canvas.delete(self.size_text_id)

        self.rectangle_id = self.canvas.create_rectangle(
            event.x,
            event.y,
            event.x,
            event.y,
            outline="#00d8ff",
            width=3,
        )

    def _mouse_move(self, event: tk.Event) -> None:
        if self.start_canvas is None or self.rectangle_id is None:
            return

        x0, y0 = self.start_canvas
        self.canvas.coords(self.rectangle_id, x0, y0, event.x, event.y)

        width = abs(event.x_root - self.start_screen[0])
        height = abs(event.y_root - self.start_screen[1])
        label = f"{width} × {height}"

        if self.size_text_id is None:
            self.size_text_id = self.canvas.create_text(
                event.x + 12,
                event.y + 12,
                anchor="nw",
                text=label,
                fill="white",
                font=("Segoe UI", 11, "bold"),
            )
        else:
            self.canvas.coords(self.size_text_id, event.x + 12, event.y + 12)
            self.canvas.itemconfigure(self.size_text_id, text=label)

    def _mouse_up(self, event: tk.Event) -> None:
        if self.start_screen is None:
            return

        x0, y0 = self.start_screen
        x1, y1 = event.x_root, event.y_root

        left = min(x0, x1)
        top = min(y0, y1)
        width = abs(x1 - x0)
        height = abs(y1 - y0)

        # H.264 with yuv420p requires even dimensions.
        width -= width % 2
        height -= height % 2

        if width < 16 or height < 16:
            messagebox.showwarning(
                "Selection too small",
                "Select an area at least 16 × 16 pixels.",
                parent=self.window,
            )
            return

        self.active = False
        try:
            self.window.grab_release()
        except tk.TclError:
            pass
        self.window.withdraw()

        # Let Windows repaint the selected area before FFmpeg starts.
        self.root.after(180, lambda: self.on_selected(left, top, width, height))


# ---------------------------- Recording indicator ------------------------

class RecordingIndicator:
    def __init__(self, root: tk.Tk) -> None:
        self.root = root
        self.window = tk.Toplevel(root)
        self.window.withdraw()
        self.window.overrideredirect(True)
        self.window.attributes("-topmost", True)
        self.window.configure(bg="#b00020")

        self.label = tk.Label(
            self.window,
            text="● REC",
            bg="#b00020",
            fg="white",
            padx=10,
            pady=5,
            font=("Segoe UI", 10, "bold"),
        )
        self.label.pack()

        self.started_at: Optional[dt.datetime] = None
        self.timer_job: Optional[str] = None

    def show_near(self, x: int, y: int, width: int) -> None:
        self.started_at = dt.datetime.now()
        self.window.update_idletasks()

        indicator_w = self.window.winfo_reqwidth()
        indicator_h = self.window.winfo_reqheight()

        screen_x, screen_y, screen_w, screen_h = virtual_screen_bounds()
        preferred_x = x + width - indicator_w
        preferred_y = y - indicator_h - 8

        if preferred_y < screen_y:
            preferred_y = y + 8
        preferred_x = max(screen_x, min(preferred_x, screen_x + screen_w - indicator_w))

        self.window.geometry(f"+{preferred_x}+{preferred_y}")
        self.window.deiconify()
        self.window.lift()
        self._tick()

    def _tick(self) -> None:
        if self.started_at is None:
            return
        elapsed = dt.datetime.now() - self.started_at
        total_seconds = max(0, int(elapsed.total_seconds()))
        minutes, seconds = divmod(total_seconds, 60)
        hours, minutes = divmod(minutes, 60)

        if hours:
            time_text = f"{hours:02d}:{minutes:02d}:{seconds:02d}"
        else:
            time_text = f"{minutes:02d}:{seconds:02d}"

        self.label.configure(text=f"● REC  {time_text}   {HOTKEY_TEXT} to stop")
        self.timer_job = self.root.after(250, self._tick)

    def hide(self) -> None:
        if self.timer_job is not None:
            self.root.after_cancel(self.timer_job)
            self.timer_job = None
        self.started_at = None
        self.window.withdraw()


# ------------------------------ Main program -----------------------------

class ScreenRegionRecorder:
    def __init__(self) -> None:
        enable_dpi_awareness()

        self.root = tk.Tk()
        self.root.withdraw()
        self.root.title("Screen Region Recorder")

        self.events: queue.Queue[str] = queue.Queue()
        self.hotkey_listener = HotkeyListener(self.events)

        self.overlay = SelectionOverlay(
            self.root,
            on_selected=self.start_recording,
            on_cancelled=self._selection_cancelled,
        )
        self.indicator = RecordingIndicator(self.root)

        self.state = "idle"  # idle | selecting | recording | stopping
        self.ffmpeg_process: Optional[subprocess.Popen] = None
        self.ffmpeg_log_handle = None
        self.ffmpeg_log_path: Optional[Path] = None
        self.output_path: Optional[Path] = None

        self.root.protocol("WM_DELETE_WINDOW", self.shutdown)
        self.root.after(50, self._process_events)
        self.root.after(500, self._check_ffmpeg)

    def run(self) -> None:
        if os.name != "nt":
            messagebox.showerror(
                "Windows required",
                "This tool currently supports Windows 10 and Windows 11.",
            )
            return

        if shutil.which("ffmpeg") is None:
            messagebox.showerror(
                "FFmpeg not found",
                "FFmpeg must be installed and available on PATH.\n\n"
                "Install it with:\n"
                "winget install Gyan.FFmpeg\n\n"
                "Then restart this tool.",
            )
            return

        self.hotkey_listener.start()
        self.root.mainloop()

    def _process_events(self) -> None:
        try:
            while True:
                event = self.events.get_nowait()

                if event == "hotkey_error":
                    messagebox.showerror(
                        "Hotkey unavailable",
                        self.hotkey_listener.registration_error
                        or f"Could not register {HOTKEY_TEXT}.",
                    )
                    self.shutdown()
                    return

                if event == "hotkey":
                    self._handle_hotkey()
                elif event.startswith("recording_finished:"):
                    return_code = int(event.split(":", 1)[1])
                    self._recording_finished(return_code)
        except queue.Empty:
            pass

        self.root.after(50, self._process_events)

    def _handle_hotkey(self) -> None:
        if self.state == "idle":
            self.state = "selecting"
            self.overlay.show()
        elif self.state == "selecting":
            self.overlay.cancel()
        elif self.state == "recording":
            self.stop_recording()

    def _selection_cancelled(self) -> None:
        self.state = "idle"

    def _build_output_path(self) -> Path:
        downloads_directory = get_downloads_directory()
        downloads_directory.mkdir(parents=True, exist_ok=True)
        timestamp = dt.datetime.now().strftime("%Y-%m-%d_%H-%M-%S")
        candidate = downloads_directory / f"Screen recording {timestamp}.mp4"

        counter = 2
        while candidate.exists():
            candidate = downloads_directory / (
                f"Screen recording {timestamp} ({counter}).mp4"
            )
            counter += 1
        return candidate

    def start_recording(self, x: int, y: int, width: int, height: int) -> None:
        if self.state != "selecting":
            return

        try:
            self.output_path = self._build_output_path()
        except Exception as exc:
            self.state = "idle"
            messagebox.showerror(
                "Could not access Downloads",
                str(exc),
            )
            return

        self.ffmpeg_log_path = self.output_path.with_suffix(".ffmpeg.log")
        self.ffmpeg_log_handle = self.ffmpeg_log_path.open(
            "w",
            encoding="utf-8",
            errors="replace",
        )

        command = [
            "ffmpeg",
            "-hide_banner",
            "-loglevel",
            "warning",
            "-y",
            "-f",
            "gdigrab",
            "-draw_mouse",
            "1" if CAPTURE_MOUSE else "0",
            "-framerate",
            str(FPS),
            "-offset_x",
            str(x),
            "-offset_y",
            str(y),
            "-video_size",
            f"{width}x{height}",
            "-i",
            "desktop",
            "-an",
            "-c:v",
            VIDEO_ENCODER,
            "-preset",
            VIDEO_PRESET,
            "-crf",
            str(CRF),
            "-pix_fmt",
            "yuv420p",
            "-movflags",
            "+faststart",
            str(self.output_path),
        ]

        creation_flags = getattr(subprocess, "CREATE_NO_WINDOW", 0)

        try:
            self.ffmpeg_process = subprocess.Popen(
                command,
                stdin=subprocess.PIPE,
                stdout=subprocess.DEVNULL,
                stderr=self.ffmpeg_log_handle,
                creationflags=creation_flags,
            )
        except Exception as exc:
            self._close_ffmpeg_log()
            self.state = "idle"
            messagebox.showerror(
                "Could not start recording",
                f"{exc}",
            )
            return

        self.state = "recording"
        self.indicator.show_near(x, y, width)

    def stop_recording(self) -> None:
        if self.state != "recording" or self.ffmpeg_process is None:
            return

        self.state = "stopping"
        self.indicator.hide()

        process = self.ffmpeg_process

        try:
            if process.stdin is not None:
                process.stdin.write(b"q\n")
                process.stdin.flush()
        except (BrokenPipeError, OSError):
            pass

        threading.Thread(
            target=self._finish_stopping,
            args=(process,),
            name="FFmpegStopper",
            daemon=True,
        ).start()

    def _finish_stopping(self, process: subprocess.Popen) -> None:
        try:
            return_code = process.wait(timeout=12)
        except subprocess.TimeoutExpired:
            process.terminate()
            try:
                return_code = process.wait(timeout=4)
            except subprocess.TimeoutExpired:
                process.kill()
                return_code = process.wait()

        self.events.put(f"recording_finished:{return_code}")

    def _check_ffmpeg(self) -> None:
        if (
            self.state == "recording"
            and self.ffmpeg_process is not None
            and self.ffmpeg_process.poll() is not None
        ):
            self._recording_finished(self.ffmpeg_process.returncode)

        self.root.after(500, self._check_ffmpeg)

    def _recording_finished(self, return_code: int) -> None:
        if self.state == "idle":
            return

        self.indicator.hide()
        self._close_ffmpeg_log()

        output_path = self.output_path
        log_path = self.ffmpeg_log_path

        self.ffmpeg_process = None
        self.output_path = None
        self.ffmpeg_log_path = None
        self.state = "idle"

        success = (
            return_code == 0
            and output_path is not None
            and output_path.exists()
            and output_path.stat().st_size > 0
        )

        if success:
            if log_path is not None:
                try:
                    log_path.unlink(missing_ok=True)
                except OSError:
                    pass

            clipboard_error = None
            try:
                copy_file_to_clipboard(output_path)
            except Exception as exc:
                clipboard_error = str(exc)

            if OPEN_FOLDER_AFTER_SAVE:
                os.startfile(str(output_path.parent))

            if clipboard_error is None:
                messagebox.showinfo(
                    "Recording saved",
                    f"Saved to Downloads and copied as a file:\n{output_path}",
                )
            else:
                messagebox.showwarning(
                    "Recording saved",
                    f"Saved to Downloads:\n{output_path}\n\n"
                    f"Could not copy the file to the clipboard:\n{clipboard_error}",
                )
        else:
            details = ""
            if log_path is not None and log_path.exists():
                details = f"\n\nFFmpeg log:\n{log_path}"

            messagebox.showerror(
                "Recording failed",
                f"FFmpeg exited with code {return_code}.{details}",
            )

    def _close_ffmpeg_log(self) -> None:
        if self.ffmpeg_log_handle is not None:
            try:
                self.ffmpeg_log_handle.close()
            except Exception:
                pass
            self.ffmpeg_log_handle = None

    def shutdown(self) -> None:
        if self.state == "recording":
            self.stop_recording()
            return

        self.hotkey_listener.stop()
        self._close_ffmpeg_log()
        try:
            self.root.destroy()
        except tk.TclError:
            pass


def main() -> int:
    app = ScreenRegionRecorder()
    app.run()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
