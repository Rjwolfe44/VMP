# VMP message compression

Vessels, kerbals, scenarios, contracts and craft files are sent as text blobs that compress very well. VMP compresses them
with Zstandard (zstd). This page describes what is compressed, how the level is chosen, which settings exist and how the
server protects itself against hostile data.

## What is compressed

The payload of these message types, in both directions:

| Statistics name      | Content                                  |
|----------------------|------------------------------------------|
| `VesselProtoMsgData` | A whole vessel (the biggest payloads)    |
| `KerbalInfo`         | A kerbal                                 |
| `ScenarioInfo`       | A scenario module (funds, science, ...)  |
| `ContractInfo`       | A contract                               |
| `CraftInfo`          | A craft file of the craft library        |

Everything else (positions, flight state, chat, the persistent-sync snapshots, which have their own encoding) is not touched.
The sizes before and after are shown by `GET /api/v1/network` (`messageCompression`, see `admin-api.md`) and in the Network tab of
the VMP server manager.

## Codecs and how the receiver tells them apart

* **zstd** is what the server sends. A zstd frame starts with the bytes `28 B5 2F FD`.
* **QuickLZ** is what earlier versions used. It is still decoded, and a client that cannot compress with zstd (see below) still
  sends it.
* A payload that starts with neither is raw.

The receiver looks at the first bytes of every payload, so the three can be mixed freely and no setting has to match between
server and client.

The KSP client ships `System.Runtime.CompilerServices.Unsafe.dll` 6.0.0 (earlier builds shipped 4.5.3, which can decode zstd but not
compress with it, because the zstd library calls `Unsafe.SkipInit`, added in Unsafe 5.0). At start the client checks once that it can
compress; when it cannot (for example another mod's older `Unsafe.dll` is the copy KSP kept) it sends QuickLZ (the server reads
both) and says so in its log (`Message compression: ... compresses with zstd: False`). `External/Dependencies/ZstdSharp.README.txt`
has the details.

## What is sent uncompressed

* Payloads smaller than `MinSizeBytes` (default 160 bytes).
* Payloads that would not become at least 8 bytes smaller. They go out as they are.

## Modes and levels

Zstd levels run from 1 (fastest) to 19 and beyond (smallest, slow). Set `Mode` in the `compression` settings:

* **Adaptive** (default): the server picks a level for every payload.
  1. Start from `ServerLevel` and add an offset by payload size: up to 256 KB, +6; up to 1 MB, +0; up to 4 MB, -5; above, -8.
     With the defaults that is level 15 for typical vessels, 9 for big ones, 4 and 1 for very big ones.
  2. Add the feedback adjustment (see below), then keep the result between 1 and `MaxAdaptiveLevel`.
  3. Lower the level while the measured speed of that level predicts that this payload would need more than 25 ms.
* **Fixed**: always `ServerLevel` for every payload.

Clients use the same idea with `ClientLevel` as both centre and cap (offsets 0, -1, -2, -3, -4, -5 by payload size) and a time
budget of 3 ms. Whatever the server says, a client never uses a level above 9 and never above 3 for a payload over 4 MB.

### Feedback

For every real compression the server records how long it took. It keeps a smoothed average per message (payloads up to 256 KB
only) and a smoothed time per KB for each level.

* When the average per message is above the budget (server 25 ms, client 3 ms) the level adjustment goes down by one (at
  most to -18).
* When it is below a third of the budget and the level in use is still below the cap, the adjustment goes up by one (at most to +8).
* The adjustment changes at most once every 5 seconds and not before three measurements exist.

`effectiveLevelNow` in the API shows the level a typical 32 KB payload gets right now.

## Reuse of compressed payloads

The server often sends the same vessel, kerbal or scenario more than once, for example to every player that joins. The compressed
bytes are kept in a cache of at most 64 MB, keyed by message type, content hash, size and level. A repeat costs a hash of the
data instead of a compression. A changed content has a different hash and is compressed again; a different level is a different
entry; the oldest entries are dropped first when the cache is full. `cacheHits` and `cacheMisses` in the API count the lookups.
Clients do not use the cache.

## Settings

Section `compression` of `Config/vmp-server.json`, or the Compression section of the Settings tab of the VMP server manager, or
`PUT /api/v1/settings` (keys `compression.Mode` and so on). All of them apply immediately, no restart is needed.

| Setting            | Default  | Range      | Meaning                                                                                      |
|--------------------|----------|------------|----------------------------------------------------------------------------------------------|
| `Mode`             | Adaptive | Adaptive, Fixed | How the server picks the level (above).                                                  |
| `ServerLevel`      | 9        | 1-19       | The level in Fixed mode and the centre of the adaptive range.                                |
| `ClientLevel`      | 5        | 1-9        | The highest level clients use. Sent to every client when it joins.                           |
| `MaxAdaptiveLevel` | 15       | 1-19       | The highest level adaptive mode may use on the server.                                       |
| `MinSizeBytes`     | 160      | 0-65536    | Smaller payloads are sent uncompressed. Sent to every client when it joins.                  |

Values outside the ranges are refused by the admin API; a hand-edited file with such a value is clamped to the range where it is
used. A client of a server that does not send the two client values keeps level 5 and 160 bytes.

Measured on real vessel files (60 files, 3.7 MB, average 62 KB), on the machine the benchmark ran on: QuickLZ shrank them 9.4
times, zstd level 3 13 times, level 9 14.7 times and level 15 38.9 times; level 3 needed 4 ms per MB, level 9 6 ms, level 15
19 ms and level 19 97 ms. Decoding costs about 1 ms per MB whatever the level. Kerbals and scenarios are small (0.3 to 4 KB) and
shrink 1.5 to 3 times at every level, so the level hardly matters for them. The benchmark is the skipped-by-default test
`CompressionBenchmark` in `ServerTest`; it reads the folders named in `VMP_BENCH_VESSELS`, `VMP_BENCH_KERBALS` and
`VMP_BENCH_SCENARIOS`.

## Limits when decoding

The server decodes what clients send, so decoding is bounded:

* A zstd frame that declares more than 64 MB of content is refused before anything is allocated. A QuickLZ payload that declares
  more than 64 MB is refused the same way.
* A frame that declares a large size is decoded into a buffer that starts small and grows only while the data really needs it, so a
  tiny hostile frame cannot make the server allocate 64 MB. A corrupt frame stops decoding at once.
* A frame that does not declare its size is decoded the same way and stops at 64 MB.
* A frame that names a dictionary is refused (this version has none).
* Every failure is reported as an invalid message: the server logs `Error deserializing message!` with the reason and drops that
  message. The connection itself is not affected.
