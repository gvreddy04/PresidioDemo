"""Verify the agreed exchange folders and immutable finalized sample bytes."""
import hashlib
import unittest
from test_feed_listener import PROJECT, FEEDS


class ProjectLayoutTests(unittest.TestCase):
    def test_three_exchange_roots_have_exactly_the_five_route_folders(self):
        docs = PROJECT / "docs"
        self.assertEqual({p.name for p in docs.iterdir() if p.is_dir()},
                         {"sample-docs", "input-feeds", "output-feeds", "failed-feeds"})
        for root in ["input-feeds", "output-feeds", "failed-feeds"]:
            self.assertEqual({p.name for p in (docs / root).iterdir()}, set(FEEDS))
            for feed in FEEDS:
                self.assertTrue((docs / root / feed / ".gitkeep").is_file())
        for obsolete in ["input", "output", "solace-feeds", "batch-runs", ".processing", ".feed-listener.lock"]:
            self.assertFalse((docs / obsolete).exists(), obsolete)

    def test_samples_are_flat_finalized_txt_files_with_unchanged_bytes(self):
        expected = {
            "ACI.txt": "f038c05e590ade818748f6547c2a624d8d662a52c54d3596ad93649eafce022d",
            "ETKT.txt": "6a0f5f65a53ff39fee57fdf8dbb870a28dc4fe136296ba4c151c277e99b78cfa",
            "PNR.txt": "5c8fc06ba95fb17048fe0e003cce8ce3ca622b2abf55b49b0d537b54e20a62b6",
            "PNR_LINKING.txt": "efa2103ffe74b92474b610c00a46eba64e0643dcc459c502166413eff855acb0",
            "SEATS.txt": "3f7fd3eb9d48c5b646c3ae73c7b982f419c2c86476370bf9c8ffbddc2a19fccf",
        }
        samples = PROJECT / "docs" / "sample-docs"
        self.assertEqual({p.name for p in samples.iterdir()}, set(expected))
        for name, checksum in expected.items():
            self.assertTrue((samples / name).is_file())
            self.assertEqual(hashlib.sha256((samples / name).read_bytes()).hexdigest(), checksum)


if __name__ == "__main__":
    unittest.main()
