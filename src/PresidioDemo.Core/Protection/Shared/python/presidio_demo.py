"""Embedded local Presidio. .NET supplies one validated recognizer set per feed.
Decoding, serialization and every protection decision stay in .NET; detection returns spans only.
No remote processing or runtime model downloads.
"""
import importlib.util
import json
import logging
import os

# Presidio reads its regex time limit (whole seconds) once, at import. A pattern that times out is skipped with a warning,
# which would publish what it should have found; the handler below turns that warning into a detection failure so the
# message is dead-lettered instead (WR-016). One second keeps a runaway pattern from holding the engine for long.
os.environ.setdefault("REGEX_TIMEOUT_SECONDS", "1")

from presidio_analyzer import BatchAnalyzerEngine, AnalyzerEngine, Pattern, PatternRecognizer, RecognizerRegistry, RecognizerResult
from presidio_analyzer.nlp_engine import NlpEngineProvider

if importlib.util.find_spec("en_core_web_lg") is None:
    raise RuntimeError("Install the bundled en_core_web_lg model before starting the listener.")
# One spaCy engine (en_core_web_lg, Presidio's default configuration) shared by every feed's registry.
nlp_engine = NlpEngineProvider().create_engine()


class _TimeoutWatch(logging.Handler):
    """Records Presidio's 'timed out ... skipping' warnings during one analysis call."""

    def __init__(self):
        super().__init__(level=logging.WARNING)
        self.timed_out = False

    def emit(self, record):
        if "timed out" in record.getMessage():
            self.timed_out = True


_timeouts = _TimeoutWatch()
logging.getLogger("presidio-analyzer").addHandler(_timeouts)

# Per-feed detection engines built by configure_feed: feed name -> (batch engine or None, entities).
_feeds = {}


def configure_feed(config_json):
    """Build one feed's Presidio registry from the .NET recognizer set (WR-014).

    config_json holds feed, formats (name, entity, score, patterns, context) and builtIns (entity names). Built-in
    recognizers are limited to the listed entities; spaCy NER provides PERSON. The score threshold is applied in .NET.
    Returns the feed name.
    """
    config = json.loads(config_json)
    registry = RecognizerRegistry(supported_languages=["en"])
    builtins = set(config["builtIns"])
    if builtins:
        registry.load_predefined_recognizers(languages=["en"], nlp_engine=nlp_engine)
        registry.recognizers = [r for r in registry.recognizers if builtins & set(r.supported_entities)]
    for item in config["formats"]:
        patterns = [Pattern(item["name"], pattern, float(item["score"])) for pattern in item["patterns"]]
        registry.add_recognizer(PatternRecognizer(supported_entity=item["entity"], name=item["name"],
                                                  patterns=patterns, context=item["context"] or None))
    entities = sorted(builtins | {item["entity"] for item in config["formats"]})
    # An empty registry would make AnalyzerEngine load every default recognizer, so a feed with nothing to detect
    # gets no engine at all.
    engine = AnalyzerEngine(registry=registry, nlp_engine=nlp_engine, supported_languages=["en"]) if entities else None
    _feeds[config["feed"]] = (BatchAnalyzerEngine(engine) if engine else None, entities)
    return config["feed"]


def analyze_strings(feed, texts_json):
    """Detect personal-data spans in each decoded string of one message, in one batch.

    texts_json is a JSON list of strings. Returns a JSON list with one list per string of
    [start, end, entity, score, recognizer name], with UTF-16 offsets. The recognizer name is the XML format name or
    the built-in's name.
    Raises when a pattern timed out, so the caller dead-letters the message rather than publish unchecked text.
    """
    if feed not in _feeds:
        raise ValueError("Feed recognizers are not configured")
    texts = json.loads(texts_json)
    if not isinstance(texts, list) or not all(isinstance(text, str) for text in texts):
        raise ValueError("A JSON list of strings is required")
    engine, entities = _feeds[feed]
    if engine is None or not texts:
        return json.dumps([[] for _ in texts])
    _timeouts.timed_out = False
    # Every span is returned, including those below the feed threshold: .NET applies the threshold and logs the rest
    # as possible misses inside kept values (WR-018).
    results = list(engine.analyze_iterator(texts, language="en", entities=entities, score_threshold=0.0,
                                           batch_size=max(1, min(len(texts), 64))))
    if _timeouts.timed_out:
        raise TimeoutError("A recognizer pattern timed out")
    return json.dumps([[[_utf16(text, r.start), _utf16(text, r.end), r.entity_type, round(float(r.score), 4),
                         (r.recognition_metadata or {}).get(RecognizerResult.RECOGNIZER_NAME_KEY, "")]
                        for r in spans if r.end > r.start] for text, spans in zip(texts, results)])


def _utf16(text, index):
    """Convert a code-point offset to the UTF-16 offset .NET strings use (they differ after characters such as emoji)."""
    return len(text[:index].encode("utf-16-le")) // 2
