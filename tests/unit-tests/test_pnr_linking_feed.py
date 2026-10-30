"""Review supplied PNR-Linking protection, schema changes and truncated input."""
import json
import unittest

from pipeline_harness import PROJECT, run
from test_seats_feed import payload, header, verify_output
from configuration_support import sample

FIXTURE = PROJECT / "tests/unit-tests/fixtures/pnr-linking-mq-export.txt"


def supplied_with(change):
    document = json.loads(payload(sample("PNR_LINKING.txt").read_bytes()))
    change(document[0])
    return json.dumps(document).encode()


def transition_text():
    return supplied_with(lambda d: d.update(transitionLinkSegments="emily.carter@example.com"))


def transition_object():
    return supplied_with(lambda d: d.update(transitionLinkSegments=[{"UnreviewedEmail": "emily.carter@example.com"}]))


def numeric_id():
    return supplied_with(lambda d: d["Customers"][0].update(PaxId=1234567))


def truncated():
    supplied = sample("PNR_LINKING.txt").read_bytes()
    return header(supplied) + b"X " + payload(supplied)[:1523].hex().upper().encode() + b"\r\n"


def unconfigured():
    document = json.loads(payload(FIXTURE.read_bytes()))
    document[0]["Customers"][0]["UnreviewedEmail"] = "emily.carter@example.com"
    return json.dumps(document).encode('ascii')


class PnrLinkingFeedTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.harness = run({"PNR-Linking/complete-linking.txt": FIXTURE.read_bytes(),
                           "PNR-Linking/supplied-linking.txt": sample("PNR_LINKING.txt").read_bytes(),
                           "PNR-Linking/transition-text.bin": transition_text(),
                           "PNR-Linking/transition-object.bin": transition_object(),
                           "PNR-Linking/numeric-id.bin": numeric_id(),
                           "PNR-Linking/original-truncated.txt": truncated(),
                           "PNR-Linking/unconfigured-linking.bin": unconfigured()}, expect=2, prefix="pnr-linking-")
        cls.addClassCleanup(cls.harness.cleanup)

    def test_complete_mq_export_protects_every_configured_field_and_preserves_grouping(self):
        original = FIXTURE.read_bytes()
        output = self.harness.published("PNR-Linking", "complete-linking.txt")
        result = verify_output(original, output, "PNR-Linking")
        self.assertGreater(result["actions"]["Replace"], 0)
        data = json.loads(payload(output))
        self.assertEqual(data[0]["PNROwner"], "K7QX2M")
        self.assertEqual(len(data[0]["Customers"]), 2)
        self.assertTrue(all(person["UaRecordLocator"] == "K7QX2M" for person in data[0]["Customers"]))
        for value in [b"Emily", b"Carter", b"Michael", b"Brooks", b"PAX-1001", b"PAX-1002", b"192.0.2.10"]:
            self.assertNotIn(value, payload(output))

    def test_supplied_complete_linking_file_protects_both_passengers_and_preserves_nulls(self):
        original = sample("PNR_LINKING.txt").read_bytes()
        output = self.harness.published("PNR-Linking", "supplied-linking.txt")
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
        output = self.harness.published("PNR-Linking", "transition-text.bin")
        self.assertEqual(json.loads(output)[0]["transitionLinkSegments"], "[PROTECTED_ATTRIBUTE]")
        self.assertNotIn(b"emily.carter@example.com", output)
        self.harness.assert_rejected("PNR-Linking", "transition-object.bin", transition_object(),
                                     "without a configured protection policy")

    def test_numeric_sensitive_field_rejects_instead_of_changing_json_type(self):
        self.harness.assert_rejected("PNR-Linking", "numeric-id.bin", numeric_id(),
                                     "Non-string JSON fields must remain unchanged")

    def test_truncated_linking_file_is_retained_without_inventing_its_tail(self):
        self.harness.assert_rejected("PNR-Linking", "original-truncated.txt", truncated(), "malformed JSON")

    def test_complete_unconfigured_personal_field_rejects_the_whole_message(self):
        self.harness.assert_rejected("PNR-Linking", "unconfigured-linking.bin", unconfigured(),
                                     "without a configured protection policy")


if __name__ == "__main__":
    unittest.main()
