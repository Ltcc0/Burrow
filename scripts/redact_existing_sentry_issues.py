#!/usr/bin/env python3
"""Build allowlisted patches for historical Sentry-created GitHub issues.

Input is the JSON array returned by:

    gh issue list --label sentry --state all --json number,title,body

Output is JSON Lines containing only issue number plus a replacement title and
body. The original title/body are never copied to stdout. Multi-marker digests,
manual issues, and already privacy-reviewed issues are deliberately skipped.
"""

from __future__ import annotations

import json
import re
import sys
from typing import Any

from render_sentry_public_issue import _safe_sentry_url


AUTO_FILED_PREFIX = "Auto-filed from Sentry by the `sentry-issues` workflow."
MARKER_PATTERN = re.compile(
    r"sentry-id: ([A-Za-z0-9_-]{1,80})(?=[ <\n\r])"
)
PRIVACY_REVIEWED_MARKER = "privacy-reviewed group metadata"


def _historical_link(body: str, short_id: str) -> str | None:
    match = re.search(rf"\[{re.escape(short_id)}\]\(([^\s)]+)\)", body)
    return _safe_sentry_url(match.group(1)) if match else None


def build_patch(issue: dict[str, Any]) -> dict[str, Any] | None:
    number = issue.get("number")
    title = issue.get("title")
    body = issue.get("body")
    if (
        not isinstance(number, int)
        or number <= 0
        or not isinstance(title, str)
        or not isinstance(body, str)
        or not body.startswith(AUTO_FILED_PREFIX)
        or PRIVACY_REVIEWED_MARKER in body
    ):
        return None

    short_ids = MARKER_PATTERN.findall(body)
    if len(short_ids) != 1:
        return None
    short_id = short_ids[0]
    permalink = _historical_link(body, short_id)
    private_reference = (
        f"[{short_id}]({permalink})"
        if permalink is not None
        else f"`{short_id}` (open the restricted Sentry project)"
    )
    safe_title = f"[Sentry] {short_id}: diagnostics in private Sentry"
    safe_body = "\n".join(
        [
            AUTO_FILED_PREFIX,
            "",
            "This historical issue was redacted to the public metadata allowlist. "
            "Open the restricted Sentry project for diagnostic details.",
            "",
            "| | |",
            "|---|---|",
            f"| **Private Sentry issue** | {private_reference} |",
            "",
            "Diagnostic payloads, stack frames, messages, local paths, user data, "
            "and command arguments are intentionally excluded from this public issue.",
            "",
            f"<sub>sentry-id: {short_id} — managed marker, do not edit or remove "
            "(prevents duplicate filings).</sub>",
            "",
        ]
    )
    if title == safe_title and body == safe_body:
        return None
    return {"number": number, "title": safe_title, "body": safe_body}


def main() -> int:
    try:
        issues = json.load(sys.stdin)
    except json.JSONDecodeError as error:
        print(f"redact_existing_sentry_issues: {error}", file=sys.stderr)
        return 2
    if not isinstance(issues, list):
        print("redact_existing_sentry_issues: input must be an array", file=sys.stderr)
        return 2

    for issue in issues:
        if not isinstance(issue, dict):
            continue
        patch = build_patch(issue)
        if patch is not None:
            json.dump(patch, sys.stdout, ensure_ascii=False)
            sys.stdout.write("\n")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
