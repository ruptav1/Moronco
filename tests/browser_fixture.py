"""Local-only browser integration pages; Python standard library, no dependencies."""

import argparse
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from urllib.parse import urlsplit


PAGES = {
    "/one": (
        "First fixture",
        "<h1>First fixture</h1><p>This local fixture verifies ordinary browser navigation "
        "and keeps all test requests on the loopback interface.</p>"
        '<a href="/two">Second fixture</a> <a href="/popup" target="_blank">Popup fixture</a>',
    ),
    "/two": (
        "Second fixture",
        "<h1>Second fixture</h1><p>This fixture contains searchable text for find-in-page, "
        "back and forward navigation, and browser download validation.</p>"
        '<a href="/one">First fixture</a> <a href="/download">Download fixture</a>',
    ),
    "/popup": (
        "Popup fixture",
        "<h1>Popup fixture</h1><p>This local popup checks that a browser-created tab "
        "keeps the expected opener relationship.</p>"
        "<script>document.body.dataset.opener = String(window.opener !== null);</script>",
    ),
}


class FixtureHandler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def do_HEAD(self):
        self.respond(head=True)

    def do_GET(self):
        self.respond(head=False)

    def respond(self, head):
        route = urlsplit(self.path).path
        headers = {}
        status = 200
        if route == "/download":
            data = b"Minium download fixture\n"
            mime = "text/plain; charset=utf-8"
            headers["Content-Disposition"] = 'attachment; filename="fixture.txt"'
        elif route in PAGES:
            title, body = PAGES[route]
            data = (
                '<!doctype html><html lang="en"><head><meta charset="utf-8">'
                f"<title>{title}</title><style>html,body{{min-height:100%;background:#e7f6ee;color:#143a2a;font:18px sans-serif}}body{{padding:24px;margin:0}}</style></head><body>{body}</body></html>"
            ).encode("utf-8")
            mime = "text/html; charset=utf-8"
        elif route == "/favicon.ico":
            status, data, mime = 204, b"", "image/x-icon"
        else:
            status, data, mime = 404, b"Unknown local fixture route.\n", "text/plain; charset=utf-8"
        self.send_response(status)
        self.send_header("Content-Type", mime)
        self.send_header("Content-Length", str(len(data)))
        self.send_header("Cache-Control", "no-store")
        self.send_header("X-Content-Type-Options", "nosniff")
        for key, value in headers.items():
            self.send_header(key, value)
        self.end_headers()
        if not head and data:
            try:
                self.wfile.write(data)
            except (BrokenPipeError, ConnectionResetError):
                pass


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--port", type=int, default=0)
    parser.add_argument("--port-file", type=Path)
    args = parser.parse_args()
    with ThreadingHTTPServer(("127.0.0.1", args.port), FixtureHandler) as server:
        port = server.server_address[1]
        if args.port_file:
            args.port_file.parent.mkdir(parents=True, exist_ok=True)
            temporary = args.port_file.with_suffix(args.port_file.suffix + ".tmp")
            temporary.write_text(str(port), encoding="ascii")
            temporary.replace(args.port_file)
        print(f"READY http://127.0.0.1:{port}", flush=True)
        server.serve_forever(poll_interval=0.2)


if __name__ == "__main__":
    main()
