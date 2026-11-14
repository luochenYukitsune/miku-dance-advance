# CP932 duplicate spellings

Source: https://www.unicode.org/Public/MAPPINGS/VENDORS/MICSFT/WINDOWS/CP932.TXT
Microsoft CP932 mapping, table version 2.01, 1998-04-15; retrieved 2026-10-26.
Source SHA256: c9bc0b0cd42e0fbcb82a09635bb5abed86afbdd4abc9e76fa5716638217cb59f

The embedded resource contains every mapping whose Unicode value occurs more than once in that table: 794 pairs, 396 Unicode values, 3176 bytes. Each record is a little-endian uint16 original byte code followed by a uint16 Unicode scalar, sorted by original byte code. This includes runtime-canonical spellings; only the strict decoder fallback consumes it. Unknown/truncated sequences still throw, and encoding uses the runtime canonical mapping. Original PMM/VMD names keep their captured bytes.

Unicode License V3 is shipped as LICENSE.unicode.txt in Formats output. No full mapping table, heuristic encoding detector, or Windows-only codec is required in the product. The official mapping, license original, and full strict-runtime audit remain in peripheral Formats/20261026/PmmNames01 evidence.