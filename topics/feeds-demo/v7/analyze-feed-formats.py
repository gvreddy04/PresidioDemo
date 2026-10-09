"""Read-only feed format evidence. Heuristics classify candidates, never decode production schemas."""
from pathlib import Path
import hashlib
import json
import re
import struct
import sys


def counted_string_candidates(data, endian):
    candidates = []
    for offset in range(0, len(data) - 4, 4):
        length = struct.unpack_from(endian + 'I', data, offset)[0]
        end = offset + 4 + length
        if not 2 <= length <= 128 or end > len(data):
            continue
        value = data[offset + 4:end]
        if not all(32 <= byte <= 126 for byte in value):
            continue
        padding_length = (-length) % 4
        padding = data[end:end + padding_length]
        if len(padding) == padding_length and not any(padding):
            # Retain structural evidence only, without personal field contents.
            candidates.append({'length_prefix_offset': offset, 'string_bytes': length,
                               'zero_padding_bytes': padding_length})
    return candidates


def analyze(folder, previous):
    results = []
    for file in sorted(folder.glob('*.txt')):
        raw = file.read_bytes()
        lines = raw.decode('ascii').splitlines()
        attributes = {}
        rows = []
        body = False
        for line in lines:
            if line.startswith('A ') and not body:
                match = re.fullmatch(r'A ([A-Z0-9]{3}) (.*)', line)
                if not match or match[1] in attributes:
                    raise ValueError('Invalid/duplicate MQ metadata in ' + file.name)
                attributes[match[1]] = match[2].strip()
            elif line.startswith('X '):
                body = True
                if not re.fullmatch(r'(?:[0-9a-fA-F]{2})+', line[2:]):
                    raise ValueError('Invalid MQ hex in ' + file.name)
                rows.append(bytes.fromhex(line[2:]))
            else:
                raise ValueError('Unexpected MQ export record in ' + file.name)
        if not rows:
            raise ValueError('Missing payload in ' + file.name)
        data = b''.join(rows)
        project_name = 'ETKT.txt' if file.name == 'TKT.txt' else file.name
        preserved = previous / project_name
        record = {'source_file': file.name, 'project_file': project_name,
                  'source_bytes': len(raw), 'source_sha256': hashlib.sha256(raw).hexdigest(),
                  'matches_previously_analyzed_copy': preserved.exists() and preserved.read_bytes() == raw,
                  'outer_format': 'ASCII IBM MQ A/X hexadecimal text export',
                  'payload_bytes': len(data), 'payload_length_modulo_4': len(data) % 4,
                  'mq_metadata': {k: attributes.get(k) for k in ['VER', 'ENC', 'CCS', 'FMT', 'ORL', 'OFF', 'MSF']}}
        if data.startswith((b'{', b'[')):
            try:
                value = json.loads(data.decode('utf-8'))
                record.update(payload_format='UTF-8 JSON', valid_json=True,
                              root_type='object' if isinstance(value, dict) else 'array')
            except json.JSONDecodeError as error:
                record.update(payload_format='UTF-8 JSON', valid_json=False,
                              error=error.msg, error_character_offset=error.pos)
        else:
            big = counted_string_candidates(data, '>')
            little = counted_string_candidates(data, '<')
            record.update(payload_format='Binary; strong XDR structural evidence, schema unconfirmed',
                          big_endian_aligned_counted_string_candidates=len(big),
                          little_endian_aligned_counted_string_candidates=len(little),
                          candidate_layouts=big,
                          complete_standalone_xdr_length_possible=len(data) % 4 == 0,
                          schema_validated=False)
        results.append(record)
    return results


if __name__ == '__main__':
    folder = Path(sys.argv[1])
    previous = Path(sys.argv[2])
    output = Path(sys.argv[3])
    result = analyze(folder, previous)
    with output.open('x', encoding='utf-8') as stream:
        json.dump(result, stream, indent=2)
        stream.write('\n')
    print('Analyzed', len(result), 'files; saved structural evidence without decoded personal values.')
