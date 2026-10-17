"""Сервер HTTP и эха UDP в пространстве сервера: сюда ведут прямой маршрут и маршрут через VLESS."""
import hashlib
import os
import socket
import threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import parse_qs, urlparse


class Handler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def log_message(self, *args):
        pass

    def reply(self, body, headers=()):
        self.send_response(200)
        for key, value in headers:
            self.send_header(key, value)
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def do_GET(self):
        url = urlparse(self.path)
        if url.path == "/bytes":
            data = os.urandom(int(parse_qs(url.query)["n"][0]))
            self.reply(data, [("X-Sum", hashlib.sha256(data).hexdigest())])
        else:
            host, port = self.client_address[:2]
            self.reply(f"hello from direct-server, you are {host}:{port}\n".encode())

    def do_POST(self):
        left = int(self.headers["Content-Length"])
        total = left
        digest = hashlib.sha256()
        while left:
            chunk = self.rfile.read(min(left, 1 << 16))
            if not chunk:
                break
            digest.update(chunk)
            left -= len(chunk)
        self.reply(f"{total - left} {digest.hexdigest()}\n".encode())


def udp_echo():
    sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    sock.bind(("0.0.0.0", 7))
    while True:
        data, address = sock.recvfrom(65535)
        sock.sendto(data, address)


threading.Thread(target=udp_echo, daemon=True).start()
ThreadingHTTPServer(("0.0.0.0", 80), Handler).serve_forever()
