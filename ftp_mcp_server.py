#!/usr/bin/env python3
"""FTP/SFTP MCP server — exposes remote file operations as Claude tools.
   Pure stdlib only (Python 3.6+).  Connection details come from env vars:
     FTP_HOST, FTP_PORT, FTP_USER, FTP_PASS, FTP_PROTOCOL, FTP_IDENTITY
"""

import os
import sys
import json
import io
import ftplib
import subprocess
import tempfile

# ── Config ────────────────────────────────────────────────────────────────────

HOST     = os.environ.get("FTP_HOST", "")
PORT     = int(os.environ.get("FTP_PORT", "21"))
USER     = os.environ.get("FTP_USER", "")
PASS     = os.environ.get("FTP_PASS", "")
PROTOCOL = os.environ.get("FTP_PROTOCOL", "ftp").lower()  # ftp | ftps | sftp
IDENTITY = os.environ.get("FTP_IDENTITY", "")             # SSH key for sftp

IS_SFTP  = (PROTOCOL == "sftp")

# ── MCP protocol helpers ──────────────────────────────────────────────────────

def send(obj):
    sys.stdout.write(json.dumps(obj) + "\n")
    sys.stdout.flush()

def send_result(req_id, result):
    send({"jsonrpc": "2.0", "id": req_id, "result": result})

def send_error(req_id, code, message):
    send({"jsonrpc": "2.0", "id": req_id,
          "error": {"code": code, "message": message}})

def tool_ok(text):
    return {"content": [{"type": "text", "text": str(text)}], "isError": False}

def tool_err(text):
    return {"content": [{"type": "text", "text": str(text)}], "isError": True}

# ── FTP / FTPS operations (stdlib ftplib) ─────────────────────────────────────

def _ftp_connect():
    if PROTOCOL == "ftps":
        ftp = ftplib.FTP_TLS()
        ftp.connect(HOST, PORT, timeout=15)
        ftp.login(USER, PASS)
        ftp.prot_p()
    else:
        ftp = ftplib.FTP()
        ftp.connect(HOST, PORT, timeout=15)
        ftp.login(USER, PASS)
    return ftp

def ftp_list(path):
    path = path or "/"
    ftp = _ftp_connect()
    lines = []
    try:
        ftp.dir(path, lines.append)
    finally:
        try: ftp.quit()
        except: pass
    return "\n".join(lines) if lines else "(empty directory)"

def ftp_read(path):
    ftp = _ftp_connect()
    buf = io.BytesIO()
    try:
        ftp.retrbinary("RETR " + path, buf.write)
    finally:
        try: ftp.quit()
        except: pass
    return buf.getvalue().decode("utf-8", errors="replace")

def ftp_write(path, content):
    ftp = _ftp_connect()
    buf = io.BytesIO(content.encode("utf-8"))
    try:
        ftp.storbinary("STOR " + path, buf)
    finally:
        try: ftp.quit()
        except: pass
    return f"Written: {path}"

def ftp_delete(path):
    ftp = _ftp_connect()
    try:
        ftp.delete(path)
    finally:
        try: ftp.quit()
        except: pass
    return f"Deleted: {path}"

def ftp_rename(from_path, to_path):
    ftp = _ftp_connect()
    try:
        ftp.rename(from_path, to_path)
    finally:
        try: ftp.quit()
        except: pass
    return f"Renamed: {from_path} → {to_path}"

def ftp_mkdir(path):
    ftp = _ftp_connect()
    try:
        ftp.mkd(path)
    finally:
        try: ftp.quit()
        except: pass
    return f"Created directory: {path}"

# ── SFTP operations (OpenSSH subprocess) ─────────────────────────────────────

def _ssh_base_args():
    """Common SSH args (no target)."""
    args = ["ssh", "-o", "StrictHostKeyChecking=no",
            "-o", "ConnectTimeout=15", "-o", "BatchMode=yes"]
    if PORT and PORT != 22:
        args += ["-p", str(PORT)]
    if IDENTITY:
        args += ["-i", IDENTITY]
    return args

def _sftp_base_args():
    args = ["sftp", "-o", "StrictHostKeyChecking=no",
            "-o", "ConnectTimeout=15"]
    if PORT and PORT != 22:
        args += ["-P", str(PORT)]
    if IDENTITY:
        args += ["-i", IDENTITY]
    return args

def _target():
    return f"{USER}@{HOST}" if USER else HOST

def _run(args, stdin_data=None):
    """Run a subprocess, optionally via sshpass when a password is set."""
    env = dict(os.environ)
    if PASS and not IDENTITY:
        # Use sshpass if available; otherwise rely on SSH agent / key auth.
        sshpass = _which("sshpass")
        if sshpass:
            args = [sshpass, "-p", PASS] + args
    r = subprocess.run(args, input=stdin_data, capture_output=True, timeout=30, env=env)
    return r.stdout.decode(errors="replace"), r.returncode

def _which(cmd):
    import shutil
    return shutil.which(cmd)

def sftp_list(path):
    path = path or "/"
    # Use ssh + ls -la for a rich listing.
    out, _ = _run(_ssh_base_args() + [_target(), f"ls -la {path}"])
    return out.strip() or "(empty directory)"

def sftp_read(path):
    with tempfile.NamedTemporaryFile(delete=False, suffix=".ftptmp") as f:
        local = f.name
    batch = f"get {path} {local}\n"
    _run(_sftp_base_args() + ["-b", "-", _target()],
         stdin_data=batch.encode())
    try:
        with open(local, "rb") as f:
            return f.read().decode(errors="replace")
    finally:
        try: os.unlink(local)
        except: pass

def sftp_write(path, content):
    with tempfile.NamedTemporaryFile(mode="wb", delete=False, suffix=".ftptmp") as f:
        f.write(content.encode("utf-8"))
        local = f.name
    batch = f"put {local} {path}\n"
    _run(_sftp_base_args() + ["-b", "-", _target()],
         stdin_data=batch.encode())
    try: os.unlink(local)
    except: pass
    return f"Written: {path}"

def sftp_delete(path):
    batch = f"rm {path}\n"
    _run(_sftp_base_args() + ["-b", "-", _target()],
         stdin_data=batch.encode())
    return f"Deleted: {path}"

def sftp_rename(from_path, to_path):
    batch = f"rename {from_path} {to_path}\n"
    _run(_sftp_base_args() + ["-b", "-", _target()],
         stdin_data=batch.encode())
    return f"Renamed: {from_path} → {to_path}"

def sftp_mkdir(path):
    batch = f"mkdir {path}\n"
    _run(_sftp_base_args() + ["-b", "-", _target()],
         stdin_data=batch.encode())
    return f"Created directory: {path}"

def ssh_exec(command):
    out, code = _run(_ssh_base_args() + [_target(), command])
    result = out.strip()
    if code != 0:
        result = f"[exit {code}]\n{result}" if result else f"[exit {code}]"
    return result or "(no output)"

# ── Dispatch ──────────────────────────────────────────────────────────────────

def dispatch_tool(name, args):
    path      = args.get("path", "/")
    from_path = args.get("from_path", "")
    to_path   = args.get("to_path", "")
    content   = args.get("content", "")

    if IS_SFTP:
        ops = {
            "ftp_list":   lambda: sftp_list(path),
            "ftp_read":   lambda: sftp_read(path),
            "ftp_write":  lambda: sftp_write(path, content),
            "ftp_delete": lambda: sftp_delete(path),
            "ftp_rename": lambda: sftp_rename(from_path, to_path),
            "ftp_mkdir":  lambda: sftp_mkdir(path),
            "ssh_exec":   lambda: ssh_exec(args.get("command", "")),
        }
    else:
        ops = {
            "ftp_list":   lambda: ftp_list(path),
            "ftp_read":   lambda: ftp_read(path),
            "ftp_write":  lambda: ftp_write(path, content),
            "ftp_delete": lambda: ftp_delete(path),
            "ftp_rename": lambda: ftp_rename(from_path, to_path),
            "ftp_mkdir":  lambda: ftp_mkdir(path),
        }

    fn = ops.get(name)
    if fn is None:
        return f"Unknown tool: {name}"
    return fn()

# ── Tool definitions ──────────────────────────────────────────────────────────

TOOLS = [
    {
        "name": "ftp_list",
        "description": "List files and directories at a path on the remote server.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "path": {"type": "string",
                         "description": "Remote directory path to list (default: /)"}
            },
            "required": []
        }
    },
    {
        "name": "ftp_read",
        "description": "Read and return the text content of a remote file.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "path": {"type": "string", "description": "Remote file path to read"}
            },
            "required": ["path"]
        }
    },
    {
        "name": "ftp_write",
        "description": "Write text content to a remote file, creating or overwriting it.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "path": {"type": "string", "description": "Remote file path to write"},
                "content": {"type": "string",
                            "description": "File content to write (UTF-8 text)"}
            },
            "required": ["path", "content"]
        }
    },
    {
        "name": "ftp_delete",
        "description": "Delete a remote file.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "path": {"type": "string", "description": "Remote file path to delete"}
            },
            "required": ["path"]
        }
    },
    {
        "name": "ftp_rename",
        "description": "Rename or move a remote file or directory.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "from_path": {"type": "string", "description": "Current remote path"},
                "to_path":   {"type": "string", "description": "New remote path"}
            },
            "required": ["from_path", "to_path"]
        }
    },
    {
        "name": "ftp_mkdir",
        "description": "Create a directory on the remote server.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "path": {"type": "string",
                         "description": "Remote directory path to create"}
            },
            "required": ["path"]
        }
    },
    {
        "name": "ssh_exec",
        "description": "Run a shell command on the remote server via SSH. "
                       "Available for SFTP connections only. "
                       "Returns stdout/stderr and exit code.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "command": {"type": "string",
                            "description": "Shell command to execute on the remote server"}
            },
            "required": ["command"]
        }
    },
]

# ── Main loop ─────────────────────────────────────────────────────────────────

def main():
    for raw_line in sys.stdin:
        raw_line = raw_line.strip()
        if not raw_line:
            continue
        try:
            req = json.loads(raw_line)
        except json.JSONDecodeError:
            continue

        req_id = req.get("id")
        method = req.get("method", "")

        if method == "initialize":
            send_result(req_id, {
                "protocolVersion": "2024-11-05",
                "capabilities": {"tools": {}},
                "serverInfo": {"name": "ftp-tools", "version": "1.0"}
            })
        elif method in ("notifications/initialized", "notifications/cancelled"):
            pass   # one-way, no reply
        elif method == "tools/list":
            send_result(req_id, {"tools": TOOLS})
        elif method == "tools/call":
            params    = req.get("params", {})
            tool_name = params.get("name", "")
            arguments = params.get("arguments", {})
            try:
                result_text = dispatch_tool(tool_name, arguments)
                send_result(req_id, tool_ok(result_text))
            except Exception as e:
                send_result(req_id, tool_err(f"Error: {e}"))
        elif method == "ping":
            send_result(req_id, {})
        else:
            if req_id is not None:
                send_error(req_id, -32601, f"Method not found: {method}")

if __name__ == "__main__":
    main()
