#!/usr/bin/env python3

import argparse
import json
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer


parser = argparse.ArgumentParser()
parser.add_argument("--port-file", required=True)
parser.add_argument("--alert-file", required=True)
args = parser.parse_args()

unhealthy = False


class Handler(BaseHTTPRequestHandler):
    def log_message(self, _format, *_args):
        return

    def do_GET(self):
        if self.path == "/health/live":
            self.send_response(200)
            self.end_headers()
            self.wfile.write(b"Healthy")
            return

        if self.path == "/health/ready":
            checks = {
                "postgres": {
                    "status": "Unhealthy" if unhealthy else "Healthy",
                    "description": "database connection refused" if unhealthy else "database reachable",
                    "duration": 12.5,
                },
                "rabbitmq": {
                    "status": "Healthy",
                    "description": "broker reachable",
                    "duration": 4.0,
                },
            }
            payload = {
                "status": "Unhealthy" if unhealthy else "Healthy",
                "checks": checks,
                "duration": 16.5,
            }
            body = json.dumps(payload).encode()
            self.send_response(503 if unhealthy else 200)
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)
            return

        self.send_response(404)
        self.end_headers()

    def do_POST(self):
        global unhealthy
        if self.path == "/mode/unhealthy":
            unhealthy = True
            self.send_response(204)
            self.end_headers()
            return

        if self.path == "/alert":
            length = int(self.headers.get("Content-Length", "0"))
            body = self.rfile.read(length)
            with open(args.alert_file, "wb") as output:
                output.write(body)
            self.send_response(204)
            self.end_headers()
            return

        self.send_response(404)
        self.end_headers()


server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
with open(args.port_file, "w", encoding="utf-8") as output:
    output.write(str(server.server_port))
server.serve_forever()
