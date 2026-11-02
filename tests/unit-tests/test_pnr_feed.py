"""PNR protection of selected fields within the reviewed sequential PNR schema."""
import unittest

from pipeline_harness import run
from test_seats_feed import payload
from reference_feed_support import replace_fields, verify_output
from configuration_support import sample

PNR_ACTIONS = {"Keep": 74, "Mask": 80}
PNR_02_ACTIONS = {"Keep": 33, "Mask": 24}


def source():
    return payload(sample("PNR.txt").read_bytes())


def changed_pnr():
    return replace_fields("PNR", source(), {"/pnr": "K7QX2M", "/surname": "SMITH", "/givenName": "EMMA"})


def flipped(position):
    raw = bytearray(source())
    raw[position] ^= 1
    return bytes(raw)


def unmodelled_list():
    raw = bytearray(source())
    # Offset 468 is a list that is empty in every sample; its element type is not modelled.
    raw[471] = 1
    return bytes(raw)


# Rejected raw inputs and the reason text each must report.
REJECTED = {
    "changed-version.bin": (lambda: flipped(3), "expected constant"),
    "unmodelled-list.bin": (unmodelled_list, "XDR"),
    "changed-count.bin": (lambda: flipped(403), "XDR"),
    "changed-padding.bin": (lambda: flipped(409), "XDR padding must contain zero bytes"),
    "truncated-pnr.bin": (lambda: source()[:-4], "Incomplete XDR field"),
    "extended-pnr.bin": (lambda: source() + bytes(4), "XDR schema did not consume the entire payload"),
}


class PnrFeedTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        inputs = {"PNR/original-pnr.txt": sample("PNR.txt").read_bytes(),
                  "PNR/second-pnr.txt": sample("PNR_02.txt").read_bytes(),
                  "PNR/changed-pnr.bin": changed_pnr()}
        inputs.update({"PNR/" + name: build() for name, (build, _) in REJECTED.items()})
        cls.harness = run(inputs, expect=2, prefix="pnr-reference-")
        cls.addClassCleanup(cls.harness.cleanup)

    def rejected(self, name):
        build, reason = REJECTED[name]
        self.harness.assert_rejected("PNR", name, build(), reason)

    def test_actual_reference_protects_every_selected_occurrence_and_preserves_other_bytes(self):
        original = sample("PNR.txt").read_bytes()
        output = self.harness.published("PNR", "original-pnr.txt")
        self.assertEqual(verify_output("PNR", original, output), PNR_ACTIONS)
        before, after = payload(original), payload(output)
        self.assertEqual(before[84:90], after[84:90])
        for value in (b"JESTE", b"MOHAN.CHALLA", b"SX397927", b"8728251761", b"21434079510", b"31AUG88", b"1600 SMITH ST", b"127.0.0.1"):
            self.assertNotIn(value, after)
        # SSR-code variants mask the values and keep the record syntax around them.
        for value in (b"IAH", b"IAD", b"DUB", b"FQTR", b"1373X12OCT", b"0228X13OCT", b"TOTAL FARE - USD11.20",
                      b"TOTAL MILEAGE DEDUCTION - 55000 MILES", b"DOCSUAHK1", b"CTCEYYHK1", b"CTCMYYHK1", b"FWGV4L"):
            self.assertIn(value, after)
        self.assertEqual((self.harness.input_root / "PNR/original-pnr.txt").read_bytes(), original)

    def test_second_sample_with_different_list_lengths_is_protected(self):
        original = sample("PNR_02.txt").read_bytes()
        output = self.harness.published("PNR", "second-pnr.txt")
        self.assertEqual(verify_output("PNR", original, output), PNR_02_ACTIONS)
        after = payload(output)
        for value in (b"ADMIN", b"DEUSERDYRDAHFMW", b"01JAN90", b"890989000", b"45526666"):
            self.assertNotIn(value, after)
        for value in (b"FWGWVG", b"SFO", b"EWR", b"03DEC26", b"DOCSUAHK1/////"):
            self.assertIn(value, after)

    def test_raw_reference_can_change_selected_same_width_values_and_keep_pnr(self):
        raw = changed_pnr()
        output = self.harness.published("PNR", "changed-pnr.bin")
        self.assertEqual(verify_output("PNR", raw, output, raw=True), PNR_ACTIONS)
        self.assertEqual(output[84:90], b"K7QX2M")

    def test_unclassified_binary_changes_are_retained_without_output(self):
        self.rejected("changed-version.bin")

    def test_unmodelled_list_content_is_retained_without_output(self):
        self.rejected("unmodelled-list.bin")

    def test_changed_counts_padding_and_frame_lengths_are_rejected(self):
        for name in ("changed-count.bin", "changed-padding.bin", "truncated-pnr.bin", "extended-pnr.bin"):
            with self.subTest(name):
                self.rejected(name)


if __name__ == "__main__":
    unittest.main()
