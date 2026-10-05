"""Presidio demo functions called from .NET through Python.NET.
Inputs and outputs are plain strings (JSON) to keep the C# side simple.

Every function takes `fmt`: "text", "json" or "xml". For JSON and XML, Presidio runs on each
value separately (JSON string values, XML element text and attributes), so keys, tags and
nesting are never changed and the output stays valid JSON/XML."""

import json
import re
import xml.etree.ElementTree as ET

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


def _map_values(content, fmt, fn):
    """Calls fn(value, field) for every text value and returns the rebuilt document.
    `field` is the JSON key, XML tag or XML attribute name (None for plain text)."""
    if fmt == "text":
        return fn(content, None)

    if fmt == "json":
        def walk(node, key):
            if isinstance(node, dict):
                return {k: walk(v, k) for k, v in node.items()}
            if isinstance(node, list):
                return [walk(v, key) for v in node]
            if isinstance(node, str):
                return fn(node, key)
            return node  # numbers, booleans and null are left as they are
        return json.dumps(walk(json.loads(content), None), indent=2, ensure_ascii=False)

    if fmt == "xml":
        root = ET.fromstring(content)
        for element in root.iter():
            if element.text and element.text.strip():
                element.text = fn(element.text, element.tag)
            for name, value in element.attrib.items():
                element.attrib[name] = fn(value, name)
        return ET.tostring(root, encoding="unicode")

    raise ValueError(f"Unknown format: {fmt}")


def _analyze_value(engine, value, field):
    # The field name (for example "email" or "pnr") is passed as context to help detection.
    return engine.analyze(text=value, language="en", context=[field] if field else None)


def _find(engine, content, fmt):
    found = []

    def collect(value, field):
        for r in sorted(_analyze_value(engine, value, field), key=lambda r: r.start):
            found.append({"field": field or "", "entity_type": r.entity_type,
                          "score": round(r.score, 2), "text": value[r.start:r.end]})
        return value

    _map_values(content, fmt, collect)
    return json.dumps(found, indent=2)


def analyze(content, fmt):
    return _find(analyzer, content, fmt)


def analyze_custom(content, fmt):
    return _find(custom_analyzer, content, fmt)


OPERATORS = {
    "replace": OperatorConfig("replace"),
    "redact": OperatorConfig("redact"),
    "mask": OperatorConfig("mask", {"masking_char": "*", "chars_to_mask": 100, "from_end": False}),
    "hash": OperatorConfig("hash", {"hash_type": "sha256"}),
}


def anonymize(content, fmt, mode):
    def protect(value, field):
        results = _analyze_value(analyzer, value, field)
        return anonymizer.anonymize(text=value, analyzer_results=results,
                                    operators={"DEFAULT": OPERATORS[mode]}).text

    return _map_values(content, fmt, protect)


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


def tokenize(content, fmt):
    """Returns {"text": tokenized document, "mapping": {entity: {original: token}}}.
    One mapping is shared by the whole document, so a repeated value gets the same token.
    The PNR is detected but kept visible and unchanged."""
    mapping = {}
    operators = {
        "DEFAULT": OperatorConfig("token", {"mapping": mapping}),
        "PNR_LOCATOR": OperatorConfig("keep"),
    }

    def protect(value, field):
        results = _analyze_value(custom_analyzer, value, field)
        # spaCy sometimes also labels a PNR as PERSON with a higher score; the PNR must win.
        pnrs = [r for r in results if r.entity_type == "PNR_LOCATOR"]
        results = [r for r in results if r.entity_type == "PNR_LOCATOR"
                   or not any(r.start < p.end and p.start < r.end for p in pnrs)]
        return anonymizer.anonymize(text=value, analyzer_results=results, operators=operators).text

    document = _map_values(content, fmt, protect)
    return json.dumps({"text": document, "mapping": mapping})


def detokenize(tokenized_json, fmt):
    """Restores originals from the mapping. Demo only; a real system uses a token vault."""
    data = json.loads(tokenized_json)

    def restore(value, field):
        for per_type in data["mapping"].values():
            for original, token in per_type.items():
                value = value.replace(token, original)
        return value

    return _map_values(data["text"], fmt, restore)
