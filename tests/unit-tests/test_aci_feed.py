"""ACI sequence checks, including configuration-only field binding changes."""
from pathlib import Path
import tempfile
import unittest
import xml.etree.ElementTree as ET

from pipeline_harness import PROJECT, RUNS, run
from test_seats_feed import payload


from reference_feed_support import field_spans as reference_field_spans, replace_fields, verify_output as verify_reference_output
from configuration_support import sample


def field_spans():
    return reference_field_spans("ACI")


def verify_output(original, protected, policy_file=None, raw=False):
    return verify_reference_output("ACI", original, protected, policy_file, raw)


def changed_values():
    return replace_fields("ACI", payload(sample("ACI.txt").read_bytes()), {
        "/passengerName": "CARTER/EMMA", "/unresolvedIdentifier": "EMMAID00001", "/pnr": "K7QX2M",
        "/unresolvedRecordReference/pnr": "K7QX2M", "/unresolvedRecordReference/Prefix": "000000002"})


def changed_layout():
    raw = bytearray(payload(sample("ACI.txt").read_bytes()))
    raw[3] = 3  # A different feed discriminator does not match this sequence.
    return bytes(raw)


class AciFeedTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.harness = run({"ACI/original-aci.txt": sample("ACI.txt").read_bytes(),
                           "ACI/changed-values.bin": changed_values(),
                           "ACI/changed-layout.bin": changed_layout()}, expect=2, prefix="aci-reference-")
        cls.addClassCleanup(cls.harness.cleanup)

    def test_actual_mq_export_preserves_all_bytes_except_configured_masks(self):
        original = sample("ACI.txt").read_bytes()
        self.assertEqual(verify_output(original, self.harness.published("ACI", "original-aci.txt")), {"Keep": 20, "Mask": 3})
        self.assertFalse(any("analyzed |" in line and "3 known | 0 analyzed" not in line
                             for line in self.harness.stdout.splitlines() if "ACI" in line and "FIELDS" in line))

    def test_raw_payload_and_different_values_use_the_same_field_configuration(self):
        raw = changed_values()
        self.assertEqual(verify_output(raw, self.harness.published("ACI", "changed-values.bin"), raw=True), {"Keep": 20, "Mask": 3})

    def test_changed_unclassified_binary_region_is_retained(self):
        self.harness.assert_rejected("ACI", "changed-layout.bin", changed_layout(), "expected constant")

    def test_configuration_can_keep_an_ambiguous_field_without_changing_other_actions(self):
        RUNS.mkdir(exist_ok=True)
        with tempfile.TemporaryDirectory(prefix="aci-policy-", dir=RUNS) as temp:
            schema = ET.parse(PROJECT / "src/PresidioDemo.Core/Configuration/feed-policies.xml")
            field = schema.find("feed[@type='ACI']/field[@name='/unresolvedIdentifier']")
            field.attrib.clear()
            field.attrib.update(name="/unresolvedIdentifier", identification="None", action="Keep")
            schema_file = Path(temp) / "schema.xml"
            schema.write(schema_file, encoding="utf-8", xml_declaration=True)
            original = sample("ACI.txt").read_bytes()
            harness = run({"ACI/ACI.txt": original}, ["--policy-file", schema_file], expect=0, prefix="aci-policy-")
            try:
                self.assertEqual(len(harness.report["Items"]), 1)
                output = harness.published("ACI", "ACI.txt")
                self.assertEqual(verify_reference_output("ACI", original, output, policy_file=schema_file), {"Keep": 21, "Mask": 2})
                self.assertEqual((harness.input_root / "ACI/ACI.txt").read_bytes(), original)
            finally:
                harness.cleanup()


if __name__ == "__main__":
    unittest.main()
