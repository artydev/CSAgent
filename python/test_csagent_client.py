"""Tests for csagent_client — a fake CsAgent server (http.server) speaks the
real SSE protocol: the client is exercised exactly like against --api."""
import importlib.util
import json
import os
import threading
import unittest
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from typing import Any

_spec = importlib.util.spec_from_file_location(
    "csagent_client",
    os.path.join(os.path.dirname(os.path.abspath(__file__)), "csagent_client.py"))
assert _spec is not None and _spec.loader is not None
cc: Any = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(cc)


class FakeCsAgent:
    """A stdlib stand-in for the CsAgent API: /api/chat (SSE), /api/confirm,
    /api/skills. Records what the client sent."""

    def __init__(self, events, confirm_wait=True):
        self.events = events            # list of (type, data) to emit
        self.confirm_wait = confirm_wait
        self.confirms: list[str] = []
        self.got: dict[str, Any] = {}
        self._confirm_go = threading.Event()
        self.server: ThreadingHTTPServer | None = None
        self.thread: threading.Thread | None = None

    def handler(self):
        outer = self

        class H(BaseHTTPRequestHandler):
            def log_message(self, format: str, *args: Any) -> None:
                pass

            def _auth(self):
                return self.headers.get("Authorization")

            def do_GET(self):
                if self.path.startswith("/api/skills"):
                    body = json.dumps([{"name": "plan-then-execute", "summary": "s"}]).encode()
                    self.send_response(200)
                    self.send_header("Content-Type", "application/json")
                    self.end_headers()
                    self.wfile.write(body)
                    return
                if self.path.startswith("/api/chat"):
                    outer.got["chat"] = self.path
                    outer.got["auth"] = self._auth()
                    self.send_response(200)
                    self.send_header("Content-Type", "text/event-stream")
                    self.end_headers()
                    for i, (etype, data) in enumerate(outer.events):
                        payload = json.dumps({"id": i + 1, "type": etype, "data": data})
                        self.wfile.write(f"data: {payload}\n\n".encode())
                        self.wfile.flush()
                        if etype == "confirm" and outer.confirm_wait:
                            outer._confirm_go.wait(timeout=10)
                    return
                self.send_response(404)
                self.end_headers()

            def do_POST(self):
                length = int(self.headers.get("Content-Length", 0))
                body = self.rfile.read(length)
                if self.path == "/api/confirm":
                    outer.confirms.append(body.decode())
                    outer._confirm_go.set()
                    self.send_response(200)
                    self.end_headers()
                    self.wfile.write(b"ok")
                    return
                if self.path == "/api/chat":
                    outer.got["body"] = body
                    outer.got["ctype"] = self.headers.get("Content-Type")
                    outer.got["auth"] = self._auth()
                    self.send_response(200)
                    self.send_header("Content-Type", "text/event-stream")
                    self.end_headers()
                    for i, (etype, data) in enumerate(outer.events):
                        payload = json.dumps({"id": i + 1, "type": etype, "data": data})
                        self.wfile.write(f"data: {payload}\n\n".encode())
                        self.wfile.flush()
                        if etype == "confirm" and outer.confirm_wait:
                            outer._confirm_go.wait(timeout=10)
                    return
                self.send_response(404)
                self.end_headers()

        return H

    def start(self):
        self.server = ThreadingHTTPServer(("127.0.0.1", 0), self.handler())
        self.thread = threading.Thread(target=self.server.serve_forever, daemon=True)
        self.thread.start()
        return f"http://127.0.0.1:{self.server.server_address[1]}"

    def stop(self):
        if self.server is None or self.thread is None:
            return
        self.server.shutdown()
        self.server.server_close()


def client_with(events, **kw):
    fake = FakeCsAgent(events)
    url = fake.start()
    return cc.CsAgentClient(url, api_key="k1"), fake


class AskTests(unittest.TestCase):
    def test_ask_returns_the_done_message_and_logs_events_in_order(self):
        c, fake = client_with([
            ("step", {"n": 1, "m": 2}),
            ("thought", "let me look"),
            ("call", {"n": "list_dir", "a": "{}"}),
            ("result", {"r": "ok", "e": False}),
            ("done", "there are 2 files"),
        ], confirm_wait=False)
        seen = []
        answer = c.ask("list them", on_event=lambda t, d: seen.append((t, d)))
        self.assertEqual(answer, "there are 2 files")
        self.assertEqual([t for t, _ in seen],
                         ["step", "thought", "call", "result", "done"])
        self.assertIn("prompt=list", fake.got["chat"])
        self.assertEqual(fake.got["auth"], "Bearer k1")
        fake.stop()

    def test_task_slug_is_sent_as_a_query_param(self):
        c, fake = client_with([("done", "ok")], confirm_wait=False)
        c.ask("hello", task="daily-brief")
        self.assertIn("task=daily-brief", fake.got["chat"])
        fake.stop()

    def test_confirm_events_are_answered_with_the_policy(self):
        c, fake = client_with([
            ("confirm", {"tool": "delete_file"}),
            ("done", "deleted"),
        ])
        answer = c.ask("delete f.txt", confirm=True)
        self.assertEqual(answer, "deleted")
        self.assertEqual(fake.confirms, ["true"])
        fake.stop()

    def test_confirm_can_be_refused_or_delegated_to_a_callback(self):
        c, fake = client_with([
            ("confirm", {"tool": "delete_file"}),
            ("done", "kept"),
        ])
        asked = []
        answer = c.ask("delete f.txt",
                       on_confirm=lambda tool: (asked.append(tool), tool == "keep_file")[1])
        self.assertEqual(answer, "kept")
        self.assertEqual(asked, ["delete_file"])
        self.assertEqual(fake.confirms, ["false"])
        fake.stop()

    def test_a_pending_confirmation_without_policy_raises(self):
        c, fake = client_with([
            ("confirm", {"tool": "delete_file"}),
            ("done", "never"),
        ], confirm_wait=False)
        with self.assertRaises(cc.PendingConfirmation) as ctx:
            c.ask("delete f.txt", confirm=None)
        self.assertEqual(ctx.exception.tool, "delete_file")
        fake.stop()

    def test_confirm_answers_a_pending_tool_from_outside_the_stream(self):
        c, fake = client_with([
            ("confirm", {"tool": "git_push"}),
            ("done", "pushed"),
        ])
        it = c.stream("push it")
        self.assertEqual(next(it), ("confirm", {"tool": "git_push"}))
        self.assertTrue(c.confirm(True))
        rest = list(it)
        self.assertEqual([t for t, _ in rest], ["done"])
        self.assertEqual(fake.confirms, ["true"])
        fake.stop()

    def test_error_events_raise(self):
        c, fake = client_with([("error", "API Key not set.")], confirm_wait=False)
        with self.assertRaisesRegex(cc.CsAgentError, "API Key not set"):
            c.ask("hello")
        fake.stop()

    def test_a_stream_closed_before_done_raises(self):
        c, fake = client_with([], confirm_wait=False)
        with self.assertRaisesRegex(cc.CsAgentError, "closed the stream"):
            c.ask("hello")
        fake.stop()


class ImageTests(unittest.TestCase):
    def test_an_image_is_sent_as_multipart_with_its_bytes(self):
        import tempfile
        img = os.path.join(tempfile.mkdtemp(), "pic.png")
        with open(img, "wb") as fh:
            fh.write(b"\x89PNG\r\n\x1a\n" + b"IMGDATA")
        c, fake = client_with([("done", "a cat")], confirm_wait=False)
        answer = c.ask("what is this?", image=img, task="vision")
        self.assertEqual(answer, "a cat")
        body = fake.got["body"]
        self.assertIn("multipart/form-data; boundary=", fake.got["ctype"])
        self.assertIn(b'filename="pic.png"', body)
        self.assertIn(b"what is this?", body)
        self.assertIn(b"task", body)
        self.assertIn(b"\x89PNG\r\n\x1a\nIMGDATA", body)
        self.assertEqual(fake.got["auth"], "Bearer k1")
        fake.stop()

    def test_a_missing_image_raises_before_any_request(self):
        c, fake = client_with([("done", "x")], confirm_wait=False)
        with self.assertRaisesRegex(cc.CsAgentError, "image not found"):
            c.ask("hi", image="/nonexistent/pic.png")
        self.assertNotIn("body", fake.got)
        fake.stop()


class MiscTests(unittest.TestCase):
    def test_skills_returns_the_library(self):
        c, fake = client_with([("done", "x")], confirm_wait=False)
        skills = c.skills()
        self.assertEqual(skills[0]["name"], "plan-then-execute")
        fake.stop()

    def test_an_invalid_base_url_is_refused(self):
        with self.assertRaises(ValueError):
            cc.CsAgentClient("ftp://nope")
        self.assertIsNone(cc.CsAgentClient("http://localhost:5050").api_key)


if __name__ == "__main__":
    unittest.main(verbosity=2)
