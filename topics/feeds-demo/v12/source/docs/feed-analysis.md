# Feed format analysis

The selected scope now includes complete configured JSON feeds and the explicitly reviewed ACI and ETKT reference frames. Incomplete messages and binary layouts outside reviewed configuration remain retained without output.

All five current files match the previously analyzed copies byte-for-byte. Source files were not changed. The project calls the supplied `TKT.txt` feed **ETKT**, as confirmed.

## Outer file representation

Every supplied `.txt` file is an ASCII IBM MQ export with `A` metadata records and `X` hexadecimal payload records. These files are text containers; reconstructing the `X` rows produces application payload bytes. The representation matches qload/dmpmqmsg-style export conventions. IBM describes dmpmqmsg exports as human-readable files in a utility-specific format: [IBM MQ export documentation](https://www.ibm.com/docs/en/ibm-mq/9.3.x?topic=objects-using-dmpmqmsg-utility-between-two-systems).

## Application payload classification

| Source file | Project route | Payload bytes | Classification | Completeness / certainty |
| --- | --- | ---: | --- | --- |
| ACI.txt | ACI | 888 | Binary with strong XDR structural evidence | Length is aligned; the supplied 888-byte reference frame now has a strict configured codec; a general producer schema remains unavailable |
| PNR.txt | PNR | 1,523 | Binary with strong XDR structural evidence | Length is not four-byte aligned; cannot be a complete standalone XDR stream starting at byte zero |
| TKT.txt | ETKT | 672 | Binary with strong XDR structural evidence | The supplied 672-byte reference frame now has a strict configured codec; a general producer schema remains unavailable |
| SEATS.txt | Seats | 1,362 | UTF-8 JSON object | Strict parsing succeeds; active processing supported |
| PNR_LINKING.txt | PNR-Linking | 1,523 | UTF-8 JSON array | Truncated inside a JSON property name; strict parsing fails at character offset 1510 |

XDR is a binary serialization format. Its integers use big-endian byte order; counted strings have a four-byte length followed by their contents and zero padding to a four-byte boundary. These rules come from [RFC 4506](https://www.rfc-editor.org/rfc/rfc4506.html#section-4.11).

## Evidence for the XDR inference

The binary scans look only for plausible counted ASCII strings of 2–128 bytes at four-byte-aligned offsets, followed by the required zero padding. Candidate counts are heuristic evidence, not a complete parse or schema validation.

| Payload | Big-endian candidates | Little-endian candidates | Payload length modulo 4 |
| --- | ---: | ---: | ---: |
| ACI | 22 | 0 | 0 |
| PNR | 47 | 0 | 3 |
| ETKT | 8 | 0 | 0 |

For example, ACI contains the following bytes at payload offset 8, and PNR contains the same sequence at offset 4:

```text
00 00 00 02 | 55 41 | 00 00
length = 2 | "UA"  | two padding bytes
```

ETKT contains a seven-byte date string at offset 12 with one zero padding byte. PNR contains nine-byte date strings with three zero padding bytes. Repeated matches across the three feeds provide strong support for the XDR hypothesis, although an equivalent custom serialization could share these properties.

PNR's size is an additional concern: 1,523 modulo 4 is 3. If the entire payload is one XDR stream beginning at byte zero, it is incomplete or malformed. An external trailer or custom framing is another possible explanation; the current files cannot establish which applies. Do not repair this by simply adding a zero byte. Four-byte alignment in ACI and ETKT is necessary evidence but does not prove message completeness.

## Implications for processing

- Keep the active Seats JSON codec and configured field protection. PNR-Linking requires a complete export and a policy for every scalar path before it can produce output.
- Classify ACI, PNR and ETKT as **XDR candidates**, with strong structural evidence. The supplied ACI and ETKT reference frames now use explicit restricted profiles. Other binary layouts and the unaligned PNR input remain unsupported until complete reviewed definitions or framing are available.
- XDR supplies encoding rules, not airline field identities. A schema is needed to distinguish names, passenger IDs, PNR, arrays, unions and optional records. The producer's `.x` definitions or generated serialization functions would provide that contract.
- Preserve MQ metadata separately. `FMT`, `CCS` and `ENC` do not establish the application's field layout; the observed payload evidence must be evaluated independently.
- Do not feed raw binary or hex text directly to Presidio. Decode schema-defined fields, apply configured protection, then serialize through the matching schema.

## Reproducibility and preservation

Structural evidence and source SHA-256 values are saved in `topics/feeds-demo/v7/feed-format-evidence.json`. The supporting read-only probe is `topics/feeds-demo/v7/analyze-feed-formats.py`. It records offsets, lengths and padding without decoded personal values. All prior source snapshots remain preserved.

## Source checksums

| Source | Project fixture | Payload bytes | Source SHA-256 |
| --- | --- | ---: | --- |
| ACI.txt | ACI.txt | 888 | `f038c05e590ade818748f6547c2a624d8d662a52c54d3596ad93649eafce022d` |
| PNR.txt | PNR.txt | 1,523 | `5c8fc06ba95fb17048fe0e003cce8ce3ca622b2abf55b49b0d537b54e20a62b6` |
| PNR_LINKING.txt | PNR_LINKING.txt | 1,523 | `efa2103ffe74b92474b610c00a46eba64e0643dcc459c502166413eff855acb0` |
| SEATS.txt | SEATS.txt | 1,362 | `3f7fd3eb9d48c5b646c3ae73c7b982f419c2c86476370bf9c8ffbddc2a19fccf` |
| TKT.txt | ETKT.txt | 672 | `6a0f5f65a53ff39fee57fdf8dbb870a28dc4fe136296ba4c151c277e99b78cfa` |

## ACI reference implementation

The approved sample is now processed through the complete listener workflow using reference-layouts.xml and explicit field policies. All 888 bytes are validated: 23 text fields are extracted from 22 counted strings; six fields are masked and 17 kept. Both PNR copies, unknown binary words, string counts/padding and MQ metadata remain unchanged. Known field policies require no NLP detection.

This is a reference-frame implementation, not a recovered general producer schema. Unknown binary bytes must match the reviewed original exactly, and field lengths cannot change. Numeric/opaque regions have not been semantically classified. The complete evidence and limitations are in [the ACI workflow report](../topics/feeds-demo/v11/aci-reference-workflow-v11.md).

## ETKT and linking verification

The supplied ETKT reference frame is now configured for nine text fields: five direct masks and four Keeps, including PNR. Its full 672 bytes and MQ container are verified before publication, with every unknown byte frozen to the original reference. Complete PNR-Linking MQ messages are verified with a separate fictional two-passenger fixture. The actual PNR and PNR-Linking files remain unpublished because their complete boundaries are not established. See [the current workflow report](../topics/feeds-demo/v12/remaining-feed-workflows-v12.md) and [incomplete-source evidence](../topics/feeds-demo/v12/incomplete-source-evidence.json).
