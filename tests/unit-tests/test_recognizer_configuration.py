"""Checks for the per-feed recognizer XML files (V-04): loader validation and pattern safety."""
from pathlib import Path
import subprocess
import time
import unittest
import xml.etree.ElementTree as ET

import regex

PROJECT = Path(__file__).resolve().parents[2]
RECOGNIZERS = PROJECT / "src" / "PresidioDemo.Core" / "Configuration" / "recognizers"
SPIKE = PROJECT / "tests" / "DetectionSpike" / "bin" / "Debug" / "net10.0" / "DetectionSpike.dll"
FILES = ["aci.xml", "etkt.xml", "pnr.xml", "pnr-linking.xml", "seats.xml"]
# Presidio's PatternRecognizer compiles with these flags.
FLAGS = regex.DOTALL | regex.MULTILINE | regex.IGNORECASE
# Long inputs that make badly nested quantifiers backtrack.
ADVERSARIAL = [
    "A" * 10_000,
    "9" * 10_000,
    ("AB/" * 3_400)[:10_000],
    ("A " * 5_000),
    ("DOCSXXHK1/" + "/" * 9_990),
    ("X" * 9_996) + "!!!!",
    ("A-" * 5_000),
]


def patterns():
    """Yield (file, name, pattern) for every pattern, as it runs: formats are searched by Presidio, keeps are only ever
    matched against the whole value (anchored), as the .NET loader compiles them."""
    for name in FILES:
        root = ET.parse(RECOGNIZERS / name).getroot()
        for element in root.iter("format"):
            for pattern in element.iter("pattern"):
                yield name, element.get("name"), pattern.text
        for element in root.iter("keep"):
            for pattern in element.iter("pattern"):
                yield name, element.get("name"), r"\A(?:" + pattern.text + r")\Z"


class RecognizerConfigurationTests(unittest.TestCase):
    def test_loader_accepts_the_files_and_rejects_invalid_ones(self):
        self.assertTrue(SPIKE.is_file(), "Build tests/DetectionSpike first")
        run = subprocess.run(["dotnet", str(SPIKE), "config-checks"], capture_output=True, text=True, timeout=300)
        self.assertEqual(run.returncode, 0, run.stdout + run.stderr)
        self.assertIn("recognizer configuration checks", run.stdout)

    def test_every_pattern_compiles_in_the_presidio_regex_engine(self):
        for name, label, pattern in patterns():
            with self.subTest(file=name, format=label):
                regex.compile(pattern, FLAGS)

    def test_no_pattern_backtracks_on_long_adversarial_input(self):
        for name, label, pattern in patterns():
            compiled = regex.compile(pattern, FLAGS)
            for text in ADVERSARIAL:
                with self.subTest(file=name, format=label, size=len(text)):
                    start = time.perf_counter()
                    list(compiled.finditer(text, timeout=1.0))
                    self.assertLess(time.perf_counter() - start, 0.1, "pattern is too slow on a 10 KB input")


if __name__ == "__main__":
    unittest.main()
