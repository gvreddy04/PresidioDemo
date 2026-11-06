"""All feeds through the real workers, the generic decoders and local Presidio (V-07, V-08, V-05 through the harness)."""
import json
import struct
import unittest

from decoded_text_support import EXPECTED_REJECTIONS, PROJECT, baseline, payload, verify_published
from pipeline_harness import run
from test_feed_logs import leaked_values

SAMPLES = PROJECT / "docs" / "sample-docs"


def counted(text):
    """An XDR counted string: big-endian length, ASCII text, zero padding."""
    data = text.encode("ascii")
    return struct.pack(">I", len(data)) + data + b"\0" * (-len(data) % 4)


class DecodedTextFeedTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        inputs = {f"{p.parent.name}/{p.name}": p.read_bytes() for p in sorted(SAMPLES.rglob("*.txt"))}
        aci = (SAMPLES / "ACI" / "ACI.txt").read_bytes()
        # A template change: the producer appends a new 4+ character string after the last ACI field.
        cls.added_text = "NEWFIELD9"
        inputs["ACI/template-added-field.bin"] = payload(aci) + counted(cls.added_text)
        linking = json.loads(payload((SAMPLES / "PNR-Linking" / "PNR_LINKING.txt").read_bytes()))
        linking[0]["NewContactNote"] = "CALL JORDAN MILLER 3125550142"
        inputs["PNR-Linking/template-added-key.bin"] = json.dumps(linking, separators=(",", ":")).encode("utf-8")
        cls.inputs = inputs
        cls.harness = run(inputs, extra_args=["--detection-log", "on"], expect=2, prefix="decoded-text-")
        cls.addClassCleanup(cls.harness.cleanup)

    def test_eighteen_samples_published_and_two_rejected_with_their_reasons(self):
        items = [i for i in self.harness.report["Items"] if "template-added" not in i["Source"]]
        published = [i for i in items if i["Status"] == "Published"]
        rejected = {i["Source"].replace("\\", "/").rsplit("/", 1)[-1]: i["Reason"] for i in items if i["Status"] == "Rejected"}
        self.assertEqual(len(published), 18)
        self.assertEqual(set(rejected), set(EXPECTED_REJECTIONS))
        for name, reason in EXPECTED_REJECTIONS.items():
            self.assertIn(reason, rejected[name])

    def test_published_samples_keep_framing_pnrs_and_protect_every_baseline_value(self):
        document, exceptions = baseline()
        totals = {"protected": 0, "approved_exceptions": 0, "pnrs_unchanged": 0}
        for key, original in self.inputs.items():
            feed, name = key.split("/")
            if "template-added" in name or name in EXPECTED_REJECTIONS:
                continue
            counts = verify_published(feed, name, original, self.harness.published(feed, name), document, exceptions)
            for k in totals:
                totals[k] += counts[k]
        self.assertEqual(totals["protected"] + totals["approved_exceptions"], len(document["protected"]))
        self.assertEqual(totals["approved_exceptions"], len(exceptions))
        self.assertEqual(totals["pnrs_unchanged"], len(document["pnrs"]))

    def test_template_changes_still_publish_and_mask_the_new_value(self):
        aci = payload(self.harness.published("ACI", "template-added-field.bin"))
        self.assertTrue(aci.endswith(counted("*" * len(self.added_text))), "the added ACI string is masked in full")
        linking = json.loads(self.harness.published("PNR-Linking", "template-added-key.bin"))
        self.assertNotIn("JORDAN", linking[0]["NewContactNote"])
        self.assertEqual(len(linking[0]["NewContactNote"]), len("CALL JORDAN MILLER 3125550142"))

    def test_json_names_use_todays_replacement_token(self):
        linking = json.loads(payload(self.harness.published("PNR-Linking", "PNR_LINKING.txt")))
        self.assertEqual({c["FirstName"] for c in linking[0]["Customers"] if c.get("FirstName")}, {"[PASSENGER_NAME]"})

    def test_detection_log_through_the_harness_is_value_free(self):
        lines = [line.split(" DETECT ", 1)[1] for line in self.harness.console.splitlines() if " DETECT " in line]
        self.assertGreater(len(lines), 400)
        leaked = leaked_values("\n".join(lines))
        self.assertEqual(leaked, [])


if __name__ == "__main__":
    unittest.main()
