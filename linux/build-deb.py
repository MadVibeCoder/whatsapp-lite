#!/usr/bin/env python3
"""Build the VibeChat .deb for Ubuntu/Debian.

Assembles the package directly (a .deb is an `ar` archive holding two tarballs), so it needs only
Python 3 and runs on Linux, macOS or Windows alike:

    python3 linux/build-deb.py

Output: linux/dist/vibechat_<version>_all.deb (the version comes from Version.cs, shared with Windows).
"""
import gzip
import hashlib
import io
import os
import re
import tarfile
import time

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
APP_ID = "io.github.madvibecoder.VibeChat"
MTIME = int(os.environ.get("SOURCE_DATE_EPOCH", time.time()))

# (path inside the package, source file under linux/, mode)
FILES = [
    ("usr/bin/vibechat", "vibechat.py", 0o755),
    ("usr/share/applications/%s.desktop" % APP_ID, "%s.desktop" % APP_ID, 0o644),
    ("usr/share/icons/hicolor/scalable/apps/%s.svg" % APP_ID, "icons/%s.svg" % APP_ID, 0o644),
    ("usr/share/icons/hicolor/scalable/apps/%s-unread.svg" % APP_ID, "icons/%s-unread.svg" % APP_ID, 0o644),
]

CONTROL = """Package: vibechat
Version: {version}
Architecture: all
Maintainer: MadVibeCoder <319844971+MadVibeCoder@users.noreply.github.com>
Installed-Size: {size_kb}
Depends: python3 (>= 3.8), python3-gi, gir1.2-gtk-3.0, gir1.2-webkit2-4.1, gir1.2-ayatanaappindicator3-0.1 | gir1.2-appindicator3-0.1
Section: net
Priority: optional
Homepage: https://github.com/MadVibeCoder/vibechat
Description: lightweight desktop client for WhatsApp Web
 VibeChat is a small GTK app that shows WhatsApp Web in its own window using
 the WebKitGTK engine already on your system. It stays in the tray when
 closed, shows an unread badge, can start on login, and opens links in your
 normal browser.
 .
 VibeChat is not affiliated with, endorsed by, or connected to WhatsApp or Meta.
"""


def version():
    with open(os.path.join(ROOT, "Version.cs"), encoding="utf-8") as f:
        m = re.search(r'AssemblyInformationalVersion\("([^"]+)"\)', f.read())
    if not m:
        raise SystemExit("could not read the version from Version.cs")
    return m.group(1)


def payload(src, ver):
    with open(os.path.join(HERE, src), "rb") as f:
        data = f.read().replace(b"\r\n", b"\n")  # a CRLF shebang would break /usr/bin/vibechat
    if src == "vibechat.py":
        data = data.replace(b'VERSION = "@VERSION@"', ('VERSION = "%s"' % ver).encode("ascii"))
    return data


def tar_gz(entries):
    """entries: (name, bytes or None for a directory, mode). Everything owned by root, as dpkg expects."""
    raw = io.BytesIO()
    with tarfile.open(fileobj=raw, mode="w", format=tarfile.GNU_FORMAT) as tar:
        for name, data, mode in entries:
            info = tarfile.TarInfo(name)
            info.mtime, info.mode = MTIME, mode
            info.uid = info.gid = 0
            info.uname = info.gname = "root"
            if data is None:
                info.type = tarfile.DIRTYPE
                tar.addfile(info)
            else:
                info.size = len(data)
                tar.addfile(info, io.BytesIO(data))
    out = io.BytesIO()
    with gzip.GzipFile(filename="", fileobj=out, mode="wb", compresslevel=9, mtime=MTIME) as gz:
        gz.write(raw.getvalue())
    return out.getvalue()


def ar(members):
    """Common ar format, as written by dpkg-deb: 60-byte headers, data padded to an even length."""
    out = [b"!<arch>\n"]
    for name, data in members:
        out.append(("%-16s%-12d%-6d%-6d%-8s%-10d`\n" % (name, MTIME, 0, 0, "100644", len(data))).encode("ascii"))
        out.append(data)
        if len(data) % 2:
            out.append(b"\n")
    return b"".join(out)


def main():
    ver = version()
    files = [(dest, payload(src, ver), mode) for dest, src, mode in FILES]

    dirs = set()
    for dest, _, _ in files:
        parts = dest.split("/")[:-1]
        for i in range(1, len(parts) + 1):
            dirs.add("/".join(parts[:i]))
    data_entries = [("./", None, 0o755)]
    data_entries += [("./" + d + "/", None, 0o755) for d in sorted(dirs)]
    data_entries += [("./" + dest, data, mode) for dest, data, mode in files]

    size_kb = sum((len(d) + 1023) // 1024 for _, d, _ in files) + len(dirs)
    control = CONTROL.format(version=ver, size_kb=size_kb).encode("utf-8")
    md5sums = "".join("%s  %s\n" % (hashlib.md5(d).hexdigest(), dest) for dest, d, _ in files).encode("ascii")
    control_entries = [("./", None, 0o755), ("./control", control, 0o644), ("./md5sums", md5sums, 0o644)]

    deb = ar([("debian-binary", b"2.0\n"),
              ("control.tar.gz", tar_gz(control_entries)),
              ("data.tar.gz", tar_gz(data_entries))])
    os.makedirs(os.path.join(HERE, "dist"), exist_ok=True)
    path = os.path.join(HERE, "dist", "vibechat_%s_all.deb" % ver)
    with open(path, "wb") as f:
        f.write(deb)
    print("built %s (%d bytes)" % (path, len(deb)))


if __name__ == "__main__":
    main()
