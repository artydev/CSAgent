"""csagent_client — drive a CsAgent server from Python, standard library only.

Works against the headless API mode (``csagent --api``) and the web UI host,
which expose the same SSE protocol:

    GET/POST  /api/chat     one prompt (optionally with an image) -> SSE event stream
    POST      /api/confirm  body "true" / "false" -> approve or refuse a pending tool
    GET       /api/skills   the skill library as JSON

Event types on the stream: ``step``, ``thought``, ``model``, ``call``,
``result``, ``warning``, ``danger``, ``confirm``, ``done``, ``error``.

Example:
    client = CsAgentClient("http://localhost:5050", api_key="...")
    answer = client.ask("List the files in the current folder")

    # log every step, approve destructive tools case by case
    answer = client.ask(
        "Commit the staged changes",
        on_event=lambda t, d: print(t, d),
        on_confirm=lambda tool: input(f"allow {tool}? [y/N] ").lower() == "y",
    )
"""
from __future__ import annotations

import http.client
import json
import mimetypes
import os
import uuid
from typing import Any
from urllib.parse import quote, urlsplit

__version__ = "0.1.0"


class CsAgentError(RuntimeError):
    """The server answered with an 'error' event, or the request failed."""


class PendingConfirmation(CsAgentError):
    """A tool call needs confirmation and no policy was provided.

    ``tool`` is the name of the tool waiting; the stream was closed. Answer
    later with :meth:`CsAgentClient.confirm`, or rerun with ``confirm=...``
    / ``on_confirm=...``.
    """

    def __init__(self, tool: str):
        super().__init__(f"confirmation pending for tool '{tool}'")
        self.tool = tool


class CsAgentClient:
    """A minimal client for the CsAgent SSE API. One conversation per server."""

    def __init__(self, base_url: str = "http://localhost:5050",
                 api_key: str | None = None, timeout: float = 600.0):
        parts = urlsplit(base_url)
        if parts.scheme not in ("http", "https"):
            raise ValueError(f"unsupported base_url: {base_url}")
        self._host = parts.hostname or "localhost"
        self._port = parts.port or (443 if parts.scheme == "https" else 80)
        self._secure = parts.scheme == "https"
        self._path = parts.path.rstrip("/")
        self.api_key = api_key
        self.timeout = timeout

    # ── the conversation ──────────────────────────────────────────────

    def ask(self, prompt: str, *, task: str | None = None,
            image: str | None = None,
            confirm: bool | None = True,
            on_event=None, on_confirm=None):
        """Send one prompt and follow the conversation until the 'done' event.

        Returns the final answer. ``image`` attaches a local file (jpg, png,
        gif, webp) to the turn. ``task`` sets the task slug for the task
        tracker.

        ``confirm`` / ``on_confirm`` decide what to answer to the server's
        ``confirm`` events (a destructive tool the agent wants to run):
        ``on_confirm(tool_name) -> bool`` wins over the constant ``confirm``.
        With both off (``confirm=None`` and no ``on_confirm``) a
        :class:`PendingConfirmation` is raised for the first pending tool.
        """
        for etype, data in self._stream(prompt, task=task, image=image):
            if on_event:
                on_event(etype, data)
            if etype == "confirm":
                allow = self._decide_confirmation(str(data.get("tool", "")),
                                                 confirm, on_confirm)
                self.confirm(allow)
            elif etype == "error":
                raise CsAgentError(str(data))
            elif etype == "done":
                return str(data)
        raise CsAgentError("the server closed the stream before 'done'")

    def stream(self, prompt: str, *, task: str | None = None,
               image: str | None = None):
        """Raw access: yield (type, data) tuples. Confirmations are NOT
        answered here — the caller answers them with :meth:`confirm` when a
        ('confirm', {'tool': ...}) event is yielded."""
        yield from self._stream(prompt, task=task, image=image)

    # ── the small endpoints ───────────────────────────────────────────

    def confirm(self, allow: bool = True) -> bool:
        """Answer the pending confirmation. False when none was pending."""
        conn = self._connect()
        try:
            conn.request("POST", self._url("/api/confirm"),
                         body="true" if allow else "false",
                         headers=self._headers("text/plain"))
            resp = conn.getresponse()
            return resp.read() is not None and resp.status == 200
        finally:
            conn.close()

    def skills(self) -> list[Any]:
        conn = self._connect()
        try:
            conn.request("GET", self._url("/api/skills"), headers=self._headers())
            resp = conn.getresponse()
            if resp.status != 200:
                raise CsAgentError(f"/api/skills -> HTTP {resp.status}")
            return json.loads(resp.read())
        finally:
            conn.close()

    # ── internals ─────────────────────────────────────────────────────

    def _connect(self) -> http.client.HTTPConnection:
        return http.client.HTTPSConnection(self._host, self._port,
                                           timeout=self.timeout) if self._secure \
            else http.client.HTTPConnection(self._host, self._port,
                                            timeout=self.timeout)

    def _headers(self, content_type: str | None = None) -> dict[str, str]:
        headers = {}
        if self.api_key:
            headers["Authorization"] = f"Bearer {self.api_key}"
        if content_type:
            headers["Content-Type"] = content_type
        return headers

    def _url(self, path: str) -> str:
        return self._path + path

    @staticmethod
    def _decide_confirmation(tool: str, confirm, on_confirm) -> bool:
        if on_confirm is not None:
            return bool(on_confirm(tool))
        if confirm is None:
            raise PendingConfirmation(tool)
        return bool(confirm)

    def _stream(self, prompt: str, *, task: str | None, image: str | None):
        url = self._url("/api/chat")
        headers: dict[str, str] = {}
        body: bytes | None = None
        method = "GET"
        if image:
            body, headers = self._multipart(prompt, task, image)
            method = "POST"
        else:
            query = f"prompt={quote(prompt)}"
            if task:
                query += f"&task={quote(task)}"
            url += "?" + query
        conn = self._connect()
        try:
            conn.request(method, url, body=body,
                         headers={**self._headers(), **headers})
            resp = conn.getresponse()
            if resp.status != 200:
                raise CsAgentError(f"/api/chat -> HTTP {resp.status}: "
                                   f"{resp.read()[:300]!r}")
            for line in resp:
                text = line.decode("utf-8", "replace").strip()
                if not text.startswith("data:"):
                    continue
                event = json.loads(text[len("data:"):].strip())
                yield event.get("type"), event.get("data")
        finally:
            conn.close()

    @staticmethod
    def _multipart(prompt: str, task: str | None, image: str) -> tuple[bytes, dict[str, str]]:
        if not os.path.isfile(image):
            raise CsAgentError(f"image not found: {image}")
        boundary = uuid.uuid4().hex
        mime = mimetypes.guess_type(image)[0] or "application/octet-stream"
        head = (f"--{boundary}\r\n"
                f'Content-Disposition: form-data; name="prompt"\r\n\r\n'
                f"{prompt}\r\n")
        if task:
            head += (f"--{boundary}\r\n"
                     f'Content-Disposition: form-data; name="task"\r\n\r\n'
                     f"{task}\r\n")
        head += (f"--{boundary}\r\n"
                 f'Content-Disposition: form-data; name="image"; '
                 f'filename="{os.path.basename(image)}"\r\n'
                 f"Content-Type: {mime}\r\n\r\n")
        with open(image, "rb") as fh:
            raw = head.encode("utf-8") + fh.read() + f"\r\n--{boundary}--\r\n".encode("utf-8")
        headers = {"Content-Type": f"multipart/form-data; boundary={boundary}"}
        return raw, headers
