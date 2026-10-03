# zvec spike (WP0), 2026-10-03

The go / no-go spike of [docs/search-zvec-plan.md](../../search-zvec-plan.md) for
[ADR-0044](../../adr/0044-zvec-search-store.md). Its outcome is **go, with layout changes** (below).

- **zvec:** `main` at `13fd088` (2026-09-29, after v0.7.0), built from source on linux-x64. `BUILD_C_BINDINGS=ON`,
  tools and Python off, `USE_OSS_MIRROR=ON` (explained under Build).
- **Machine:** 4 cores, 15 GB RAM, the cloud container these sessions run in.
- **Harness:** [`Program.cs`](Program.cs) and [`Native.cs`](Native.cs), a .NET 10 console app over the C API with
  `LibraryImport`. It is not part of the solution. Run it with
  `LD_LIBRARY_PATH=<zvec>/build/lib dotnet run -- probes|scale|crash|escape <dir>`.
- **Raw output:** [probes.txt](probes.txt), [scale.txt](scale.txt), [crash.txt](crash.txt).

## Answers

| # | Question | Answer |
|---|---|---|
| S1 | Can a row exist without a vector? | **Yes.** A nullable vector column can stay empty. Vector queries skip such rows, and `update` adds the vector later. |
| S2 | Does `update` change only the given columns? | **Yes.** Text, other metadata and the vector are kept. |
| S3 | Does full-text search support prefix queries (`inv*`)? | **No.** The FTS grammar has no prefix terms. A trigram column (`ngram` tokenizer, 3–3) with `+lea +ear +arn` finds `learn*`; it matches substrings, not only prefixes. |
| S4 | How fast is `scope_id IN (…)` with many scopes? | **Fast enough.** Over 200k rows, top 200, median: keyword 16 / 26 / 74 ms, vector 8 / 18 / 59 ms, hybrid 17 / 23 / 62 ms with 1k / 5k / 20k scope ids. |
| S5 | Does a query see writes before a flush, in the same process? | **Yes.** |
| S6 | Does a multi-route result say which route matched? | **No.** There is one fused score per row. `matchedBy` needs one cheap route query per side for the final page (`doc_id IN (…)`). |
| S7 | Does a filter-only search through a constant token work? | **Yes.** A whitespace-tokenized `all` column with one token: 24–79 ms for the top 1,000 with the same scope filters. |
| S8 | Are acknowledged writes kept after `kill -9`? | **Yes, in all 3 rounds.** Batches are **not atomic**: 56 rows of an unacknowledged 100-row batch were visible after recovery. |
| S9 | Which libraries are linked in, under which licenses? | Only the 380 `zvec_*` C functions are exported, and the library depends on libc and libm alone (static libstdc++). The table below lists what is statically included. |

## Findings that change the layout

1. **Only numeric columns can be added later.** `add_column` accepts INT32 to DOUBLE; STRING, BOOL, arrays and
   vectors are refused ("requires a numeric field"). The limit is 1,024 scalar columns.
   - Per-field string columns (`f_vendor_s`) are therefore not possible.
   - **New layout:** every text-kind and boolean field value becomes a token in one `field_tokens` STRING-array
     column, created with the collection. Numeric, date and time fields get their own DOUBLE or INT64 columns,
     added the first time they appear.
   - **A model change needs a new collection.** It is a new collection generation, copied with the iterator and
     embedded again.
2. **Backslashes in string literals are not unescaped** in filters. `'O\'Brien'` works; a value containing `\` can
   never be matched. User strings therefore never go into filter text: field tokens are
   `{kind}:{name}:{base64url(value)}`, so filter literals hold only `[A-Za-z0-9_:-]`.
3. **The equality operator is `=`.** `==` is a syntax error.
4. **`topk` is at most 100,000.**
5. **`zvec_collection_destroy` deletes the collection.** `zvec_collection_close` only releases the handle. The
   binding must never call `destroy` except to drop a tenant's index.
6. **A collection directory takes a `LOCK`.** A second read-write open fails ("Can't lock read-write collection"),
   so the one-server rule is enforced by zvec itself; no lock file of our own is needed.
7. **String arrays are read by guessing from the byte size.** `add_field_by_value(ARRAY_STRING)` treats a size that
   is a multiple of 8 as `zvec_string_t*` pointers, anything else as packed C strings. Packed GUIDs (33 bytes
   each) would be misread whenever there are 8, 16, … of them. The binding always passes `zvec_string_t*`.
8. **Full-text columns cannot be used in filter conditions.** That is fine: filters use scalar columns.
9. **Memory:** the working set reached 1 GB after inserting and querying 200k rows (8-dimension test vectors,
   2 GB memory limit, no mmap). WP4 must set the limit and mmap and measure with real dimensions (768).
10. **Each full-text column costs about 40 MB per open collection, even when empty** (found while building WP4).
    zvec opens one RocksDB instance per full-text column, with a hash skip-list memtable of a million buckets and
    `IncreaseParallelism()`. Neither `max_buffer_size` nor mmap changes it, and the C API cannot tune it. Measured
    resident memory per open collection:
    - 21 full-text columns: 830 MB;
    - 5: 210 MB;
    - 1: 55 MB;
    - 0: about 1 MB, with scalar and vector columns only.

    The store therefore keeps three or four full-text columns per collection and caps the open collections.

## What else worked

- English stemming (`stemmer`, `{"stemmer_lang":"english"}`): `invoice` finds `invoices`.
- `+required -excluded` and phrases.
- Filters: `IN`, `CONTAIN_ANY` on string arrays, ranges on INT64 with a range-optimized inverted index, and
  combinations with `AND`.
- Hybrid RRF across text and vector routes, with one filter.
- `add_column` plus `create_index` on a filled collection, then filtering on it.
- `delete_by_filter`, and reopening after a flush.
- Writes: about 5,600 rows/s in batches of 1,000 with text and 8-dimension vectors. 200k rows take 252 MB on disk,
  215 MB after `optimize` (7 s).

## Build

- **Arrow downloads its dependencies at build time.** From GitHub, the downloads were refused here (403); from
  zvec's mirror (`zvec-bj.oss-cn-beijing.aliyuncs.com`, `USE_OSS_MIRROR=ON`) they worked.
- Our CI build must **pin and mirror these archives itself** (boost, rapidjson, re2, thrift, utf8proc, xsimd,
  zlib) and check their hashes.
- **Size and time:** a cold build is about 40 minutes on 4 cores, and `libzvec_c_api.so` is 39 MB.
- **libaio** is loaded with `dlopen` only when present (DiskANN); it is not linked or shipped.

## Statically included third-party code

| Component | License |
|---|---|
| CRoaring, FastPFOR, RaBitQ-Library, Arrow, Thrift (Arrow) | Apache-2.0 |
| RocksDB | Dual GPL-2.0 / **Apache-2.0** (used under Apache-2.0), with a LevelDB BSD notice |
| cppjieba, limonp, magic_enum, yaml-cpp, utf8proc | MIT |
| antlr4 runtime, gflags, glog, sparsehash, snowball, re2 (Arrow), xsimd (Arrow) | BSD-3-Clause |
| lz4 (library only) | BSD-2-Clause |
| **zlib** (Arrow bundled dependencies) | **zlib License**, not on the allow list |
| **Boost** (Arrow / Thrift build) | **BSL-1.0**, not on the allow list |

**Two licenses need a decision** before WP3 ships a binary: zlib and BSL-1.0. Both are permissive and only ask for
the notice. The options are:
- allow both with a notice (`docs/dependency-licenses.md`);
- or build Arrow without them, if the parts zvec uses allow that.
