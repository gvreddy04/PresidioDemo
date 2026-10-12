"""Verify the agreed exchange folders and immutable finalized sample bytes."""
import hashlib
import unittest
from test_feed_listener import PROJECT, FEEDS


class ProjectLayoutTests(unittest.TestCase):
    def test_exchange_and_processing_roots_have_exactly_the_five_route_folders(self):
        docs = PROJECT / "docs"
        self.assertEqual({p.name for p in docs.iterdir() if p.is_dir()},
                         {"sample-docs", "input-feeds", "output-feeds", "failed-feeds", "feed-logs", "feed-processing"})
        self.assertTrue((docs / "feed-logs" / ".gitkeep").is_file())
        for root in ["input-feeds", "output-feeds", "failed-feeds", "feed-processing"]:
            self.assertEqual({p.name for p in (docs / root).iterdir()}, set(FEEDS))
            for feed in FEEDS:
                self.assertTrue((docs / root / feed / ".gitkeep").is_file())
        for obsolete in ["input", "output", "solace-feeds", "batch-runs", ".processing", ".feed-listener.lock"]:
            self.assertFalse((docs / obsolete).exists(), obsolete)

    def test_samples_are_flat_finalized_txt_files_with_unchanged_bytes(self):
        expected = {
            "ACI.txt": "f038c05e590ade818748f6547c2a624d8d662a52c54d3596ad93649eafce022d",
            "ETKT.txt": "6a0f5f65a53ff39fee57fdf8dbb870a28dc4fe136296ba4c151c277e99b78cfa",
            "PNR.txt": "75cc60895384395f8df28eff4c4abf6a0c58f1dc7960c5eee41cb294def0e4de",
            "PNR_LINKING.txt": "2a8caf4538eee4a2232bdcc939adf427171d5ab35a6b380059b2e09eb5c03610",
            "SEATS.txt": "3f7fd3eb9d48c5b646c3ae73c7b982f419c2c86476370bf9c8ffbddc2a19fccf",
        }
        samples = PROJECT / "docs" / "sample-docs"
        self.assertEqual({p.name for p in samples.iterdir()}, set(expected))
        for name, checksum in expected.items():
            self.assertTrue((samples / name).is_file())
            self.assertEqual(hashlib.sha256((samples / name).read_bytes()).hexdigest(), checksum)


if __name__ == "__main__":
    unittest.main()
