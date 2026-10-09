"""ETKT end-to-end checks for the supplied 672-byte reference frame."""
from pathlib import Path
import tempfile
import unittest

from test_feed_listener import PROJECT, Listener, wait_for, rejected, runtime_root
from test_seats_feed import payload
from reference_feed_support import field_spans, verify_output


class EtktFeedTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        (PROJECT / ".test-runs").mkdir(exist_ok=True)
        cls.sandbox = tempfile.TemporaryDirectory(prefix="etkt-reference-", dir=PROJECT / ".test-runs")
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
        inbox = self.docs / "input-feeds/ETKT" / name
        inbox.write_bytes(content)
        directory = self.docs / "output-feeds/ETKT"
        pattern = Path(name).stem + ".*" + Path(name).suffix
        wait_for(lambda: len(list(directory.glob(pattern))) == 1 and not inbox.exists()
                 and not list((runtime_root(self.docs) / "processing/ETKT").rglob(name)), "ETKT publication and acknowledgment")
        return next(directory.glob(pattern)).read_bytes()

    def test_actual_etkt_export_preserves_every_byte_except_configured_masks(self):
        original = (PROJECT / "docs/sample-docs/ETKT.txt").read_bytes()
        output = self.deliver("original-etkt.txt", original)
        self.assertEqual(verify_output("ETKT", original, output), {"Keep": 4, "Mask": 5})
        self.assertEqual((PROJECT / "docs/sample-docs/ETKT.txt").read_bytes(), original)
        self.assertIn(b"CHECKED-IN", payload(output))

    def test_raw_etkt_with_different_identifier_values_and_pnr(self):
        raw = bytearray(payload((PROJECT / "docs/sample-docs/ETKT.txt").read_bytes()))
        changed = {"/pnr": "K7QX2M", "/Text0044": "USR", "/Text0072": "1234", "/Text0088": "000000000000123",
                   "/Text0152": "0161234567890", "/Text0172/Prefix": "42B00012"}
        for name, start, length in field_spans("ETKT"):
            if name in changed:
                encoded = changed[name].encode("ascii")
                self.assertEqual(len(encoded), length)
                raw[start:start + length] = encoded
        self.assertEqual(verify_output("ETKT", bytes(raw), self.deliver("changed-etkt.bin", raw), raw=True), {"Keep": 4, "Mask": 5})

    def test_etkt_rejects_changed_unclassified_binary_bytes(self):
        raw = bytearray(payload((PROJECT / "docs/sample-docs/ETKT.txt").read_bytes()))
        raw[3] ^= 1
        (self.docs / "input-feeds/ETKT/changed-version.bin").write_bytes(raw)
        rejected(self.docs, "ETKT", "changed-version.bin", bytes(raw), "unclassified binary region")

    def test_etkt_rejects_truncated_reference(self):
        raw = payload((PROJECT / "docs/sample-docs/ETKT.txt").read_bytes())[:-4]
        (self.docs / "input-feeds/ETKT/truncated-etkt.bin").write_bytes(raw)
        rejected(self.docs, "ETKT", "truncated-etkt.bin", raw, "requires 672 payload bytes")


if __name__ == "__main__":
    unittest.main()
