"""Review supplied PNR-Linking protection, schema changes and truncated input."""
from pathlib import Path
import json
import tempfile
import unittest

from test_feed_listener import PROJECT, Listener, wait_for, rejected, runtime_root, processing_files
from test_seats_feed import payload, header, verify_output


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
                 and not processing_files(self.docs, "PNR-Linking", inbox.name), "complete linking publication and acknowledgment")
        output = next(outdir.glob("complete-linking.*.txt")).read_bytes()
        result = verify_output(original, output, "PNR-Linking")
        self.assertGreater(result["actions"]["Replace"], 0)
        data = json.loads(payload(output))
        self.assertEqual(data[0]["PNROwner"], "K7QX2M")
        self.assertEqual(len(data[0]["Customers"]), 2)
        self.assertTrue(all(person["UaRecordLocator"] == "K7QX2M" for person in data[0]["Customers"]))
        for value in [b"Emily", b"Carter", b"Michael", b"Brooks", b"PAX-1001", b"PAX-1002", b"192.0.2.10"]:
            self.assertNotIn(value, payload(output))

    def test_supplied_complete_linking_file_protects_both_passengers_and_preserves_nulls(self):
        original = (PROJECT / "docs/sample-docs/PNR_LINKING.txt").read_bytes()
        inbox = self.docs / "input-feeds/PNR-Linking/supplied-linking.txt"
        inbox.write_bytes(original)
        outdir = self.docs / "output-feeds/PNR-Linking"
        wait_for(lambda: len(list(outdir.glob("supplied-linking.*.txt"))) == 1 and not inbox.exists()
                 and not processing_files(self.docs, "PNR-Linking", inbox.name), "supplied linking acknowledgment")
        output = next(outdir.glob("supplied-linking.*.txt")).read_bytes()
        result = verify_output(original, output, "PNR-Linking")
        self.assertEqual(result["unique_paths"], 49)
        self.assertEqual(result["scalar_values"], 76)
        self.assertEqual(result["actions"], {"Replace": 22, "Keep": 54})
        before, after = json.loads(payload(original))[0], json.loads(payload(output))[0]
        self.assertEqual(after["PNROwner"], before["PNROwner"])
        self.assertEqual(len(after["Customers"]), 2)
        self.assertIsNone(after["transitionLinkSegments"])
        self.assertIsNone(after["UserId"])
        for person, protected in zip(before["Customers"], after["Customers"]):
            self.assertEqual(protected["UaRecordLocator"], person["UaRecordLocator"])
            self.assertEqual(protected["PaxId"], "[PASSENGER_ID]")
            self.assertEqual(protected["FirstName"], "[PASSENGER_NAME]")
            self.assertIsNone(protected["UaLoyaltyAcctNbr"])
            self.assertEqual(protected["FlightLeg"], person["FlightLeg"])

    def test_transition_scalar_text_is_protected_and_nested_unreviewed_fields_are_rejected(self):
        document = json.loads(payload((PROJECT / "docs/sample-docs/PNR_LINKING.txt").read_bytes()))
        document[0]["transitionLinkSegments"] = "emily.carter@example.com"
        inbox = self.docs / "input-feeds/PNR-Linking/transition-text.bin"
        inbox.write_bytes(json.dumps(document).encode())
        outdir = self.docs / "output-feeds/PNR-Linking"
        wait_for(lambda: len(list(outdir.glob("transition-text.*.bin"))) == 1 and not inbox.exists()
                 and not processing_files(self.docs, "PNR-Linking", inbox.name), "transition scalar protection")
        output = next(outdir.glob("transition-text.*.bin")).read_bytes()
        self.assertEqual(json.loads(output)[0]["transitionLinkSegments"], "[PROTECTED_ATTRIBUTE]")
        self.assertNotIn(b"emily.carter@example.com", output)
        document[0]["transitionLinkSegments"] = [{"UnreviewedEmail": "emily.carter@example.com"}]
        raw = json.dumps(document).encode()
        (self.docs / "input-feeds/PNR-Linking/transition-object.bin").write_bytes(raw)
        rejected(self.docs, "PNR-Linking", "transition-object.bin", raw, "without a configured protection policy")

    def test_numeric_sensitive_field_rejects_instead_of_changing_json_type(self):
        document = json.loads(payload((PROJECT / "docs/sample-docs/PNR_LINKING.txt").read_bytes()))
        document[0]["Customers"][0]["PaxId"] = 1234567
        raw = json.dumps(document).encode()
        (self.docs / "input-feeds/PNR-Linking/numeric-id.bin").write_bytes(raw)
        rejected(self.docs, "PNR-Linking", "numeric-id.bin", raw, "Non-string JSON fields must remain unchanged")

    def test_truncated_linking_file_is_retained_without_inventing_its_tail(self):
        supplied = (PROJECT / "docs/sample-docs/PNR_LINKING.txt").read_bytes()
        original = header(supplied) + b"X " + payload(supplied)[:1523].hex().upper().encode() + b"\r\n"
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
