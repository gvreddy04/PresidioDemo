"""Embedded local Presidio. .NET supplies validated per-feed field policies.
Feed parsing and serialization stay in .NET. No remote processing or runtime model downloads.
"""
import importlib.util
from presidio_analyzer import AnalyzerEngine, RecognizerResult
from presidio_anonymizer import AnonymizerEngine
from presidio_anonymizer.entities import OperatorConfig

if importlib.util.find_spec("en_core_web_lg") is None:
    raise RuntimeError("Install the bundled en_core_web_lg model before starting the listener.")
analyzer = AnalyzerEngine()
anonymizer = AnonymizerEngine()


def protect_configured_field(value, field, identification, action, entity, replacement,
                             entities, threshold, mask_character, mask_characters, from_end):
    if not isinstance(value, str) or not isinstance(field, str):
        raise ValueError("Decoded text and field name are required")
    if action == "Keep" and identification == "None":
        return value
    if not value:
        return value
    if identification == "Known":
        # Known structured PII does not need NLP detection.
        results = [RecognizerResult(entity, 0, len(value), 1.0)]
    elif identification == "Analyze":
        results = analyzer.analyze(text=value, language="en", context=[field],
                                   entities=entities.split(","), score_threshold=float(threshold))
    else:
        raise ValueError("Unsupported identification mode")
    if action == "Replace":
        operator = OperatorConfig("replace", {"new_value": replacement})
    elif action == "Mask":
        operator = OperatorConfig("mask", {"masking_char": mask_character,
                                           "chars_to_mask": int(mask_characters),
                                           "from_end": from_end == "true"})
    elif action == "Redact":
        operator = OperatorConfig("redact")
    else:
        raise ValueError("Unsupported protection action")
    return anonymizer.anonymize(text=value, analyzer_results=results,
                               operators={"DEFAULT": operator}).text