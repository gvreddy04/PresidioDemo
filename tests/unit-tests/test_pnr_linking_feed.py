"""Complete configured PNR-Linking delivery and safe retention of the truncated original."""
from pathlib import Path
import json
import tempfile
import unittest

from test_feed_listener import PROJECT, Listener, wait_for, rejected, runtime_root
from test_seats_feed import payload, verify_output


class PnrLinkingFeedTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        (PROJECT / ".test-runs").mkdir(exist_ok=True)
        cls.sandbox = tempfile.TemporaryDirectory(prefix="pnr-linking-", dir=PROJECT / ".test-runs")
        cls.docs = Path(cls.sandbox.name) / "docs"
        try:
            cls.listener = Listener(cls.docs)
        except BaseException:
            cls.sandbox.cleanup()
            raise

    @classmethod
    def tearDownClass(cls):
        try:
            cls.listener.stop()
        finally:
            cls.sandbox.cleanup()

    def test_complete_mq_export_protects_every_configured_field_and_preserves_grouping(self):
        original = (PROJECT / "tests/unit-tests/fixtures/pnr-linking-mq-export.txt").read_bytes()
        inbox = self.docs / "input-feeds/PNR-Linking/complete-linking.txt"
        inbox.write_bytes(original)
        outdir = self.docs / "output-feeds/PNR-Linking"
        wait_for(lambda: len(list(outdir.glob("complete-linking.*.txt"))) == 1 and not inbox.exists()
                 and not list((runtime_root(self.docs) / "processing/PNR-Linking").rglob(inbox.name)), "complete linking publication and acknowledgment")
        output = next(outdir.glob("complete-linking.*.txt")).read_bytes()
        result = verify_output(original, output, "PNR-Linking")
        self.assertGreater(result["actions"]["Replace"], 0)
        data = json.loads(payload(output))
        self.assertEqual(data[0]["PNROwner"], "K7QX2M")
        self.assertEqual(len(data[0]["Customers"]), 2)
        self.assertTrue(all(person["UaRecordLocator"] == "K7QX2M" for person in data[0]["Customers"]))
        for value in [b"Emily", b"Carter", b"Michael", b"Brooks", b"PAX-1001", b"PAX-1002", b"192.0.2.10"]:
            self.assertNotIn(value, payload(output))

    def test_supplied_truncated_linking_file_is_retained_without_inventing_its_tail(self):
        original = (PROJECT / "docs/sample-docs/PNR_LINKING.txt").read_bytes()
        (self.docs / "input-feeds/PNR-Linking/original-truncated.txt").write_bytes(original)
        rejected(self.docs, "PNR-Linking", "original-truncated.txt", original, "malformed JSON")

    def test_complete_unconfigured_personal_field_rejects_the_whole_message(self):
        document = json.loads(payload((PROJECT / "tests/unit-tests/fixtures/pnr-linking-mq-export.txt").read_bytes()))
        document[0]["Customers"][0]["UnreviewedEmail"] = "emily.carter@example.com"
        raw = json.dumps(document).encode('ascii')
        (self.docs / "input-feeds/PNR-Linking/unconfigured-linking.bin").write_bytes(raw)
        rejected(self.docs, "PNR-Linking", "unconfigured-linking.bin", raw, "without a configured protection policy")


if __name__ == "__main__":
    unittest.main()
