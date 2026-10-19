"""PNR protection of selected fields within the reviewed sequential PNR schema."""
from pathlib import Path
import tempfile
import unittest

from test_feed_listener import PROJECT, Listener, wait_for, rejected, processing_files
from test_seats_feed import payload
from reference_feed_support import field_spans, verify_output


class PnrFeedTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        (PROJECT / ".test-runs").mkdir(exist_ok=True)
        cls.sandbox = tempfile.TemporaryDirectory(prefix="pnr-reference-", dir=PROJECT / ".test-runs")
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

    def deliver(self, name, data):
        inbox = self.docs / "input-feeds/PNR" / name
        inbox.write_bytes(data)
        directory = self.docs / "output-feeds/PNR"
        pattern = Path(name).stem + ".*" + Path(name).suffix
        wait_for(lambda: len(list(directory.glob(pattern))) == 1 and not inbox.exists()
                 and not processing_files(self.docs, "PNR", name), "PNR publication and acknowledgment")
        return next(directory.glob(pattern)).read_bytes()

    def test_actual_reference_protects_every_selected_occurrence_and_preserves_other_bytes(self):
        source = (PROJECT / "docs/sample-docs/PNR.txt").read_bytes()
        output = self.deliver("original-pnr.txt", source)
        self.assertEqual(verify_output("PNR", source, output), {"Keep": 30, "Mask": 76})
        before, after = payload(source), payload(output)
        self.assertEqual(before[84:90], after[84:90])
        for value in (b"JESTE", b"MOHAN.CHALLA", b"SX397927", b"8728251761", b"21434079510", b"31AUG88", b"1600 SMITH ST", b"127.0.0.1"):
            self.assertNotIn(value, after)
        for value in (b"IAH", b"IAD", b"DUB", b"1373X12OCT", b"0228X13OCT", b"TOTAL FARE - USD11.20", b"TOTAL MILEAGE DEDUCTION - 55000 MILES", b"DOCSUAHK1", b"CTCEYYHK1", b"CTCMYYHK1"):
            self.assertIn(value, after)
        self.assertEqual((PROJECT / "docs/sample-docs/PNR.txt").read_bytes(), source)

    def test_raw_reference_can_change_selected_same_width_values_and_keep_pnr(self):
        raw = bytearray(payload((PROJECT / "docs/sample-docs/PNR.txt").read_bytes()))
        replacements = {"/pnr": "K7QX2M", "/passengerSurname": "SMITH", "/passengerGivenName": "EMMA"}
        for name, start, length in field_spans("PNR"):
            if name in replacements:
                value = replacements[name].encode("ascii")
                self.assertEqual(len(value), length)
                raw[start:start + length] = value
        output = self.deliver("changed-pnr.bin", raw)
        self.assertEqual(verify_output("PNR", bytes(raw), output, raw=True), {"Keep": 30, "Mask": 76})
        self.assertEqual(output[84:90], b"K7QX2M")

    def test_unclassified_binary_changes_are_retained_without_output(self):
        raw = bytearray(payload((PROJECT / "docs/sample-docs/PNR.txt").read_bytes()))
        raw[3] ^= 1
        (self.docs / "input-feeds/PNR/changed-version.bin").write_bytes(raw)
        rejected(self.docs, "PNR", "changed-version.bin", bytes(raw), "error:")

    def test_fixed_record_prefix_changes_are_retained_without_output(self):
        raw = bytearray(payload((PROJECT / "docs/sample-docs/PNR.txt").read_bytes()))
        raw[500] = ord("Z")
        (self.docs / "input-feeds/PNR/changed-docs-prefix.bin").write_bytes(raw)
        rejected(self.docs, "PNR", "changed-docs-prefix.bin", bytes(raw), "error:")

    def test_changed_counts_padding_and_frame_lengths_are_rejected(self):
        source = payload((PROJECT / "docs/sample-docs/PNR.txt").read_bytes())
        for name, position in (("count", 403), ("padding", 409)):
            raw = bytearray(source)
            raw[position] ^= 1
            filename = "changed-" + name + ".bin"
            (self.docs / "input-feeds/PNR" / filename).write_bytes(raw)
            rejected(self.docs, "PNR", filename, bytes(raw), "error:")
        for name, raw in (("truncated", source[:-4]), ("extended", source + bytes(4))):
            filename = name + "-pnr.bin"
            (self.docs / "input-feeds/PNR" / filename).write_bytes(raw)
            rejected(self.docs, "PNR", filename, raw, "XDR")


if __name__ == "__main__":
    unittest.main()
