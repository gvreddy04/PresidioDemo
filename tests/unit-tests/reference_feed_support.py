"""Shared exact-byte checks for explicitly configured binary reference layouts."""
from collections import Counter
import xml.etree.ElementTree as ET

from test_feed_listener import PROJECT
from test_seats_feed import payload, header


def reference_entry(feed):
    return next(item for item in ET.parse(PROJECT / "src/config/reference-layouts.xml").getroot()
                if item.get("type") == feed)


def field_spans(feed):
    spans = []
    for text in reference_entry(feed):
        start = int(text.get("offset")) + 4
        if len(text):
            for part in text:
                spans.append(("/" + text.get("name") + "/" + part.get("name"),
                              start + int(part.get("start")), int(part.get("length"))))
        else:
            spans.append(("/" + text.get("name"), start, int(text.get("length"))))
    return spans


def verify_output(feed, original, protected, policy_file=None, raw=False):
    rules = {field.get("name"): field.attrib
             for entry in ET.parse(policy_file or PROJECT / "src/config/feed-policies.xml").getroot()
             if entry.get("type") == feed for field in entry}
    before = original if raw else payload(original)
    after = protected if raw else payload(protected)
    if not raw:
        assert header(original) == header(protected), "MQ metadata changed"
        assert original.endswith(b"\n") == protected.endswith(b"\n"), "Final newline changed"
    reference_file = (PROJECT / "src/config" / reference_entry(feed).get("referenceFile")).resolve()
    assert len(before) == len(after) == len(payload(reference_file.read_bytes()))
    expected = bytearray(before)
    actions = Counter()
    for name, start, length in field_spans(feed):
        rule = rules[name]
        if rule["action"] == "Mask":
            assert rule["identification"] == "Known" and int(rule["maskCharacters"]) == length
            expected[start:start + length] = rule.get("maskCharacter", "*").encode("ascii") * length
        else:
            assert rule["action"] == "Keep" and rule["identification"] == "None"
        actions[rule["action"]] += 1
        if name.split("/")[-1] == "pnr":
            assert before[start:start + length] == after[start:start + length], "PNR changed"
    assert bytes(expected) == after, "Configured protection or preserved binary bytes differ"
    return dict(actions)
