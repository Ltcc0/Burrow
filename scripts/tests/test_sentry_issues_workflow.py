import importlib.util
import json
import sys
import unittest
from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]
WORKFLOW = ROOT / ".github" / "workflows" / "sentry-issues.yml"
RENDERER = ROOT / "scripts" / "render_sentry_public_issue.py"
REDACTOR = ROOT / "scripts" / "redact_existing_sentry_issues.py"
FIXTURE = ROOT / "scripts" / "tests" / "fixtures" / "sentry_sensitive_group.json"

SPEC = importlib.util.spec_from_file_location("render_sentry_public_issue", RENDERER)
assert SPEC is not None and SPEC.loader is not None
renderer = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = renderer
SPEC.loader.exec_module(renderer)

REDACTOR_SPEC = importlib.util.spec_from_file_location(
    "sentry_public_redactor", REDACTOR
)
assert REDACTOR_SPEC is not None and REDACTOR_SPEC.loader is not None
redactor = importlib.util.module_from_spec(REDACTOR_SPEC)
REDACTOR_SPEC.loader.exec_module(redactor)


class SentryIssuesWorkflowTests(unittest.TestCase):
    def test_app_hangs_are_aggregated_instead_of_silently_skipped(self) -> None:
        workflow = WORKFLOW.read_text(encoding="utf-8")

        self.assertNotIn("Skipping App-Hang issue", workflow)
        self.assertIn("[Sentry] App-Hang digest", workflow)
        self.assertIn("gh issue edit", workflow)
        self.assertIn("jq -r '.marker'", workflow)
        self.assertIn("sentry-id: {short_id}", RENDERER.read_text(encoding="utf-8"))

    def test_hang_digests_are_bounded_and_roll_into_numbered_parts(self) -> None:
        workflow = WORKFLOW.read_text(encoding="utf-8")

        self.assertIn('MAX_HANG_GROUPS_PER_RUN: "20"', workflow)
        self.assertIn('MAX_ISSUE_BODY_BYTES: "60000"', workflow)
        self.assertIn('digest_title="${digest_base_title} — part ${digest_part}"', workflow)
        self.assertIn("existing_bytes + section_bytes", workflow)

    def test_sentry_issue_poll_follows_cursor_pagination(self) -> None:
        workflow = WORKFLOW.read_text(encoding="utf-8")

        self.assertIn('MAX_SENTRY_PAGES_PER_PROJECT: "100"', workflow)
        self.assertIn('-D "$headers" -o "$page"', workflow)
        self.assertIn("'rel=\"next\"'", workflow)
        self.assertIn("'results=\"true\"'", workflow)
        self.assertIn('done < "$response_rows"', workflow)

    def test_hang_digest_uses_the_privacy_reviewed_renderer(self) -> None:
        workflow = WORKFLOW.read_text(encoding="utf-8")

        self.assertIn("render_sentry_public_issue.py \\\n                     hang-row", workflow)
        self.assertIn("jq -r '.row'", workflow)
        self.assertIn("jq -r '.marker'", workflow)

    def test_regular_issues_use_the_privacy_reviewed_renderer(self) -> None:
        workflow = WORKFLOW.read_text(encoding="utf-8")

        self.assertIn("python3 scripts/render_sentry_public_issue.py", workflow)
        self.assertIn('regular --project "$project"', workflow)
        self.assertIn("--title \"$title\"", workflow)
        self.assertIn("--body \"$body\"", workflow)

    def test_workflow_never_fetches_or_formats_event_diagnostics(self) -> None:
        workflow = WORKFLOW.read_text(encoding="utf-8")

        self.assertNotIn("events/latest", workflow)
        self.assertNotIn("TRACE_JQ", workflow)
        self.assertNotIn("${trace}", workflow)
        self.assertNotIn("Stack trace (most recent event)", workflow)
        self.assertNotIn("head -c", workflow)
        self.assertNotIn("event:read", workflow)

    def test_renderer_is_checked_out_before_the_workflow_uses_it(self) -> None:
        workflow = WORKFLOW.read_text(encoding="utf-8")

        checkout = "actions/checkout@df4cb1c069e1874edd31b4311f1884172cec0e10"
        self.assertIn(checkout, workflow)
        renderer_call = "python3 scripts/render_sentry_public_issue.py"
        self.assertLess(workflow.index(checkout), workflow.index(renderer_call))

    def test_historical_redaction_is_explicit_and_routes_through_redactor(self) -> None:
        workflow = WORKFLOW.read_text(encoding="utf-8")

        self.assertIn("redact_existing:", workflow)
        self.assertIn("type: boolean", workflow)
        self.assertIn("default: false", workflow)
        self.assertIn('if [ "$REDACT_EXISTING" = "true" ]', workflow)
        self.assertIn("python3 scripts/redact_existing_sentry_issues.py", workflow)
        self.assertIn('gh issue edit "$issue_number"', workflow)


class SentryPublicIssueRendererTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        cls.sensitive_group = json.loads(FIXTURE.read_text(encoding="utf-8"))

    def assertSensitiveValuesAbsent(self, rendered: dict[str, str]) -> None:
        output = json.dumps(rendered, ensure_ascii=False)
        for value in (
            "/Users/alice",
            "alice@example.com",
            "sk_live_secret",
            "hunter2",
            "sentry-secret-token",
            "203.0.113.42",
            "browser-secret",
            "deleteCustomerDatabase",
            "Secrets.swift",
            "rm -rf",
            "device-private-identifier",
            "private-secret",
            "event-identifier",
        ):
            self.assertNotIn(value, output)

    def test_sensitive_fixture_does_not_cross_regular_issue_boundary(self) -> None:
        rendered = renderer.render_regular(self.sensitive_group, "burrow-windows")

        self.assertSensitiveValuesAbsent(rendered)
        self.assertEqual(
            rendered["title"],
            "[Sentry] BURROW-WINDOWS-SECRET-7: fatal in burrow-windows",
        )
        self.assertIn("https://henry-zhang-r7.sentry.io/issues/7639318005/", rendered["body"])
        self.assertIn("privacy-reviewed group metadata", rendered["body"])
        self.assertTrue(rendered["body"].rstrip().endswith("</sub>"))

    def test_sensitive_fixture_does_not_cross_hang_digest_boundary(self) -> None:
        rendered = renderer.render_hang_row(self.sensitive_group, "burrow")

        self.assertSensitiveValuesAbsent(rendered)
        self.assertEqual(rendered["short_id"], "BURROW-WINDOWS-SECRET-7")
        self.assertIn("| `burrow` | 3 | 2026-08-07T04:05:06Z |", rendered["row"])

    def test_untrusted_required_identifiers_fail_closed(self) -> None:
        unsafe = dict(self.sensitive_group, shortId="BAD\n| injected |")

        with self.assertRaises(renderer.UnsafePublicIssueInput):
            renderer.render_regular(unsafe, "burrow")
        with self.assertRaises(renderer.UnsafePublicIssueInput):
            renderer.render_hang_row(self.sensitive_group, "../burrow")

    def test_non_sentry_or_credentialed_links_are_not_published(self) -> None:
        for permalink in (
            "https://example.com/issues/1",
            "http://sentry.io/issues/1",
            "https://user:password@sentry.io/issues/1",
            "https://acme.sentry.io:443/issues/1",
            "https://acme.sentry.io/issues/1/)%0Asecret",
        ):
            unsafe = dict(self.sensitive_group, permalink=permalink)
            rendered = renderer.render_regular(unsafe, "burrow")
            self.assertNotIn(permalink, rendered["body"])
            self.assertIn("open the restricted Sentry project", rendered["body"])

    def test_supported_organization_link_is_canonicalized(self) -> None:
        issue = dict(
            self.sensitive_group,
            permalink=(
                "https://sentry.io/organizations/acme/issues/123/"
                "?project=456#private-token"
            ),
        )

        rendered = renderer.render_regular(issue, "burrow")

        self.assertIn(
            "https://sentry.io/organizations/acme/issues/123/", rendered["body"]
        )
        self.assertNotIn("project=456", rendered["body"])
        self.assertNotIn("private-token", rendered["body"])


class ExistingSentryIssueRedactorTests(unittest.TestCase):
    def setUp(self) -> None:
        self.sensitive_body = "\n".join(
            [
                redactor.AUTO_FILED_PREFIX,
                "",
                "| **Sentry issue** | "
                "[BURROW-WINDOWS-SECRET-7](https://acme.sentry.io/issues/123/?token=secret) |",
                "| **Project** | `burrow-windows` |",
                "",
                "<details><summary>Stack trace (most recent event)</summary>",
                "```",
                "deleteCustomerDatabase (/Users/alice/Secrets.swift:42)",
                "Authorization: Bearer sentry-secret-token",
                "```</details>",
                "",
                "<sub>sentry-id: BURROW-WINDOWS-SECRET-7 — managed marker, "
                "do not edit or remove (prevents duplicate filings).</sub>",
                "",
            ]
        )

    def test_historical_patch_keeps_only_validated_reference_and_marker(self) -> None:
        patch = redactor.build_patch(
            {
                "number": 291,
                "title": "[Sentry] secret /Users/alice/private.db",
                "body": self.sensitive_body,
            }
        )

        self.assertIsNotNone(patch)
        assert patch is not None
        output = json.dumps(patch)
        self.assertEqual(patch["number"], 291)
        self.assertEqual(
            patch["title"],
            "[Sentry] BURROW-WINDOWS-SECRET-7: diagnostics in private Sentry",
        )
        self.assertIn("https://acme.sentry.io/issues/123/", patch["body"])
        for sensitive in (
            "/Users/alice",
            "deleteCustomerDatabase",
            "sentry-secret-token",
            "token=secret",
        ):
            self.assertNotIn(sensitive, output)

    def test_manual_multi_marker_and_reviewed_issues_are_skipped(self) -> None:
        manual = {
            "number": 1,
            "title": "Manual issue",
            "body": "Manual report\nsentry-id: BURROW-1 <end>",
        }
        digest = {
            "number": 2,
            "title": "[Sentry] App-Hang digest",
            "body": (
                f"{redactor.AUTO_FILED_PREFIX}\n"
                "sentry-id: BURROW-1 <end>\n"
                "sentry-id: BURROW-2 <end>\n"
            ),
        }
        reviewed = {
            "number": 3,
            "title": "[Sentry] BURROW-3: error in burrow",
            "body": (
                f"{redactor.AUTO_FILED_PREFIX}\n"
                "privacy-reviewed group metadata\n"
                "sentry-id: BURROW-3 <end>\n"
            ),
        }

        self.assertIsNone(redactor.build_patch(manual))
        self.assertIsNone(redactor.build_patch(digest))
        self.assertIsNone(redactor.build_patch(reviewed))


if __name__ == "__main__":
    unittest.main()
