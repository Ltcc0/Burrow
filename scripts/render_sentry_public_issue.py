#!/usr/bin/env python3
"""Render the only Sentry fields approved for a public GitHub issue.

The Sentry group payload is untrusted diagnostic data.  Keep every decision
about what may cross the public-issue boundary in this small, testable module.
Never add raw event payloads, exception messages, titles, culprits, tags,
breadcrumbs, request data, stack frames, or command arguments here.
"""

from __future__ import annotations

import argparse
from datetime import datetime, timezone
import json
import re
import sys
from typing import Any
from urllib.parse import urlsplit, urlunsplit


SAFE_TOKEN = re.compile(r"^[A-Za-z0-9_-]{1,80}$")
SAFE_SENTRY_PATH = re.compile(
    r"(?:/issues/[0-9]+/?|/organizations/[A-Za-z0-9_-]{1,80}/issues/[0-9]+/?)"
)
SAFE_LEVELS = {"debug", "info", "warning", "error", "fatal"}


class UnsafePublicIssueInput(ValueError):
    """Raised when a required public identifier fails closed."""


def _safe_token(value: Any, field: str) -> str:
    if not isinstance(value, str) or not SAFE_TOKEN.fullmatch(value):
        raise UnsafePublicIssueInput(f"unsafe {field}")
    return value


def _safe_level(value: Any) -> str:
    if not isinstance(value, str):
        return "error"
    normalized = value.lower()
    return normalized if normalized in SAFE_LEVELS else "error"


def _safe_count(value: Any) -> str:
    try:
        count = int(value)
    except (TypeError, ValueError):
        return "unknown"
    return str(count) if 0 <= count <= 1_000_000_000 else "unknown"


def _safe_timestamp(value: Any) -> str:
    if not isinstance(value, str) or len(value) > 64:
        return "unknown"
    try:
        parsed = datetime.fromisoformat(value.replace("Z", "+00:00"))
    except ValueError:
        return "unknown"
    if parsed.tzinfo is None:
        parsed = parsed.replace(tzinfo=timezone.utc)
    return parsed.astimezone(timezone.utc).isoformat(timespec="seconds").replace(
        "+00:00", "Z"
    )


def _safe_sentry_url(value: Any) -> str | None:
    if not isinstance(value, str) or len(value) > 2048:
        return None
    try:
        parsed = urlsplit(value)
    except ValueError:
        return None
    hostname = (parsed.hostname or "").lower()
    try:
        port = parsed.port
    except ValueError:
        return None
    if (
        parsed.scheme != "https"
        or parsed.username is not None
        or parsed.password is not None
        or port is not None
        or not (hostname == "sentry.io" or hostname.endswith(".sentry.io"))
        or not SAFE_SENTRY_PATH.fullmatch(parsed.path)
    ):
        return None
    return urlunsplit(("https", hostname, parsed.path, "", ""))


def _private_link(short_id: str, permalink: str | None) -> str:
    if permalink is None:
        return f"`{short_id}` (open the restricted Sentry project)"
    return f"[{short_id}]({permalink})"


def render_regular(group: dict[str, Any], project: str) -> dict[str, str]:
    short_id = _safe_token(group.get("shortId"), "Sentry short ID")
    safe_project = _safe_token(project, "project")
    level = _safe_level(group.get("level"))
    count = _safe_count(group.get("count"))
    first_seen = _safe_timestamp(group.get("firstSeen"))
    last_seen = _safe_timestamp(group.get("lastSeen"))
    permalink = _safe_sentry_url(group.get("permalink"))

    title = f"[Sentry] {short_id}: {level} in {safe_project}"
    body = "\n".join(
        [
            "Auto-filed from Sentry by the `sentry-issues` workflow.",
            "",
            "This public issue contains only privacy-reviewed group metadata. "
            "Open the restricted Sentry link for diagnostic details.",
            "",
            "| | |",
            "|---|---|",
            f"| **Private Sentry issue** | {_private_link(short_id, permalink)} |",
            f"| **Project** | `{safe_project}` |",
            f"| **Level** | {level} |",
            f"| **Events** | {count} |",
            f"| **First seen** | {first_seen} |",
            f"| **Last seen** | {last_seen} |",
            "",
            "Diagnostic payloads, stack frames, messages, local paths, user data, "
            "and command arguments are intentionally excluded from this public issue.",
            "",
            f"<sub>sentry-id: {short_id} — managed marker, do not edit or remove "
            "(prevents duplicate filings).</sub>",
            "",
        ]
    )
    return {"short_id": short_id, "title": title, "body": body}


def render_hang_row(group: dict[str, Any], project: str) -> dict[str, str]:
    short_id = _safe_token(group.get("shortId"), "Sentry short ID")
    safe_project = _safe_token(project, "project")
    count = _safe_count(group.get("count"))
    last_seen = _safe_timestamp(group.get("lastSeen"))
    permalink = _safe_sentry_url(group.get("permalink"))
    row = (
        f"| {_private_link(short_id, permalink)} | `{safe_project}` | "
        f"{count} | {last_seen} |"
    )
    marker = (
        f"<sub>sentry-id: {short_id} — managed marker, do not edit or remove.</sub>"
    )
    return {"short_id": short_id, "row": row, "marker": marker}


def _parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser()
    parser.add_argument("kind", choices=("regular", "hang-row"))
    parser.add_argument("--project", required=True)
    return parser.parse_args()


def main() -> int:
    args = _parse_args()
    try:
        payload = json.load(sys.stdin)
        if not isinstance(payload, dict):
            raise UnsafePublicIssueInput("Sentry group payload must be an object")
        rendered = (
            render_regular(payload, args.project)
            if args.kind == "regular"
            else render_hang_row(payload, args.project)
        )
    except (json.JSONDecodeError, UnsafePublicIssueInput) as error:
        print(f"render_sentry_public_issue: {error}", file=sys.stderr)
        return 2
    json.dump(rendered, sys.stdout, ensure_ascii=False)
    sys.stdout.write("\n")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
