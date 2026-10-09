"""Reference-layout ACI checks; these do not claim support for other airline layouts."""
from pathlib import Path
import json
import subprocess
import tempfile
import unittest
import xml.etree.ElementTree as ET

from test_feed_listener import PROJECT, DLL, Listener, wait_for, rejected, runtime_root
from test_seats_feed import payload, header


from reference_feed_support import field_spans as reference_field_spans, verify_output as verify_reference_output


def field_spans():
    return reference_field_spans("ACI")


def verify_output(original, protected, policy_file=None, raw=False):
    return verify_reference_output("ACI", original, protected, policy_file, raw)


class AciFeedTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        (PROJECT / ".test-runs").mkdir(exist_ok=True)
        cls.sandbox = tempfile.TemporaryDirectory(prefix="aci-reference-", dir=PROJECT / ".test-runs")
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

    def deliver(self, name, content):
        inbox = self.docs / "input-feeds/ACI" / name
        inbox.write_bytes(content)
        outputs = self.docs / "output-feeds/ACI"
        pattern = Path(name).stem + ".*" + Path(name).suffix
        wait_for(lambda: len(list(outputs.glob(pattern))) == 1 and not inbox.exists()
                 and not list((runtime_root(self.docs) / "processing/ACI").rglob(name)), "ACI output and acknowledgment")
        return next(outputs.glob(pattern)).read_bytes()

    def test_actual_mq_export_preserves_all_bytes_except_configured_masks(self):
        original = (PROJECT / "docs/sample-docs/ACI.txt").read_bytes()
        self.assertEqual(verify_output(original, self.deliver("original-aci.txt", original)), {"Keep": 17, "Mask": 6})
        self.assertFalse(any("analyzed |" in line and "6 known | 0 analyzed" not in line
                             for line in self.listener.lines if "ACI" in line and "FIELDS" in line))

    def test_raw_payload_and_different_values_use_the_same_field_configuration(self):
        raw = bytearray(payload((PROJECT / "docs/sample-docs/ACI.txt").read_bytes()))
        values = {"/PassengerName": "CARTER/EMMA", "/Text0840": "EMMAID00001",
                  "/pnr": "K7QX2M", "/RecordReference/pnr": "K7QX2M", "/RecordReference/Prefix": "000000002"}
        for name, start, length in field_spans():
            if name in values:
                encoded = values[name].encode("ascii")
                self.assertEqual(len(encoded), length)
                raw[start:start + length] = encoded
        self.assertEqual(verify_output(bytes(raw), self.deliver("changed-values.bin", raw), raw=True), {"Keep": 17, "Mask": 6})

    def test_changed_unclassified_binary_region_is_retained(self):
        raw = bytearray(payload((PROJECT / "docs/sample-docs/ACI.txt").read_bytes()))
        raw[403] = 2  # The reference frame has a fixed word here; a different collection shape is unreviewed.
        (self.docs / "input-feeds/ACI/changed-layout.bin").write_bytes(raw)
        rejected(self.docs, "ACI", "changed-layout.bin", bytes(raw), "unclassified binary region")

    def test_configuration_can_keep_an_ambiguous_field_without_changing_other_actions(self):
        with tempfile.TemporaryDirectory(prefix="aci-policy-", dir=PROJECT / ".test-runs") as temp:
            root = Path(temp)
            sources = root / "sources"
            sources.mkdir()
            original = (PROJECT / "docs/sample-docs/ACI.txt").read_bytes()
            (sources / "ACI.txt").write_bytes(original)
            policies = ET.parse(PROJECT / "src/config/feed-policies.xml")
            feed = next(item for item in policies.getroot() if item.get("type") == "ACI")
            field = next(item for item in feed if item.get("name") == "/Text0152")
            field.attrib.clear()
            field.attrib.update(name="/Text0152", identification="None", action="Keep")
            policy_file = root / "policies.xml"
            policies.write(policy_file, encoding="utf-8", xml_declaration=True)
            docs = root / "docs"
            process = subprocess.run(["dotnet", str(DLL), "--docs-root", str(docs), "--process-folder", str(sources),
                                      "--policy-file", str(policy_file)], cwd=PROJECT, capture_output=True,
                                     text=True, encoding="utf-8", timeout=90)
            self.assertEqual(process.returncode, 0, process.stdout + process.stderr)
            report_file = next((runtime_root(docs) / "reports").glob("*.json"))
            report = json.loads(report_file.read_text())
            self.assertTrue(report["AllPublished"])
            output = Path(report["Items"][0]["OutputFile"]).read_bytes()
            self.assertEqual(verify_output(original, output, policy_file), {"Keep": 18, "Mask": 5})
            self.assertEqual((sources / "ACI.txt").read_bytes(), original)


if __name__ == "__main__":
    unittest.main()
