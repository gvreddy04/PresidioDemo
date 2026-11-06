"""Unit checks for the local detection bridge: spans only, per-feed registries, fail-closed timeouts."""
from pathlib import Path
import json
import subprocess
import sys
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "src" / "PresidioDemo.Core" / "Protection" / "Shared" / "python"))
import presidio_demo


SPIKE = Path(__file__).resolve().parents[2] / "tests" / "DetectionSpike" / "bin" / "Debug" / "net10.0" / "DetectionSpike.dll"


def spans(feed, *texts):
    """Return (entity, matched text, recognizer) per input string from the configured feed."""
    result = json.loads(presidio_demo.analyze_strings(feed, json.dumps(list(texts))))
    return [[(entity, text[start:end], name) for start, end, entity, score, name in found] for text, found in zip(texts, result)]


class DetectionBridgeTests(unittest.TestCase):
    """The batch detection bridge (T-006): spans only, per-feed registries, no protection decisions."""

    @classmethod
    def setUpClass(cls):
        run = subprocess.run(["dotnet", str(SPIKE), "bridge-json"], capture_output=True, text=True, timeout=300)
        if run.returncode != 0:
            raise AssertionError("Build tests/DetectionSpike first: " + run.stdout + run.stderr)
        cls.feeds = [presidio_demo.configure_feed(line) for line in run.stdout.splitlines() if line.strip()]
        presidio_demo.configure_feed(json.dumps({"feed": "OnlyA", "builtIns": [],
            "formats": [{"name": "demoCode", "entity": "DEMO_CODE", "score": 0.9, "patterns": [r"DEMO-\d{4}"], "context": []}]}))
        presidio_demo.configure_feed(json.dumps({"feed": "OnlyB", "builtIns": [], "formats": []}))

    def test_all_five_feeds_configure(self):
        self.assertEqual(sorted(self.feeds), ["ACI", "ETKT", "PNR", "PNR-Linking", "Seats"])

    def test_docs_record_returns_name_date_of_birth_and_gender_spans(self):
        found = spans("PNR", "DOCSDAHK1/////12MAR85/F//MILLER/JORDAN-1MILLER/JORDAN")[0]
        self.assertIn(("DATE_OF_BIRTH", "12MAR85", "dateOfBirth"), found)
        self.assertIn(("GENDER", "F", "gender"), found)
        self.assertEqual([t for e, t, n in found if n == "passengerName"], ["MILLER/JORDAN", "MILLER/JORDAN"])

    def test_contact_records_return_email_and_phone(self):
        email, phone = spans("PNR", "CTCEDAHK1/JORDAN.MILLER//DEMOAIR.EXAMPLE-1MILLER/JORDAN", "CTCMDAHK1/3125550142-1MILLER/JORDAN")
        self.assertIn(("EMAIL_ADDRESS", "JORDAN.MILLER//DEMOAIR.EXAMPLE", "ctceEmail"), email)
        self.assertIn(("PHONE_NUMBER", "3125550142", "ctcmPhone"), phone)

    def test_built_in_email_is_found_in_free_text(self):
        found = spans("Seats", "Contact emily.carter@example.com today")[0]
        self.assertIn("EMAIL_ADDRESS", [entity for entity, text, name in found])

    def test_a_format_applies_only_to_its_own_feed(self):
        self.assertEqual(spans("OnlyA", "ref DEMO-1234")[0], [("DEMO_CODE", "DEMO-1234", "demoCode")])
        self.assertEqual(spans("OnlyB", "ref DEMO-1234")[0], [])
        self.assertNotIn("dateOfBirth", [n for e, t, n in spans("Seats", "DOCSDAHK1/////12MAR85/F//")[0]])

    def test_a_pattern_timeout_raises_instead_of_skipping_the_pattern(self):
        # Presidio would skip a timed-out pattern and return no span; the bridge raises so .NET dead-letters (WR-016).
        presidio_demo.configure_feed(json.dumps({"feed": "Slow", "builtIns": [],
            "formats": [{"name": "slow", "entity": "DEMO_CODE", "score": 0.9, "patterns": [r"(A|AA)+$"], "context": []}]}))
        with self.assertRaises(TimeoutError):
            presidio_demo.analyze_strings("Slow", json.dumps(["A" * 40 + "!"]))
        self.assertEqual(len(spans("ACI", "MILLER/JORDAN")[0]), 1, "the next call works normally")

    def test_batch_keeps_one_result_per_string_and_rejects_bad_input(self):
        self.assertEqual(len(spans("ACI", "20261005", "MILLER/JORDAN", "")), 3)
        with self.assertRaises(ValueError):
            presidio_demo.analyze_strings("ACI", json.dumps([1, 2]))
        with self.assertRaises(ValueError):
            presidio_demo.analyze_strings("NoSuchFeed", "[]")


if __name__ == "__main__":
    unittest.main()
