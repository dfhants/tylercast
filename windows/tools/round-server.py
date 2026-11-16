#!/usr/bin/env python3
"""One Windows test round, served from the Mac on the local network.

    python3 tools/round-server.py ROUND_DIR RESULTS_DIR [--port 8765]

ROUND_DIR holds index.html, round.ps1 and the files to download (tylercast.exe ...).
Every URL carries a random token, so only someone given the link can use it.
The page and the script get the base URL filled in (__BASE__). The PC sends its
results back with a POST to /<token>/upload?name=<file>; the server saves them to
RESULTS_DIR and stops once a file named results*.zip has arrived (one round, one-off).

Standard library only; listens on this Mac's LAN address, not on every interface.
"""
import argparse
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
    a = ap.parse_args()
    round_dir = os.path.abspath(a.round_dir)
    results = os.path.abspath(a.results_dir)
    os.makedirs(results, exist_ok=True)
    host = lan_address()
    token = secrets.token_urlsafe(12)
    base = f"http://{host}:{a.port}/{token}"
    files = sorted(f for f in os.listdir(round_dir) if not f.startswith("."))
    done = threading.Event()

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
                html = open(os.path.join(round_dir, "index.html"), encoding="utf-8").read().replace("__BASE__", base)
                return self._send(200, html.encode(), "text/html; charset=utf-8")
            if p == "/round.ps1":
                ps = open(os.path.join(round_dir, "round.ps1"), encoding="utf-8").read().replace("__BASE__", base)
                return self._send(200, ps.encode("utf-8")  # no BOM: `irm | iex` would run it as a command, "text/plain; charset=utf-8")
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
    print(f"Serving {', '.join(files)} from {round_dir}; results to {results}", flush=True)
    t = threading.Thread(target=srv.serve_forever, daemon=True)
    t.start()
    done.wait()
    time.sleep(2)  # let the reply reach the PC
    srv.shutdown()
    print("ROUND COMPLETE: results received; server stopped", flush=True)


if __name__ == "__main__":
    main()
