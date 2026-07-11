# LingCode FTP — Windows

A Windows port of the macOS **LingCode FTP** app: a native FTP / FTPS / SFTP
client (C# / WinForms) with a built-in **Claude chat pane** and an FTP MCP
server (`ftp_mcp_server.py`).

It mirrors the Mac app's design:

- **Launch window** — a grid of saved "server cards" (＋ to add, ⚙ Settings).
- **Settings** — edit a server: name, protocol (ftp / ftps / sftp), host, port,
  initial path, username, password, and an SSH key (SFTP). Live-saves; includes
  **Test Connection**.
- **Server window** — three panes:
  1. **File browser** — a lazy-loading remote tree. Expand to list in place,
     double-click a folder to navigate into it, right-click for
     New Folder / Rename / Delete / Download / Refresh, and **drag files in from
     Explorer to upload**.
  2. **Editor** — opens a file (download), syntax-highlights it, **Ctrl+S**
     uploads it back.
  3. **Claude chat** — drives your local `claude` CLI in print mode (uses your
     Claude subscription, not an API key) with an MCP server that gives Claude
     tools to read/write/delete/rename files on the remote host. Renders
     `ask_user` questions as clickable buttons, shows tool steps and file-edit
     diffs, a thinking-steps toggle (Ctrl+Shift+T), Stop (Ctrl+.), and a model
     selector (default / opus / sonnet / haiku).

## How it works

Like the Mac original, the FTP engine doesn't implement the protocols itself —
it shells out to tools already on Windows 10/11:

- **`curl.exe`** for `ftp` / `ftps`
- **OpenSSH `sftp.exe`** for `sftp`
- **`claude.exe`** (Claude Code) for the chat pane
- **`python`** to run `ftp_mcp_server.py`

## Build

No .NET SDK required — it builds with the .NET Framework compiler bundled with
Windows:

```powershell
powershell -ExecutionPolicy Bypass -File .\build.ps1
```

This produces `LingCodeFTP.exe`. Run it directly. Keep `ftp_mcp_server.py` next
to the `.exe` (the app looks for it there to wire up Claude's tools).

## Notes / differences from the Mac build

- **SFTP with a password**: Windows OpenSSH has no `sshpass`, so batch-mode
  `sftp.exe` can't supply a password non-interactively. Use an **SSH key**
  (set it in Settings) for SFTP. Key-based SFTP works fully; ftp/ftps work with
  username+password as usual.
- The Mac app's bundled C syntax-highlighting engine is replaced by a
  lightweight tokenizer covering the common languages (C-family, Python/shell,
  JSON, CSS, HTML) with the same Xcode "Default (Light)" palette.
- Settings are stored in `%APPDATA%\LingCodeFTP\` (`accounts.json`,
  `settings.json`) instead of macOS `NSUserDefaults`.
- Drag-*out* to Explorer is replaced by a right-click **Download…** item.

## A quick test server

Rebex hosts a public read-only demo server you can add in Settings:

- Protocol `ftp`, Host `test.rebex.net`, Port `21`, User `demo`, Pass `password`
