"""Verify the before/after transaction log written for a published message, and the value-free detection log."""
import json
import re
import struct
import subprocess
import unittest

from pipeline_harness import PROJECT, run

SPIKE = PROJECT / "tests" / "DetectionSpike" / "bin" / "Debug" / "net10.0" / "DetectionSpike.dll"
DETECT = re.compile(r"^(\S+) [0-9a-f]{32} DETECT slot=(\d+) key=(\S+) len=(\d+) shape=(.*) format=(\S+) entity=(\S+) "
                    r"score=(\d\.\d\d) decision=(masked|replaced|kept|masked-inside|anchor|masked-by-default|missed-below-threshold)$")


def sample_values():
    """Every decoded value of the supplied samples: XDR counted strings and JSON scalars."""
    values = set()
    for path in sorted((PROJECT / "docs" / "sample-docs").rglob("*.txt")):
        rows = [line[2:].strip() for line in path.read_text(encoding="utf-8").splitlines() if line.startswith("X ")]
        payload = bytes.fromhex("".join(rows))
        if path.parent.name in ("ACI", "ETKT", "PNR"):
            offset = 0
            while offset + 4 <= len(payload):
                length = struct.unpack(">I", payload[offset:offset + 4])[0]
                end = offset + 4 + length
                if 0 < length <= len(payload) - offset - 4 and all(0x20 <= b <= 0x7E for b in payload[offset + 4:end]):
                    values.add(payload[offset + 4:end].decode("ascii"))
                    offset = end + (-length % 4)
                else:
                    offset += 4
        else:
            stack = [json.loads(payload.decode("utf-8", errors="replace"))]
            while stack:
                item = stack.pop()
                if isinstance(item, dict):
                    stack.extend(item.values())
                elif isinstance(item, list):
                    stack.extend(item)
                elif item is not None:
                    values.add(str(item))
    return values


def shape(value):
    """The detection-log shape of a value: A for a letter, 9 for a digit, other characters unchanged."""
    return re.sub(r"[0-9]", "9", re.sub(r"[A-Za-z]", "A", value))


def declared_keys():
    """JSON property names declared in the recognizer files; the log prints only these, as labels (WR-018)."""
    import xml.etree.ElementTree as ET
    folder = PROJECT / "src" / "PresidioDemo.Core" / "Configuration" / "recognizers"
    return {key.text.strip() for path in folder.glob("*.xml") for key in ET.parse(path).getroot().iter("key")}


def leaked_values(text):
    """Sample values of 4+ characters found in text, except values identical to their own shape (such as 9999).
    Declared property names after key= are labels from the recognizer files and are removed before the search."""
    declared = declared_keys()
    text = re.sub(r" key=(\S+)", lambda m: "" if m.group(1) in declared else m.group(0), text)
    return sorted(v for v in sample_values() if len(v.strip()) >= 4 and shape(v.strip()) != v.strip() and v.strip() in text)


def detection_log(state):
    run_ = subprocess.run(["dotnet", str(SPIKE), "detection-log", state], capture_output=True, text=True, timeout=900)
    if run_.returncode != 0:
        raise AssertionError("Build tests/DetectionSpike first: " + run_.stdout + run_.stderr)
    return run_.stdout


class DetectionLogTests(unittest.TestCase):
    """V-05: with the switch on, one value-free line per decoded string; with it off, none (WR-018)."""

    @classmethod
    def setUpClass(cls):
        cls.on = detection_log("on")
        cls.off = detection_log("off")

    def test_one_complete_line_per_decoded_string(self):
        expected = sum(int(m.group(1)) for m in re.finditer(r"^SAMPLE \S+ candidates=(\d+)", self.on, re.M))
        lines = [line for line in self.on.splitlines() if " DETECT " in line]
        self.assertGreater(expected, 0)
        self.assertEqual(len(lines), expected)
        for line in lines:
            self.assertRegex(line, DETECT)

    def test_no_sample_value_appears_in_the_log(self):
        # Only the feed name and transaction ID before DETECT are skipped; keys are logged only when declared (WR-018).
        lines = "\n".join(line.split(" DETECT ", 1)[1] for line in self.on.splitlines() if " DETECT " in line)
        leaked = leaked_values(lines)
        self.assertEqual(leaked, [], "values found in the detection log: " + str(len(leaked)))

    def test_switched_off_writes_no_detection_lines(self):
        self.assertNotIn(" DETECT ", self.off)
        self.assertIn("SAMPLE ", self.off)


class FeedLogTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.original = (PROJECT / "tests/unit-tests/fixtures/seats-mq-export.txt").read_bytes()
        cls.harness = run({"Seats/comparison.txt": cls.original}, expect=0, prefix="feed-logs-")
        cls.addClassCleanup(cls.harness.cleanup)

    def test_one_log_per_transaction_with_complete_before_and_after_sections(self):
        original = self.original
        item = self.harness.item("Seats", "comparison.txt")
        logs = list((self.harness.output_root / "logs").rglob("*"))
        self.assertEqual([p.name for p in logs if p.is_file()], ["comparison.txt.log.txt"])
        text = self.harness.log("Seats", "comparison.txt")
        self.assertIn("Transaction: " + item["MessageId"], text)
        self.assertEqual(text.count("BEFORE ANONYMIZATION"), 1)
        before, after = text.split("AFTER ANONYMIZATION")
        self.assertIn("BEFORE ANONYMIZATION", before)
        self.assertIn(original.decode("utf-8"), before)
        self.assertIn("Decoded MQ payload", before)
        self.assertIn("Decoded MQ payload", after)
        self.assertIn("Complete feed data (" + str(len(original)) + " bytes):", before)
        self.assertNotIn("Parsed fields", after)
        self.assertNotIn("Plain text:", after)
        self.assertLess(before.index("Complete feed data"), before.index("Decoded MQ payload"))
        self.assertLess(before.index("Decoded MQ payload"), before.index("Plain text:"))
        self.assertLess(before.index("Plain text:"), before.index("Parsed fields"))
        self.assertLess(after.index("Decoded MQ payload"), after.index("Complete feed data"))
        output = self.harness.published("Seats", "comparison.txt")
        self.assertIn("Complete feed data (" + str(len(output)) + " bytes):", after)
        self.assertIn(output.decode("utf-8"), after)


if __name__ == "__main__":
    unittest.main()
