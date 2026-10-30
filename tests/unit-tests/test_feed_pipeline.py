"""Verify MQ export routing, JSON protection and dead-lettered failures through the broker workers."""
import json
import unittest

from pipeline_harness import PROJECT, FEEDS, run
from configuration_support import sample
from reference_feed_support import verify_output as verify_reference_output
from test_aci_feed import verify_output as verify_aci_output
from test_seats_feed import verify_output as verify_json_output

FIXTURES = PROJECT / "tests" / "unit-tests" / "fixtures"
BINARY = b"\x00\x00\x00\x02"
# Malformed ACI exports and the reason each must report.
ACI_DUMPS = {
    "partial.txt": ((FIXTURES / "aci-mq-partial.txt").read_bytes(), "560 of 920"),
    "complete.txt": (b"AMQSBCG0 - starts here\nlength - 3 of 3 bytes\n00000000:  4142 43           'ABC'\n", "four-byte aligned"),
    "gap.txt": (b"AMQSBCG0 - starts here\nlength - 2 bytes\n00000010:  4142 'AB'\n", "out of order"),
    "plain.txt": (b"Emma Carter", "must be an IBM MQ diagnostic dump"),
}
PUBLISHED = {"Seats/seats.txt": (FIXTURES / "seats-mq-export.txt").read_bytes(),
             "PNR/original-pnr.txt": sample("PNR.txt").read_bytes(),
             "PNR-Linking/finalized-linking.txt": sample("PNR_LINKING.txt").read_bytes(),
             "ACI/finalized-aci.txt": sample("ACI.txt").read_bytes(),
             "ETKT/finalized-etkt.txt": sample("ETKT.txt").read_bytes(),
             "Seats/finalized-seats.txt": sample("SEATS.txt").read_bytes()}


def decode_export(text):
    return bytes.fromhex("".join(line[2:] for line in text.splitlines() if line.startswith("X ")))


class FeedPipelineTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        inputs = dict(PUBLISHED)
        for feed in FEEDS:
            inputs[feed + "/ignored.JSON"] = b'{"name":"Emma Carter","pnr":"K7QX2M"}'
            inputs[feed + "/unconfigured.BIN"] = BINARY
        inputs.update({"ACI/" + name: body for name, (body, _) in ACI_DUMPS.items()})
        # Two failed publishes are retried by redelivery and must not change any outcome.
        cls.harness = run(inputs, ["--fail-publishes", "2"], expect=2, prefix="feed-pipeline-")
        cls.addClassCleanup(cls.harness.cleanup)

    def test_json_inputs_are_skipped_in_all_five_feeds_and_remain_untouched(self):
        self.assertFalse([item for item in self.harness.report["Items"] if item["Source"].lower().endswith(".json")])
        for feed in FEEDS:
            self.assertTrue((self.harness.input_root / feed / "ignored.JSON").exists())
            for folder in ("published", "dead-letter", "logs"):
                self.assertFalse(list((self.harness.output_root / folder).rglob("ignored.*")))

    def test_raw_binary_routes_retain_unsupported_input_without_publishing(self):
        for feed in FEEDS:
            with self.subTest(feed):
                reason = {"Seats": "malformed JSON", "PNR-Linking": "malformed JSON", "ETKT": "unsupported structure"}.get(feed, "XDR")
                self.harness.assert_rejected(feed, "unconfigured.BIN", BINARY, reason)

    def test_malformed_ibm_mq_dumps_are_retained_with_original_bytes_and_a_reason(self):
        # Partial and gapped dumps fail closed; a complete dump is reconstructed, then fails its sequence check;
        # ordinary text is not silently treated as a binary feed.
        for name, (body, reason) in ACI_DUMPS.items():
            with self.subTest(name):
                self.harness.assert_rejected("ACI", name, body, reason)

    def test_seats_preserves_mq_metadata_pnr_passenger_grouping_types_and_seats(self):
        before_text = PUBLISHED["Seats/seats.txt"].decode("utf-8")
        output = self.harness.published("Seats", "seats.txt").decode("utf-8")
        self.assertEqual([x for x in output.splitlines() if x.startswith("A ")], [x for x in before_text.splitlines() if x.startswith("A ")])
        customers = json.loads(decode_export(output))["OrderChangeNotif"]["Old"]["Customers"]
        self.assertEqual(len(customers), 2)
        for customer in customers:
            self.assertEqual(customer["UARecloc"], "K7QX2M")
            self.assertEqual(customer["PaxId"], "[PASSENGER_ID]")
            self.assertTrue(customer["FirstName"] == customer["LastName"] == customer["BookedAsName"] == "[PASSENGER_NAME]")
            self.assertEqual(set(customer["ServiceItems"][0]["Attributes"]), {"[PROTECTED_ATTRIBUTE]"})
        self.assertEqual([x["ServiceItems"][0]["SeatNumber"] for x in customers], ["25C", "25D"])
        self.assertFalse(any(x in decode_export(output).decode() for x in ["Emily", "Carter", "Michael", "Brooks", "PAX-1001", "PAX-1002"]))

    def test_supplied_pnr_masks_selected_personal_data_and_preserves_pnr_record_syntax_and_mq_metadata(self):
        self.assertEqual(verify_reference_output("PNR", PUBLISHED["PNR/original-pnr.txt"],
                                                 self.harness.published("PNR", "original-pnr.txt")), {"Keep": 74, "Mask": 80})

    def test_supplied_pnr_linking_protects_both_passengers_and_preserves_pnr_nulls_and_mq_metadata(self):
        result = verify_json_output(PUBLISHED["PNR-Linking/finalized-linking.txt"],
                                    self.harness.published("PNR-Linking", "finalized-linking.txt"), "PNR-Linking")
        self.assertEqual(result["actions"], {"Keep": 54, "Replace": 22})
        self.assertEqual(result["unique_paths"], 49)

    def test_supplied_aci_is_protected_by_configuration_and_preserves_pnr_and_binary_framing(self):
        self.assertEqual(verify_aci_output(PUBLISHED["ACI/finalized-aci.txt"],
                                           self.harness.published("ACI", "finalized-aci.txt")), {"Keep": 20, "Mask": 3})

    def test_supplied_etkt_preserves_pnr_status_text_framing_and_mq_metadata_with_five_masks(self):
        self.assertEqual(verify_reference_output("ETKT", PUBLISHED["ETKT/finalized-etkt.txt"],
                                                 self.harness.published("ETKT", "finalized-etkt.txt")), {"Keep": 4, "Mask": 5})

    def test_unmodified_supplied_seats_export_is_published(self):
        protected = json.loads(decode_export(self.harness.published("Seats", "finalized-seats.txt").decode("utf-8")))
        original = json.loads(decode_export(PUBLISHED["Seats/finalized-seats.txt"].decode()))
        self.assertEqual(protected["OrderChangeNotif"]["Old"]["Customers"][0]["UARecloc"], original["OrderChangeNotif"]["Old"]["Customers"][0]["UARecloc"])
        self.assertEqual(protected["OrderChangeNotif"]["Old"]["Customers"][0]["FirstName"], "[PASSENGER_NAME]")

    def test_failed_publishes_are_retried_and_every_message_completes(self):
        items = self.harness.report["Items"]
        self.assertEqual(len(items), len(PUBLISHED) + len(FEEDS) + len(ACI_DUMPS))
        self.assertEqual(self.harness.report["PublishAttempts"], len(items) + 2)
        self.assertEqual({item["Status"] for item in items}, {"Published", "Rejected"})
        self.assertEqual(sum(item["Status"] == "Published" for item in items), len(PUBLISHED))

    def test_console_output_has_no_personal_values_and_no_token_vault_is_created(self):
        self.assertFalse(any(value in self.harness.console for value in ["Emma Carter", "CARTER/EMMA", "Emily", "PAX-1001"]))
        self.assertFalse(list(self.harness.input_root.parent.rglob(".demo-token-vault")))
        self.assertFalse((PROJECT / ".demo-token-vault").exists())


if __name__ == "__main__":
    unittest.main()
