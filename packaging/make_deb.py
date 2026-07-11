#!/usr/bin/env python3
"""Build a .deb for the self-contained LingCode FTP Avalonia app.

A .deb is an `ar` archive of exactly three members, in order:
  debian-binary, control.tar.gz, data.tar.gz
We assemble it by hand (no dpkg-deb needed). File modes/ownership are set
explicitly via tarfile, so it works even when built on Windows.

Usage:
  make_deb.py --publish <dir> --arch amd64 --version 1.1.0 --out out.deb \
              --logo logo.svg
"""
import argparse, gzip, hashlib, io, os, tarfile, time

INSTALL = "opt/lingcodeftp"
FIXED_MTIME = 1700000000  # deterministic

def ti(name, mode, size=0, isdir=False, islnk=False, linkname=""):
    t = tarfile.TarInfo(name)
    t.mode = mode
    t.uid = t.gid = 0
    t.uname = t.gname = "root"
    t.mtime = FIXED_MTIME
    if isdir:
        t.type = tarfile.DIRTYPE
    elif islnk:
        t.type = tarfile.SYMTYPE
        t.linkname = linkname
    else:
        t.size = size
    return t

def make_tar_gz(entries):
    """entries: list of (tarinfo, bytes|None). Returns gzipped tar bytes."""
    raw = io.BytesIO()
    with tarfile.open(fileobj=raw, mode="w", format=tarfile.GNU_FORMAT) as tar:
        for info, data in entries:
            if data is None:
                tar.addfile(info)
            else:
                info.size = len(data)
                tar.addfile(info, io.BytesIO(data))
    # gzip with fixed mtime for reproducibility
    gz = io.BytesIO()
    with gzip.GzipFile(fileobj=gz, mode="wb", mtime=0) as g:
        g.write(raw.getvalue())
    return gz.getvalue()

def ar_member(name, data):
    hdr = "{:<16}{:<12}{:<6}{:<6}{:<8}{:<10}`\n".format(
        name, 0, 0, 0, 100644, len(data)).encode()
    assert len(hdr) == 60
    out = hdr + data
    if len(data) % 2 == 1:
        out += b"\n"
    return out

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--publish", required=True)
    ap.add_argument("--arch", required=True)      # amd64 | arm64
    ap.add_argument("--version", required=True)
    ap.add_argument("--out", required=True)
    ap.add_argument("--logo", default=None)
    args = ap.parse_args()

    payload = [
        ("LingCodeFTP", 0o755),
        ("ftp_mcp_server.py", 0o755),
        ("libSkiaSharp.so", 0o644),
        ("libHarfBuzzSharp.so", 0o644),
        ("RUN-LINUX.txt", 0o644),
    ]

    wrapper = ("#!/bin/sh\n"
               "# Invariant globalization avoids a hard libicu dependency.\n"
               "export DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1\n"
               'exec /opt/lingcodeftp/LingCodeFTP "$@"\n').encode()

    desktop = ("[Desktop Entry]\n"
               "Type=Application\n"
               "Name=LingCode FTP\n"
               "Comment=FTP/FTPS/SFTP client with a Claude chat pane\n"
               "Exec=lingcodeftp\n"
               "Icon=lingcodeftp\n"
               "Terminal=false\n"
               "Categories=Network;FileTransfer;Utility;\n").encode()

    # ---- data.tar.gz ----
    data_entries = []
    for d in ["opt", "opt/lingcodeftp", "usr", "usr/bin",
              "usr/share", "usr/share/applications",
              "usr/share/icons", "usr/share/icons/hicolor",
              "usr/share/icons/hicolor/scalable",
              "usr/share/icons/hicolor/scalable/apps"]:
        data_entries.append((ti("./" + d + "/", 0o755, isdir=True), None))

    md5_lines = []
    def add_file(installed_path, content, mode):
        data_entries.append((ti("./" + installed_path, mode, size=len(content)), content))
        md5_lines.append("{}  {}".format(hashlib.md5(content).hexdigest(), installed_path))

    installed_size_kb = 0
    for fname, mode in payload:
        with open(os.path.join(args.publish, fname), "rb") as f:
            content = f.read()
        add_file(INSTALL + "/" + fname, content, mode)
        installed_size_kb += (len(content) + 1023) // 1024

    add_file("usr/bin/lingcodeftp", wrapper, 0o755)
    add_file("usr/share/applications/lingcodeftp.desktop", desktop, 0o644)
    if args.logo and os.path.exists(args.logo):
        with open(args.logo, "rb") as f:
            svg = f.read()
        add_file("usr/share/icons/hicolor/scalable/apps/lingcodeftp.svg", svg, 0o644)

    data_tar = make_tar_gz(data_entries)

    # ---- control.tar.gz ----
    control = (
        "Package: lingcodeftp\n"
        "Version: {ver}\n"
        "Section: net\n"
        "Priority: optional\n"
        "Architecture: {arch}\n"
        "Maintainer: Xavier Huang <huangxavier526@gmail.com>\n"
        "Installed-Size: {size}\n"
        "Depends: libc6, libstdc++6, libgcc-s1, zlib1g\n"
        "Recommends: curl, openssh-client, python3, libx11-6, libfontconfig1, libice6, libsm6\n"
        "Description: FTP/FTPS/SFTP client with a built-in Claude chat pane\n"
        " A cross-platform (Avalonia/.NET) FTP, FTPS and SFTP client with a\n"
        " built-in Claude chat pane and an FTP MCP server. Self-contained: the\n"
        " .NET runtime is bundled. Uses curl and OpenSSH sftp for transfers.\n"
    ).format(ver=args.version, arch=args.arch, size=installed_size_kb).encode()

    postinst = ("#!/bin/sh\nset -e\n"
                "if command -v update-desktop-database >/dev/null 2>&1; then "
                "update-desktop-database -q /usr/share/applications || true; fi\n"
                "if command -v gtk-update-icon-cache >/dev/null 2>&1; then "
                "gtk-update-icon-cache -q -f /usr/share/icons/hicolor || true; fi\n"
                "exit 0\n").encode()

    md5sums = ("\n".join(md5_lines) + "\n").encode()

    control_entries = [
        (ti("./", 0o755, isdir=True), None),
        (ti("./control", 0o644, size=len(control)), control),
        (ti("./md5sums", 0o644, size=len(md5sums)), md5sums),
        (ti("./postinst", 0o755, size=len(postinst)), postinst),
    ]
    control_tar = make_tar_gz(control_entries)

    # ---- assemble ar ----
    deb = b"!<arch>\n"
    deb += ar_member("debian-binary", b"2.0\n")
    deb += ar_member("control.tar.gz", control_tar)
    deb += ar_member("data.tar.gz", data_tar)

    with open(args.out, "wb") as f:
        f.write(deb)
    print("wrote {} ({} bytes, arch={}, installed {} KB)".format(
        args.out, len(deb), args.arch, installed_size_kb))

if __name__ == "__main__":
    main()
