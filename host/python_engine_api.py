#!/usr/bin/env python3
"""
Thin HTTP API in front of the film engine (for Blazor / any client).

Does NOT reimplement the renderer — it calls gui/review_app/pipeline_api.py.

Usage (repo root, venv active):
  python host/python_engine_api.py
  python host/python_engine_api.py --port 8765

Endpoints:
  GET  /health
  GET  /api/projects
  POST /api/projects/{id}/activate
  GET  /api/jobs
  POST /api/jobs/gen-scene   JSON: {"project_id":"Buster","scene":2,"only_missing":true}
  POST /api/jobs/cancel
  GET  /api/stage2-status
"""
from __future__ import annotations

import argparse
import json
import sys
import traceback
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from typing import Any, Dict, Optional, Tuple
from urllib.parse import parse_qs, urlparse

ROOT = Path(__file__).resolve().parents[1]
GUI = ROOT / "gui"
for p in (ROOT, GUI):
    s = str(p)
    if s not in sys.path:
        sys.path.insert(0, s)


def _json_response(handler: BaseHTTPRequestHandler, code: int, payload: Any) -> None:
    body = json.dumps(payload, ensure_ascii=False, default=str).encode("utf-8")
    handler.send_response(code)
    handler.send_header("Content-Type", "application/json; charset=utf-8")
    handler.send_header("Content-Length", str(len(body)))
    handler.send_header("Access-Control-Allow-Origin", "*")
    handler.send_header("Access-Control-Allow-Methods", "GET, POST, OPTIONS")
    handler.send_header("Access-Control-Allow-Headers", "Content-Type")
    handler.end_headers()
    handler.wfile.write(body)


def _read_json(handler: BaseHTTPRequestHandler) -> Dict[str, Any]:
    n = int(handler.headers.get("Content-Length") or 0)
    if n <= 0:
        return {}
    raw = handler.rfile.read(n)
    try:
        data = json.loads(raw.decode("utf-8"))
        return data if isinstance(data, dict) else {}
    except json.JSONDecodeError:
        return {}


def _api():
    from review_app import pipeline_api as api

    return api


class Handler(BaseHTTPRequestHandler):
    server_version = "NickAndMeEngineAPI/0.1"

    def log_message(self, fmt: str, *args: Any) -> None:
        sys.stderr.write("%s - %s\n" % (self.address_string(), fmt % args))

    def do_OPTIONS(self) -> None:
        self.send_response(204)
        self.send_header("Access-Control-Allow-Origin", "*")
        self.send_header("Access-Control-Allow-Methods", "GET, POST, OPTIONS")
        self.send_header("Access-Control-Allow-Headers", "Content-Type")
        self.end_headers()

    def do_GET(self) -> None:
        try:
            self._dispatch_get()
        except Exception as e:
            _json_response(
                self,
                500,
                {"ok": False, "error": str(e), "trace": traceback.format_exc()[-800:]},
            )

    def do_POST(self) -> None:
        try:
            self._dispatch_post()
        except Exception as e:
            _json_response(
                self,
                500,
                {"ok": False, "error": str(e), "trace": traceback.format_exc()[-800:]},
            )

    def _path(self) -> Tuple[str, Dict[str, list]]:
        u = urlparse(self.path)
        return u.path.rstrip("/") or "/", parse_qs(u.query)

    def _dispatch_get(self) -> None:
        path, _qs = self._path()
        api = _api()

        if path in ("/health", "/api/health"):
            _json_response(
                self,
                200,
                {
                    "ok": True,
                    "service": "python_engine_api",
                    "workspace": str(ROOT),
                },
            )
            return

        if path == "/api/projects":
            projects = api.list_all_projects()
            active = api.active_project_info()
            _json_response(
                self,
                200,
                {"ok": True, "active": active, "projects": projects},
            )
            return

        if path == "/api/jobs":
            st = api.gen_job_status()
            running = api.gen_job_running()
            _json_response(
                self,
                200,
                {"ok": True, "running": running, "job": st},
            )
            return

        if path == "/api/stage2-status":
            _json_response(self, 200, {"ok": True, **api.stage2_status()})
            return

        _json_response(self, 404, {"ok": False, "error": f"not found: {path}"})

    def _dispatch_post(self) -> None:
        path, _qs = self._path()
        api = _api()
        body = _read_json(self)

        # /api/projects/{id}/activate
        if path.startswith("/api/projects/") and path.endswith("/activate"):
            pid = path[len("/api/projects/") : -len("/activate")].strip("/")
            if not pid:
                _json_response(self, 400, {"ok": False, "error": "missing project id"})
                return
            info = api.switch_project(pid)
            _json_response(self, 200, {"ok": True, "active": info})
            return

        if path == "/api/jobs/cancel":
            st = api.cancel_gen_job()
            _json_response(self, 200, {"ok": True, "job": st})
            return

        if path == "/api/jobs/gen-scene":
            pid = str(body.get("project_id") or body.get("project") or "").strip()
            scene = body.get("scene") or body.get("scene_num")
            if not scene:
                _json_response(self, 400, {"ok": False, "error": "scene required"})
                return
            if pid:
                api.switch_project(pid)
            if api.gen_job_running():
                _json_response(
                    self,
                    409,
                    {
                        "ok": False,
                        "error": "A generation job is already running",
                        "job": api.gen_job_status(),
                    },
                )
                return
            job = api.start_scene_gen_job(
                int(scene),
                only_missing=bool(body.get("only_missing", True)),
                run_qa=bool(body.get("run_qa", True)),
                remux=bool(body.get("remux", True)),
            )
            _json_response(
                self,
                202,
                {
                    "ok": True,
                    "message": f"Started scene {scene} generation",
                    "job": job,
                    "project": api.active_project_info(),
                },
            )
            return

        if path == "/api/jobs/gen-batch":
            pid = str(body.get("project_id") or "").strip()
            scenes = body.get("scenes") or body.get("scene_nums") or []
            if not isinstance(scenes, list) or not scenes:
                _json_response(self, 400, {"ok": False, "error": "scenes[] required"})
                return
            if pid:
                api.switch_project(pid)
            if api.gen_job_running():
                _json_response(
                    self,
                    409,
                    {"ok": False, "error": "job already running", "job": api.gen_job_status()},
                )
                return
            job = api.start_batch_gen_job(
                [int(s) for s in scenes],
                only_missing=bool(body.get("only_missing", True)),
                run_qa=bool(body.get("run_qa", True)),
                remux=bool(body.get("remux", True)),
                stop_on_fail=bool(body.get("stop_on_fail", True)),
            )
            _json_response(
                self,
                202,
                {"ok": True, "message": f"Started batch {scenes}", "job": job},
            )
            return

        _json_response(self, 404, {"ok": False, "error": f"not found: {path}"})


def main() -> int:
    ap = argparse.ArgumentParser(description="NickAndMe Python engine HTTP API")
    ap.add_argument("--host", default="127.0.0.1")
    ap.add_argument("--port", type=int, default=8765)
    args = ap.parse_args()
    # Smoke-import so boot fails fast if paths wrong
    try:
        _api()
    except Exception as e:
        print(f"[boot] pipeline_api import failed: {e}", file=sys.stderr)
        print(f"[boot] ROOT={ROOT} GUI={GUI}", file=sys.stderr)
        return 1
    httpd = ThreadingHTTPServer((args.host, args.port), Handler)
    print(f"Engine API http://{args.host}:{args.port}  (workspace {ROOT})")
    print("  GET  /health  /api/projects  /api/jobs  /api/stage2-status")
    print("  POST /api/jobs/gen-scene  /api/jobs/gen-batch  /api/jobs/cancel")
    try:
        httpd.serve_forever()
    except KeyboardInterrupt:
        print("\nshutdown")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
