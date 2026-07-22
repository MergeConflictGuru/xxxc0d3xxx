# Automato

Automato is a local VS Code extension for a simple copy-to-AI / copy-back-patch workflow.

## Copy the active file

Run **Automato: Copy Active File for AI** or press `Ctrl+Alt+Shift+C`.

Automato creates a temporary copy containing a first-line comment such as:

```ts
// FILE PATH: src/extension.ts
```

It discovers the comment syntax from the active language configuration contributed to VS Code. The visible editor and original file are never edited. On Windows, the clipboard contains both:

1. the temporary file (`CF_HDROP`), offered first;
2. the same contents as Unicode text fallback.

A file-aware browser can attach the file; a text-only receiver can paste the text. If the language has no discoverable comment syntax, Automato leaves the contents unchanged and encodes the path into the temporary filename.

The active editor does not need to be saved. Automato copies the current in-memory text, including unsaved edits, without changing the visible editor or writing those edits to disk. If a returned patch targets a dirty editor, Automato validates it against the live buffer and saves that buffer only after you approve the patch.

## Clipboard patch watcher

On Windows, Automato listens for native clipboard-change notifications and keeps a polling watchdog as a fallback. Copying the same patch text twice still creates a new event. Clipboard reads are retried while another application temporarily owns the clipboard, and clipboard changes received while a patch confirmation is pending are queued rather than discarded. Ordinary clipboard content does nothing.

When copied text contains either a full Git patch or a plain unified diff beginning with `---` / `+++`, Automato:

1. removes Markdown fences and mail-style patch trailers;
2. ignores the supplied hunk line positions;
3. strips conventional `a/` and `b/` diff prefixes, then matches each hunk's complete old side (context plus deleted lines) against the named repository file;
4. if the supplied path is incomplete, searches files with the same basename and chooses only a unique best path whose hunks match;
5. requires exactly one match per hunk, then repairs the path, hunk line numbers, and old-side whitespace;
6. validates the result with `git apply --check`;
7. shows a non-modal VS Code notification listing changed, added, and deleted files;
8. applies only after **Apply** is selected.

The notification does not bring VS Code to the foreground. If VS Code is behind another application, the decision remains waiting in VS Code until you return.

## Supported patches

- Modifications of existing UTF-8 text files
- New UTF-8 text files
- Deleted UTF-8 text files
- Multiple files and multiple non-overlapping hunks
- Full Git patches and plain `---` / `+++` unified diffs
- Conventional `a/` and `b/` path prefixes
- Standard or numberless hunk headers (`@@`)

Automato refuses ambiguous context, paths outside the repository, binary patches, submodules, renames, and copies. A rename can be represented as deletion plus addition.

## Whitespace matching

`automato.whitespaceMatching` defaults to `auto`:

- JavaScript, TypeScript, C-family languages, Java, Go, Rust, HTML, CSS, JSON, XML, SQL, and similar formats ignore leading indentation and trailing horizontal whitespace while locating old text.
- Python, YAML, Makefiles, and unknown formats use exact whitespace.
- Internal whitespace is always significant.

The repaired patch uses the file's actual old-side whitespace before Git validates it.

## Commands

- **Automato: Copy Active File for AI**
- **Automato: Toggle Clipboard Patch Watcher**
- **Automato: Inspect Clipboard for Patch Now**

The status bar shows whether the watcher is active and whether a patch decision is pending.

## Local-only limitations

This build supports local Windows filesystem repositories. Multi-format file-plus-text copying uses bundled PowerShell and Windows Forms. On other operating systems it falls back to text-only copying.

## Patch detection diagnostics

Automato watches the clipboard and every existing Windows Downloads location it can resolve, including the Windows Known Folder registry path and common OneDrive Downloads paths. The Output channel records activation, the exact watched directories, initial candidate counts, every changed candidate file, patch extraction, cross-window routing, repository matching, claims, and failures.

Use **Automato: Scan Downloads for Patches Now** to inspect all existing `.patch`, `.diff`, and `.txt` files immediately. Use **Automato: Show Diagnostics** to open the Automato Output channel. A patch that cannot be prepared by any repository in the focused VS Code window now produces a visible error after other windows have had time to claim it.
