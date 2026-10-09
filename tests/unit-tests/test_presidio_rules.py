"""Unit checks for local identification and anonymization rules."""
from pathlib import Path
import sys
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "src" / "python"))
import presidio_demo


def protect(value, identification="Known", action="Replace", entity="PERSON", replacement="[PERSON]",
            entities="", threshold="0.5", mask_character="*", mask_characters="0", from_end="true"):
    return presidio_demo.protect_configured_field(value, "sampleField", identification, action, entity,
        replacement, entities, threshold, mask_character, mask_characters, from_end)


class PresidioRuleTests(unittest.TestCase):
    def test_known_name_is_protected_without_nlp_guessing(self):
        self.assertEqual(protect("Emily Carter"), "[PERSON]")
        self.assertEqual(protect("Qzx"), "[PERSON]")

    def test_keep_and_empty_values(self):
        self.assertEqual(protect("K7QX2M", "None", "Keep"), "K7QX2M")
        self.assertEqual(protect(""), "")

    def test_mask_and_redact(self):
        self.assertEqual(protect("1234567890", action="Mask", entity="PHONE_NUMBER", mask_characters="6"), "1234******")
        self.assertEqual(protect("X1234567", action="Redact", entity="PASSPORT_NUMBER"), "")

    def test_selected_free_text_detection(self):
        self.assertEqual(protect("Contact emily.carter@example.com today", "Analyze", entity="", entities="EMAIL_ADDRESS"), "Contact [PERSON] today")

    def test_invalid_mode_and_input(self):
        with self.assertRaises(ValueError):
            protect("Emily", identification="Unknown")
        with self.assertRaises(ValueError):
            protect(123)


if __name__ == "__main__":
    unittest.main()
