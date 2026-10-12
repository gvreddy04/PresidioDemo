# Feed format analysis

The selected scope now includes complete configured JSON feeds and the explicitly reviewed ACI, ETKT and PNR reference frames. Incomplete messages and binary layouts outside reviewed configuration remain retained without output.

The current PNR_LINKING.txt replaces the earlier truncated export with a complete 2,217-byte JSON payload. The updated PNR.txt now contains 5,452 aligned binary payload bytes; its previous 1,523-byte export remains archived. ACI, ETKT and Seats retain their previously reviewed checksums. This review did not modify the supplied files. The project calls the supplied `TKT.txt` feed **ETKT**, as confirmed.

## Outer file representation

Every supplied `.txt` file is an ASCII IBM MQ export with `A` metadata records and `X` hexadecimal payload records. These files are text containers; reconstructing the `X` rows produces application payload bytes. The representation matches qload/dmpmqmsg-style export conventions. IBM describes dmpmqmsg exports as human-readable files in a utility-specific format: [IBM MQ export documentation](https://www.ibm.com/docs/en/ibm-mq/9.3.x?topic=objects-using-dmpmqmsg-utility-between-two-systems).

## Application payload classification

| Source file | Project route | Payload bytes | Classification | Completeness / certainty |
| --- | --- | ---: | --- | --- |
| ACI.txt | ACI | 888 | Binary with strong XDR structural evidence | Length is aligned; the supplied 888-byte reference frame now has a strict configured codec; a general producer schema remains unavailable |
| PNR.txt | PNR | 5,452 | Binary with strong XDR structural evidence | The supplied frame has a strict reference profile for 106 selected fields; a general producer schema and complete semantic classification remain unavailable |
| TKT.txt | ETKT | 672 | Binary with strong XDR structural evidence | The supplied 672-byte reference frame now has a strict configured codec; a general producer schema remains unavailable |
| SEATS.txt | Seats | 1,362 | UTF-8 JSON object | Strict parsing succeeds; active processing supported |
| PNR_LINKING.txt | PNR-Linking | 2,217 | UTF-8 JSON array | Strict parsing succeeds; 49 exact scalar paths are configured, including the previously missing transitionLinkSegments |

XDR is a binary serialization format. Its integers use big-endian byte order; counted strings have a four-byte length followed by their contents and zero padding to a four-byte boundary. These rules come from [RFC 4506](https://www.rfc-editor.org/rfc/rfc4506.html#section-4.11).

## Evidence for the XDR inference

The binary scans look only for plausible counted ASCII strings of 2â€“128 bytes at four-byte-aligned offsets, followed by the required zero padding. Candidate counts are heuristic evidence, not a complete parse or schema validation.

| Payload | Big-endian candidates | Little-endian candidates | Payload length modulo 4 |
| --- | ---: | ---: | ---: |
| ACI | 22 | 0 | 0 |
| PNR | 141 | 0 | 0 |
| ETKT | 8 | 0 | 0 |

For example, ACI contains the following bytes at payload offset 8, and PNR contains the same sequence at offset 4:

```text
00 00 00 02 | 55 41 | 00 00
length = 2 | "UA"  | two padding bytes
```

ETKT contains a seven-byte date string at offset 12 with one zero padding byte. PNR contains nine-byte date strings with three zero padding bytes. Repeated matches across the three feeds provide strong support for the XDR hypothesis, although an equivalent custom serialization could share these properties.

The updated PNR payload has 5,452 bytes, a multiple of four, and passes the previous alignment check. It is a different message rather than a simple extension of the old export: its first differing payload byte is at offset 35. Alignment and counted-string candidates support the XDR hypothesis but cannot validate message completeness or establish field meanings, array boundaries and optional records. The previous unaligned export remains preserved in the earlier review snapshot.

## Implications for processing

- Keep the active Seats JSON codec and configured field protection. The complete PNR-Linking export now resolves all scalar paths and can produce validated output.
- Classify ACI, PNR and ETKT as **XDR candidates**, with strong structural evidence. The supplied ACI, ETKT and PNR reference frames now use explicit restricted profiles. Other binary layouts remain unsupported until reviewed definitions are available.
- XDR supplies encoding rules, not airline field identities. A schema is needed to distinguish names, passenger IDs, PNR, arrays, unions and optional records. The producer's `.x` definitions or generated serialization functions would provide that contract.
- Preserve MQ metadata separately. `FMT`, `CCS` and `ENC` do not establish the application's field layout; the observed payload evidence must be evaluated independently.
- Do not feed raw binary or hex text directly to Presidio. Decode schema-defined fields, apply configured protection, then serialize through the matching schema.

## Reproducibility and preservation

Structural evidence and source SHA-256 values are saved in `topics/feeds-demo/v7/feed-format-evidence.json`. The supporting read-only probe is `topics/feeds-demo/v7/analyze-feed-formats.py`. It records offsets, lengths and padding without decoded personal values. All prior source snapshots remain preserved. The new PNR structural scan and source snapshot are recorded under `D:/Bala_Support/topics/feeds-demo/v24/`; the earlier v7 scan describes the historical inputs.

## Source checksums

| Source | Project fixture | Payload bytes | Source SHA-256 |
| --- | --- | ---: | --- |
| ACI.txt | ACI.txt | 888 | `f038c05e590ade818748f6547c2a624d8d662a52c54d3596ad93649eafce022d` |
| PNR.txt | PNR.txt | 5,452 | `75cc60895384395f8df28eff4c4abf6a0c58f1dc7960c5eee41cb294def0e4de` |
| PNR_LINKING.txt | PNR_LINKING.txt | 2,217 | `2a8caf4538eee4a2232bdcc939adf427171d5ab35a6b380059b2e09eb5c03610` |
| SEATS.txt | SEATS.txt | 1,362 | `3f7fd3eb9d48c5b646c3ae73c7b982f419c2c86476370bf9c8ffbddc2a19fccf` |
| TKT.txt | ETKT.txt | 672 | `6a0f5f65a53ff39fee57fdf8dbb870a28dc4fe136296ba4c151c277e99b78cfa` |

## ACI reference implementation

The approved sample is now processed through the complete listener workflow using reference-layouts.xml and explicit field policies. All 888 bytes are validated: 23 text fields are extracted from 22 counted strings; six fields are masked and 17 kept. Both PNR copies, unknown binary words, string counts/padding and MQ metadata remain unchanged. Known field policies require no NLP detection.

This is a reference-frame implementation, not a recovered general producer schema. Unknown binary bytes must match the reviewed original exactly, and field lengths cannot change. Numeric/opaque regions have not been semantically classified. The complete evidence and limitations are in [the ACI workflow report](../topics/feeds-demo/v11/aci-reference-workflow-v11.md).

## ETKT and linking verification

The supplied ETKT reference frame is now configured for nine text fields: five direct masks and four Keeps, including PNR. Its full 672 bytes and MQ container are verified before publication, with every unknown byte frozen to the original reference. The updated supplied PNR-Linking export is complete and is verified alongside the fictional two-passenger fixture. Its policy now contains exactly 49 sample scalar paths; 26 unused generic aliases were removed. The new transitionLinkSegments rule preserves its observed null and conservatively replaces future scalar text; nested unreviewed fields fail closed. MQ metadata, nulls, numeric types, grouping, PNROwner and customer UaRecordLocator values are preserved. The earlier truncated message remains covered by a separate rejection fixture.

The updated PNR payload has 5,452 bytes and passes the XDR alignment check. Its selected readable fields now have a restricted reference profile, described below. A general producer schema remains unavailable. Obtain the producer `.x` definitions or generated serializer/deserializer contract, including field order/types, collection bounds, optional/union rules and framing, to support other layouts and confirm semantic relationships. The attachment was preserved byte-for-byte.

## PNR configuration mapping comparison

The mapping review found 36 provisional aliases: 16 Keep rules, 19 Known replacements and one Analyze replacement for remarks. Before the selected reference implementation, none was bound to the supplied binary payload because PNR had neither a producer schema nor a reference layout. Cleanup removed all 36 and replaced them with an explicit unconfigured PNR policy. The comparison below records the removed rules and the observed gaps; it does not describe enabled mappings.

There was also a path mismatch in the removed aliases: they used bare names such as `pnr` and `firstName`, while `XdrFeedCodec` emits slash-prefixed paths such as `/pnr` and nested wildcard paths. `FeedPolicy.Get` requires the configured path and has no bare-name fallback. Prefixing every alias now would still leave field order, nesting, types and passenger associations unknown.

The following offsets identify observed counted-string length prefixes in the decoded payload; string values start four bytes later. Business interpretations are inferred from visible text, rather than confirmed producer field definitions. This comparison predates the selected reference implementation; its inferred field meanings still need producer confirmation.

| Category | Removed provisional rules | Observed prefix offsets | Mapping finding |
| --- | --- | --- | --- |
| PNR and linked locators | `pnr`, `linkedPnr`, `oldPnr`, `newPnr`: Keep | 80 | Locator-like six-character value; actual PNR identity and old/new/link relationships need confirmation. |
| Names | Seven Known PERSON aliases | 400, 440, 1880, 1984, 5276 | Name-like fragments repeat inside documents, contacts, tickets and remarks; no extraction path or passenger grouping is defined. |
| Stable passenger ID | `sourcePassengerId`, `passengerId`: Replace | Not established | No stable source identity is confirmed; PNR, names, tickets and array position cannot substitute for it. |
| Email | `email`, `emailAddress`: Replace | 876, 3508 | CTCE text uses `//`; a remark uses `-AT-`. Standalone email aliases do not define how to extract these composite records. |
| Phone | `phone`, `phoneNumber`, `mobile`: Replace | 972, 2052, 2060 | Phone-like text appears in CTCM and separate three- and seven-digit strings; boundaries and ownership need a producer definition. |
| Document and DOB | Passport and DOB aliases: Replace | 496 | DOCS has a date-like fragment and empty document-number-like slots; no standalone DOB or populated passport field is confirmed. |
| Postal address | `address`: Replace | 3896, 3940, 3988, 4032, 4072, 4116 | ADDR, ADDRCITY, ADDRSTATE and ADDRZIP span multiple records; no reconstruction/grouping rule exists. |
| Loyalty, ticket and order references | No dedicated PNR rules | 1628, 1732, 1860, 1964, 3100, 3172, 3816, 4384, 4860, 4944 | Sensitive reference-like text occurs in mixed records; explicit classification and protection are needed after extraction is confirmed. |
| IP and payment-related text | No dedicated PNR rules | 2852, 2904, 4316 | IP and masked-card/payment-like records need review; generic remarks analysis does not establish coverage. |
| Operational metadata | Twelve other Keep aliases | Multiple airline/date/route/status-like strings | Current values cannot be assigned confidently to tenant, message ID, flight, event or other aliases without the producer contract. |

### Direct remarks-rule diagnostic

The removed remarks rule selected only `PERSON`, `EMAIL_ADDRESS` and `PHONE_NUMBER` at threshold 0.5. It does not request DOB, postal addresses, loyalty, ticket, order/payment references or IP entities. To test its practical limits, the existing local Presidio function was invoked directly on nine observed string candidates using that exact rule. This diagnostic did not parse or publish a PNR feed.

| Selected observed fragment | Prefix offset | Fragment remains after configured remarks action |
| --- | ---: | --- |
| DOCS date-like fragment | 496 | No |
| CTCE encoded email | 876 | Yes |
| CTCM phone | 972 | Yes |
| Loyalty account | 1860 | Yes |
| Ticket-number-like value | 1628 | Yes |
| IP address | 2904 | Yes |
| Remark encoded email | 3508 | No |
| Street address | 3940 | Yes |
| Order UUID | 3816 | Yes |

Seven selected fragments and their entire records remained unchanged. The other two fragments changed, which does not establish correct entity classification or complete protection. Known protection on confirmed extracted fields, with explicit handling for mixed SSR/OSI/contact/address records, is needed; that Analyze rule is insufficient as blanket coverage.

The detailed comparison of every configured alias, exact segment lengths, probe results and source checksums is saved in `D:/Bala_Support/topics/feeds-demo/v25/config-mapping-comparison.json`; its reproducible probe is alongside it. The comparison preserved the supplied file and runtime configurations. Subsequent configuration cleanup removed the unbound PNR aliases and introduced an explicit unconfigured policy. At that cleanup stage, PNR processing rejected the supplied binary at the missing-schema guard; supplying a layout alone could not bypass the unconfigured protection policy. The selected reference profile below now enables this supplied frame. PNR-Linking was rechecked against the attachment: all 49 configured paths match, with no missing or unused rules, so its rules and protection actions were preserved.

## Selected PNR reference implementation

The initial PNR field selection is now configured in `reference-layouts.xml` and `feed-policies.xml`. It covers 94 counted-string records and exposes 106 exact field paths: 76 full-span masks and 30 Keep rules. Protection uses known spans through local Presidio, with no dependence on NLP recognition of encoded contact details or mixed remarks.

| Selected data | Action |
| --- | --- |
| PNR, selected event/travel dates and times, airlines, flight number, routes, fare amounts, mileage and passenger-count text | Keep |
| Names and repeated passenger-name fragments | Mask |
| DOCS date of birth and gender | Mask |
| Encoded emails, CTCM phone and separate phone fragments | Mask |
| Address name, street, city, state, postal code and remaining address text | Mask |
| Loyalty account/status, sponsor and balance | Mask |
| Ticket numbers, order references, approval references, payment suffix and IP address | Mask |
| Selected free-form remarks and unidentified reference-like values | Mask |

Mixed records preserve their reviewed prefixes and separators through `fixed="true"` parts. Fixed parts are excluded from mutable field paths and must match the source reference exactly. String length words, padding and all other unclassified bytes are also pinned. Selected field values can vary only within the configured ASCII widths; output must retain the complete binary frame and MQ metadata. PNR stays unchanged.

This profile supports the supplied 5,452-byte frame and matching layouts only. It does not recover a general producer schema, classify every numeric/opaque region, establish passenger ownership or identify a stable source passenger ID. Reference meanings are based on visible sample text. Tokenization remains unimplemented. General PNR XDR configuration stays disabled until the producer contract is available.

The field inventory and reproducible validation evidence are preserved under `D:/Bala_Support/topics/feeds-demo/v27/`. Earlier comparison and cleanup snapshots remain unchanged.
