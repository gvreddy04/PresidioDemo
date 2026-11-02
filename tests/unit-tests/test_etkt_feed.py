"""ETKT end-to-end checks for the supplied 672-byte reference frame."""
import struct
import unittest

from pipeline_harness import run
from test_seats_feed import payload
from reference_feed_support import field_spans, replace_fields, verify_output
from configuration_support import sample


def changed_etkt():
    return replace_fields("ETKT", payload(sample("ETKT.txt").read_bytes()), {
        "/agentTerminalAddress": "K7QX2M", "/agentCityCode": "USR", "/agentSignInNumber": "1234",
        "/ticketNumber": "000000000000123", "/historySequenceNumber": "0161234567890",
        "/ticketStatusRecord/Prefix": "42B00012"})


def named_etkt():
    """Populate h_pax_name, empty in every sample; its four-byte count sits at payload offset 244."""
    raw = payload(sample("ETKT.txt").read_bytes())
    assert raw[244:248] == bytes(4)
    return raw[:244] + struct.pack(">I", 12) + b"EMILY CARTER" + raw[248:]


def changed_version():
    raw = bytearray(payload(sample("ETKT.txt").read_bytes()))
    raw[3] ^= 1
    return bytes(raw)


def truncated():
    return payload(sample("ETKT.txt").read_bytes())[:-4]


class EtktFeedTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.harness = run({"ETKT/original-etkt.txt": sample("ETKT.txt").read_bytes(),
                           "ETKT/changed-etkt.bin": changed_etkt(),
                           "ETKT/changed-version.bin": changed_version(),
                           "ETKT/divide-etkt.txt": sample("ETKT_05.txt").read_bytes(),
                           "ETKT/named-etkt.bin": named_etkt(),
                           "ETKT/truncated-etkt.bin": truncated()}, expect=2, prefix="etkt-reference-")
        cls.addClassCleanup(cls.harness.cleanup)

    def test_actual_etkt_export_preserves_every_byte_except_configured_masks(self):
        original = sample("ETKT.txt").read_bytes()
        output = self.harness.published("ETKT", "original-etkt.txt")
        self.assertEqual(verify_output("ETKT", original, output), {"Keep": 4, "Mask": 5})
        self.assertEqual((self.harness.input_root / "ETKT/original-etkt.txt").read_bytes(), original)
        self.assertIn(b"CHECKED-IN", payload(output))

    def test_raw_etkt_with_different_identifier_values_and_pnr(self):
        raw = changed_etkt()
        self.assertEqual(verify_output("ETKT", raw, self.harness.published("ETKT", "changed-etkt.bin"), raw=True), {"Keep": 4, "Mask": 5})

    def test_etkt_rejects_changed_unclassified_binary_bytes(self):
        self.harness.assert_rejected("ETKT", "changed-version.bin", changed_version(), "expected constant")

    def test_divide_event_preserves_new_and_old_pnrs(self):
        original = sample("ETKT_05.txt").read_bytes()
        output = self.harness.published("ETKT", "divide-etkt.txt")
        self.assertEqual(verify_output("ETKT", original, output), {"Keep": 5, "Mask": 5})
        fields = {name: payload(output)[start:start + length]
                  for name, start, length in field_spans("ETKT", original)}
        self.assertEqual(fields["/newPnr"], b"FWGXKN")
        self.assertEqual(fields["/oldPnr"], b"FWDZRF")

    def test_populated_passenger_name_is_published_masked(self):
        raw = named_etkt()
        output = self.harness.published("ETKT", "named-etkt.bin")
        self.assertEqual(verify_output("ETKT", raw, output, raw=True), {"Keep": 4, "Mask": 6})
        self.assertNotIn(b"EMILY", output)
        self.assertIn(b"************", output)

    def test_etkt_rejects_truncated_reference(self):
        self.harness.assert_rejected("ETKT", "truncated-etkt.bin", truncated(), "Incomplete XDR field")


if __name__ == "__main__":
    unittest.main()
