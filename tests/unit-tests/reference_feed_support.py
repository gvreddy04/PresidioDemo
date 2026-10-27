"""Independent sequential XML reader and byte-exact binary masking oracle."""
from collections import Counter
import re
import struct
import xml.etree.ElementTree as ET

from configuration_support import SCHEMA, REFERENCE, catalog, json_rules
from test_seats_feed import payload, header


def inspect_fields(feed, original, raw=False, schema_file=None, reference_file=None, policy_file=None):
    """Return scalar paths, value spans and rule IDs without consulting sample filenames."""
    data = original if raw else payload(original)
    entry = ET.parse(schema_file or SCHEMA).find(f"feed[@type='{feed}']")
    reference = ET.parse(reference_file or REFERENCE).find(f"feed[@type='{feed}']")
    texts = {text.get("path", "/" + text.get("name")): text for text in reference}
    bindings = json_rules(feed, policy_file=policy_file)
    shared = catalog(policy_file)
    inline = {path: rule for path, rule in bindings.items() if "id" not in rule}
    cursor = 0
    fields = []
    framing = []

    def word():
        nonlocal cursor
        value = struct.unpack_from(">I", data, cursor)[0]
        cursor += 4
        return value

    def walk(node, path):
        nonlocal cursor
        kind = node.tag
        if kind == "struct":
            for child in node:
                walk(child, path + "/" + child.get("name"))
        elif kind == "words":
            # Independently consume N raw words; the shorthand adds no count word.
            for _ in range(int(node.get("count"))):
                start = cursor
                word()
                fields.append((path + "/*", start, 4, "KeepStructural", "word"))
        elif kind in {"array", "fixedArray"}:
            count = word() if kind == "array" else int(node.get("count"))
            assert count <= int(node.get("max", node.get("count", "0")))
            for _ in range(count):
                walk(node[0], path + "/*")
        elif kind == "optional":
            present = word()
            assert present in (0, 1)
            if present:
                walk(node[0], path + "/value")
        elif kind == "string":
            start = cursor
            length = word()
            assert int(node.get("min", "0")) <= length <= int(node.get("max"))
            value_start = cursor
            value = data[cursor:cursor + length].decode("ascii")
            assert len(value) == length
            cursor += length
            padding = (-length) % 4
            assert data[cursor:cursor + padding] == bytes(padding)
            cursor += padding
            framing.extend([(start, 4), (value_start + length, padding)])
            mapping = texts[path]
            alias = "/" + mapping.get("name")
            pattern = mapping.get("pattern")
            if length == 0 and mapping.get("allowEmpty") == "true":
                return
            match = re.fullmatch(re.sub(r"\(\?<([A-Za-z0-9_]+)>", r"(?P<\1>", pattern), value) if pattern else None
            assert not pattern or match, "Text pattern mismatch: " + path
            if len(mapping):
                for part in mapping:
                    if part.get("fixed") == "true":
                        continue
                    first, last = match.span(part.get("name"))
                    policy_path = alias + "/" + part.get("name")
                    rule = bindings[policy_path]
                    fields.append((policy_path, value_start + first, last - first, rule.get("id", policy_path), "text"))
            else:
                rule = bindings[alias]
                fields.append((alias, value_start, length, rule.get("id", alias), "text"))
        elif kind in {"uint32", "int32"}:
            start = cursor
            value = word()
            assert node.get("expected") is None or value == int(node.get("expected"))
            fields.append((path, start, 4, "KeepStructural", "word"))
        else:
            raise AssertionError("Extend independent oracle for schema type " + kind)

    for node in entry:
        walk(node, "/" + node.get("name"))
    assert cursor == len(data), "Schema must consume the complete binary payload"
    return fields, framing


def field_spans(feed, original=None, raw=False, schema_file=None):
    if original is None:
        from configuration_support import PROJECT
        original = (PROJECT / "docs/sample-docs" / (feed + ".txt")).read_bytes()
    return [(name, start, length) for name, start, length, _, kind
            in inspect_fields(feed, original, raw, schema_file)[0] if kind == "text"]


def verify_output(feed, original, protected, policy_file=None, raw=False, schema_file=None):
    rules = catalog(policy_file)
    rules.update({path: rule for path, rule in json_rules(feed, policy_file=policy_file).items() if "id" not in rule})
    rules.setdefault("KeepStructural", {"action": "Keep", "identification": "None"})
    before, after = (original, protected) if raw else (payload(original), payload(protected))
    if not raw:
        assert header(original) == header(protected), "MQ metadata changed"
        assert len(original) == len(protected), "MQ container byte count changed"
        assert original.endswith(b"\n") == protected.endswith(b"\n"), "Final newline changed"
        assert [len(row) for row in original.splitlines()] == [len(row) for row in protected.splitlines()]
    assert len(before) == len(after), "Binary payload width changed"
    expected = bytearray(before)
    actions = Counter()
    fields, framing = inspect_fields(feed, original, raw, schema_file, policy_file=policy_file)
    for name, start, length, policy, kind in fields:
        rule = rules[policy]
        if rule["action"] == "Mask":
            assert kind == "text" and rule["identification"] == "Known"
            count = length if rule["maskCharacters"] == "all" else min(length, int(rule["maskCharacters"]))
            offset = start if rule.get("fromEnd", "true") == "false" else start + length - count
            expected[offset:offset + count] = rule.get("maskCharacter", "*").encode("ascii") * count
        else:
            assert rule["action"] == "Keep"
        # Count populated text fields separately from framing words and absent values.
        if kind == "text" and length:
            actions[rule["action"]] += 1
        if name.rsplit("/", 1)[-1].lower() in {"pnr", "newpnr", "oldpnr"}:
            assert before[start:start + length] == after[start:start + length], "PNR changed"
    for start, length in framing:
        assert before[start:start + length] == after[start:start + length], "String count or padding changed"
    assert bytes(expected) == after, "Masking or preserved framing bytes differ"
    return dict(actions)
