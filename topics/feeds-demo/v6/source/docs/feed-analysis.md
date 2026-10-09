# Finalized feed analysis

All source exports were read locally. The original folder was not modified. Project copies preserve exact bytes; `TKT.txt` is named `ETKT.txt` under the confirmed project route.

| Source | Project fixture | Payload bytes | Source SHA-256 |
| --- | --- | ---: | --- |
| ACI.txt | ACI.txt | 888 | `f038c05e590ade818748f6547c2a624d8d662a52c54d3596ad93649eafce022d` |
| PNR.txt | PNR.txt | 1,523 | `5c8fc06ba95fb17048fe0e003cce8ce3ca622b2abf55b49b0d537b54e20a62b6` |
| PNR_LINKING.txt | PNR_LINKING.txt | 1,523 | `efa2103ffe74b92474b610c00a46eba64e0643dcc459c502166413eff855acb0` |
| SEATS.txt | SEATS.txt | 1,362 | `3f7fd3eb9d48c5b646c3ae73c7b982f419c2c86476370bf9c8ffbddc2a19fccf` |
| TKT.txt | ETKT.txt | 672 | `6a0f5f65a53ff39fee57fdf8dbb870a28dc4fe136296ba4c151c277e99b78cfa` |

## Findings

- All files use an A/X IBM MQ export envelope. The reader preserves metadata independently of the application payload.
- SEATS is complete UTF-8 JSON with old/new customer groups. Protection covers every observed scalar path, including sensitive attribute arrays. JSON order, grouping, scalar types and unchanged values are preserved.
- PNR_LINKING decodes as a JSON array but ends inside the second customer record, at an unterminated `UaRecordLocator` string. The payload fails strict parsing at byte/character 1510 (zero-based). A complete first customer is not sufficient to publish any part of the message.
- ACI, PNR and ETKT contain non-JSON binary records. Visible strings are insufficient to establish record boundaries, identity fields, padding, lengths or serialization rules. No authoritative producer layouts accompany these exports.
- The A/X files do not declare a total application-payload length. Hex decoding and envelope round trips cannot establish completeness of proprietary binary payloads; the producer schema must validate that.
- ACI declares CCSID 500; PNR and ETKT declare 437. Their payloads are not blindly converted using those code pages. The JSON exports declare CCSID 1208 and are decoded as UTF-8.

## Producer information still required

For ACI, PNR and ETKT, provide the schema/record definitions and version discriminator, encoding and byte-order rules, optional-field and array layout, string-length and padding rules, and the matching serializer or source code. Include a producer round-trip fixture with expected decoded fields and preserved PNR/passenger associations.

For PNR-Linking, provide a complete message and the full field contract. Configuration covers visible scalar paths only; previously unseen scalar paths reject the message until explicitly reviewed and configured.

## Verified installed stack

- `presidio-analyzer`: `2.2.364`
- `presidio-anonymizer`: `2.2.364`
- `spacy`: `3.8.16`
- `en-core-web-lg`: `3.8.0`
