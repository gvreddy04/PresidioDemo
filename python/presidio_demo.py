"""Presidio demo functions called from .NET through Python.NET.
Inputs and outputs are plain strings (JSON) to keep the C# side simple."""

import json
import re

from presidio_analyzer import AnalyzerEngine, Pattern, PatternRecognizer
from presidio_anonymizer import AnonymizerEngine
from presidio_anonymizer.entities import OperatorConfig
from presidio_anonymizer.operators import Operator, OperatorType

# Engines are created once and reused (loading en_core_web_lg is slow).
analyzer = AnalyzerEngine()  # default NLP engine: spaCy en_core_web_lg
anonymizer = AnonymizerEngine()

# Custom airline recognizers, registered on a second analyzer so option 1 stays "built-in only".
custom_analyzer = AnalyzerEngine()
custom_analyzer.registry.add_recognizer(PatternRecognizer(
    supported_entity="PNR_LOCATOR",
    patterns=[Pattern("pnr", r"\b[A-Z][A-Z0-9]{5}\b", 0.4)],
    context=["pnr", "booking", "reservation", "locator"],
    global_regex_flags=re.MULTILINE,  # default includes IGNORECASE, which would match "Carter"
))
custom_analyzer.registry.add_recognizer(PatternRecognizer(
    supported_entity="PASSENGER_ID",
    patterns=[Pattern("pax_id", r"\bPAX-\d{4}\b", 0.9)],
))


def _entities(text, results):
    return json.dumps([
        {"entity_type": r.entity_type, "score": round(r.score, 2), "text": text[r.start:r.end]}
        for r in sorted(results, key=lambda r: r.start)
    ])


def analyze(text):
    return _entities(text, analyzer.analyze(text=text, language="en"))


def analyze_custom(text):
    return _entities(text, custom_analyzer.analyze(text=text, language="en"))


OPERATORS = {
    "replace": OperatorConfig("replace"),
    "redact": OperatorConfig("redact"),
    "mask": OperatorConfig("mask", {"masking_char": "*", "chars_to_mask": 100, "from_end": False}),
    "hash": OperatorConfig("hash", {"hash_type": "sha256"}),
}


def anonymize(text, mode):
    results = analyzer.analyze(text=text, language="en")
    result = anonymizer.anonymize(text=text, analyzer_results=results,
                                  operators={"DEFAULT": OPERATORS[mode]})
    return result.text


class TokenOperator(Operator):
    """Custom operator: replaces each distinct value with <ENTITY_TYPE_n> and records it
    in a mapping (adapted from the Presidio 'pseudonymization' sample)."""

    def operate(self, text, params=None):
        per_type = params["mapping"].setdefault(params["entity_type"], {})
        if text not in per_type:
            per_type[text] = f"<{params['entity_type']}_{len(per_type)}>"
        return per_type[text]

    def validate(self, params=None):
        pass

    def operator_name(self):
        return "token"

    def operator_type(self):
        return OperatorType.Anonymize


anonymizer.add_anonymizer(TokenOperator)


def tokenize(text):
    """Returns {"text": tokenized text, "mapping": {entity: {original: token}}}.
    The PNR is detected but kept visible and unchanged."""
    mapping = {}
    results = custom_analyzer.analyze(text=text, language="en")
    result = anonymizer.anonymize(text=text, analyzer_results=results, operators={
        "DEFAULT": OperatorConfig("token", {"mapping": mapping}),
        "PNR_LOCATOR": OperatorConfig("keep"),
    })
    return json.dumps({"text": result.text, "mapping": mapping})


def detokenize(tokenized_json):
    """Restores originals from the mapping. Demo only; a real system uses a token vault."""
    data = json.loads(tokenized_json)
    text = data["text"]
    for per_type in data["mapping"].values():
        for original, token in per_type.items():
            text = text.replace(token, original)
    return text
