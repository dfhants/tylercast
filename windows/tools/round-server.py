#!/usr/bin/env python3
"""One Windows test round, served from the Mac on the local network.

    python3 tools/round-server.py ROUND_DIR RESULTS_DIR [--port 8765]

ROUND_DIR holds index.html, round.ps1 and the files to download (tylercast.exe ...).
Every URL carries a random token, so only someone given the link can use it.
The page and the script get the base URL filled in (__BASE__). Plain HTTP can be tampered
with by anyone on the network's path, so (security review, finding 2): the script gets the
SHA-256 of every download (__HASHES__) and checks each before running it, and the page's
one-liner carries the script's own SHA-256 (__PSHASH__) and refuses to run a script that
doesn't match. The script hash is printed here too, to be passed on outside the page (in
chat), so the tester can check the one-liner itself wasn't changed. The PC sends its
results back with a POST to /<token>/upload?name=<file>; the server saves them to
RESULTS_DIR and stops once a file named results*.zip has arrived (one round, one-off).

Standard library only; listens on this Mac's LAN address, not on every interface.
"""
import argparse
import hashlib
import http.server
import os
import re
import secrets
import socket
import sys
import threading
import time

MAX_UPLOAD = 1_500_000_000  # 1.5 GB: recordings plus logs, with room to spare


def lan_address():
    s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    try:
        s.connect(("192.0.2.1", 9))  # no packet is sent; this picks the outgoing interface
        return s.getsockname()[0]
    finally:
        s.close()


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("round_dir")
    ap.add_argument("results_dir")
    ap.add_argument("--port", type=int, default=8765)
    ap.add_argument("--token", help="reuse a page address (default: a new random one)")
    a = ap.parse_args()
    round_dir = os.path.abspath(a.round_dir)
    results = os.path.abspath(a.results_dir)
    os.makedirs(results, exist_ok=True)
    host = lan_address()
    token = a.token or secrets.token_urlsafe(12)
    base = f"http://{host}:{a.port}/{token}"
    files = sorted(f for f in os.listdir(round_dir) if not f.startswith("."))
    done = threading.Event()

    def sha256(path):
        h = hashlib.sha256()
        with open(path, "rb") as f:
            for chunk in iter(lambda: f.read(1 << 20), b""):
                h.update(chunk)
        return h.hexdigest().upper()

    downloads = {f: sha256(os.path.join(round_dir, f)) for f in files if f not in ("index.html", "round.ps1")}
    hashes = "; ".join(f"'{f}' = '{h}'" for f, h in downloads.items())
    script = open(os.path.join(round_dir, "round.ps1"), encoding="utf-8").read()
    script = script.replace("__BASE__", base).replace("__HASHES__", hashes).strip() + "\n"
    ps_hash = hashlib.sha256(script.strip().encode("utf-8")).hexdigest().upper()
    page = open(os.path.join(round_dir, "index.html"), encoding="utf-8").read()
    page = page.replace("__BASE__", base).replace("__PSHASH__", ps_hash).replace("__PSHASH8__", ps_hash[:8])

    class Handler(http.server.BaseHTTPRequestHandler):
        server_version = "TylerCastRound/1"

        def log_message(self, fmt, *args):
            sys.stdout.write("%s %s %s\n" % (time.strftime("%H:%M:%S"), self.client_address[0], fmt % args))
            sys.stdout.flush()

        def _path(self):
            p = self.path.split("?", 1)[0]
            prefix = f"/{token}"
            if not (p == prefix or p.startswith(prefix + "/")):
                return None
            return p[len(prefix):] or "/"

        def _send(self, code, body, ctype="text/plain; charset=utf-8", extra=None):
            self.send_response(code)
            self.send_header("Content-Type", ctype)
            self.send_header("Content-Length", str(len(body)))
            self.send_header("Cache-Control", "no-store")
            for k, v in (extra or {}).items():
                self.send_header(k, v)
            self.end_headers()
            if self.command != "HEAD":
                self.wfile.write(body)

        def do_HEAD(self):
            self.do_GET()

        def do_GET(self):
            p = self._path()
            if p is None:
                return self._send(404, b"not found\n")
            if p in ("/", "/index.html"):
                return self._send(200, page.encode(), "text/html; charset=utf-8")
            if p == "/round.ps1":
                # No BOM: `irm | iex` would try to run it as a command.
                return self._send(200, script.encode("utf-8"), "text/plain; charset=utf-8")
            m = re.fullmatch(r"/files/([A-Za-z0-9._-]+)", p)
            if m and m.group(1) in files:
                path = os.path.join(round_dir, m.group(1))
                size = os.path.getsize(path)
                self.send_response(200)
                self.send_header("Content-Type", "application/octet-stream")
                self.send_header("Content-Length", str(size))
                self.send_header("Content-Disposition", f'attachment; filename="{m.group(1)}"')
                self.end_headers()
                if self.command == "HEAD":
                    return
                with open(path, "rb") as f:
                    while chunk := f.read(1 << 20):
                        self.wfile.write(chunk)
                return
            return self._send(404, b"not found\n")

        def do_POST(self):
            p = self._path()
            if p != "/upload":
                return self._send(404, b"not found\n")
            q = self.path.split("?", 1)[1] if "?" in self.path else ""
            m = re.search(r"(?:^|&)name=([A-Za-z0-9._-]{1,100})(?:&|$)", q)
            n = int(self.headers.get("Content-Length") or 0)
            if not m or n <= 0 or n > MAX_UPLOAD:
                return self._send(400, b"need ?name=<file> and a body under 1.5 GB\n")
            name = time.strftime("%H%M%S-") + m.group(1)
            path = os.path.join(results, name)
            left = n
            with open(path, "wb") as f:
                while left > 0:
                    chunk = self.rfile.read(min(left, 1 << 20))
                    if not chunk:
                        break
                    f.write(chunk)
                    left -= len(chunk)
            if left > 0:
                os.remove(path)
                return self._send(400, b"upload cut short\n")
            print(f"UPLOADED {path} ({n} bytes)", flush=True)
            self._send(200, f"Received {m.group(1)} ({n // 1000} kB). Thank you!\n".encode())
            if m.group(1).startswith("results") and m.group(1).endswith(".zip"):
                done.set()

    srv = http.server.ThreadingHTTPServer((host, a.port), Handler)
    print(f"ROUND PAGE {base}/", flush=True)
    print(f"ROUND SCRIPT SHA256 {ps_hash}", flush=True)
    print(f"Serving {', '.join(files)} from {round_dir}; results to {results}", flush=True)
    t = threading.Thread(target=srv.serve_forever, daemon=True)
    t.start()
    done.wait()
    time.sleep(2)  # let the reply reach the PC
    srv.shutdown()
    print("ROUND COMPLETE: results received; server stopped", flush=True)


if __name__ == "__main__":
    main()
