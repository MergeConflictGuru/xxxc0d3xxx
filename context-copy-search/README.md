# Context Copy Search

Two actions only:

1. **Context Copy Search: Preview Matches** — search and show results in **Explorer → Context Search Results**.
2. **Context Copy Search: Search and Copy Contexts** — ask for the search text, run the search, then immediately copy the containing context blocks to the clipboard.

## Use

```powershell
npm install
npm test
```

Then press `F5` in VS Code.

In the Extension Development Host, open the real target workspace, then run one of:

- **Context Copy Search: Preview Matches**
- **Context Copy Search: Search and Copy Contexts**

If text is selected in the editor, the search box is prefilled with that selected text.

## Context copy behavior

- Uses the v0.6 ripgrep-based search path.
- Plain text searches are case-insensitive by default.
- `.js`, `.mjs`, `.md`, etc. are searched when include/exclude settings are empty.
- If a match is inside a method of a class, the copied context is the enclosing class/struct/interface/enum when VS Code symbols can identify it.
- If symbols are unavailable, brace-language fallback also tries to find the enclosing class-like block before falling back to the nearest brace block.
- Every copied context block includes source comments and match markers.
