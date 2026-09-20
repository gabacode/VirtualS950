# AkaiS950List

Lists the contents of Akai S900/S950 floppy disks, reading Gotek/HxC `.hfe`
images directly — no emulator, no sampler, no disk drive.

Built against two 101-disk Gotek sticks; all images decoded with zero CRC
errors and zero unreadable sectors.

## Build

```
dotnet build -c Release
```

Needs a .NET SDK. The output is `akailist`.

## Use

```
akailist E:\                     list every program on every disk on the stick
akailist E:\ --all               list every file of every type
akailist E:\DSKA0043.hfe --all   list one disk
akailist E:\ --type S            samples only
akailist E:\ --all --csv > inventory.csv
akailist E:\DSKA0000.hfe --refs  show the sample each keygroup references
```

| Option | Effect |
|---|---|
| `--all` | list every file type (default is programs only) |
| `--type X` | filter by type: `P` program, `S` sample, `D` drum set, `O` overall. Combine letters, e.g. `--type PD` |
| `--refs` | for each program, list the sample each keygroup references |
| `--empty` | also show disks with no matching files |
| `--csv` | machine-readable output |

Input may be a `.hfe` image, a raw 819200-byte sector image, or a folder/drive
containing them.

A keygroup may reference a sample held on a *different* disk — the S950 loads
samples into memory across disk changes. Across the 390 programs on one stick,
1744 of 1908 keygroups resolve to a sample on the same disk and the remaining
164 are valid names living elsewhere.

## Disk format

The disk, directory, FAT and sample-file layouts below are documented at
<http://mda.smartelectronix.com/akai/akaiinfo.htm> ("Akai Disk & File Formats").
Every field was independently verified against the images. The **program file
layout is not in that document** and was derived here.

### Physical

HFE rev 0 (`HXCPICFE`), interface mode 12 (S950 DD), 250 kbit/s MFM,
80 cylinders x 2 heads x 5 sectors x 1024 bytes = **800 KB** (819200 bytes).
Sector IDs are 1..5. Logical order is `LBA = (cyl * 2 + head) * 5 + (sec - 1)`.

### Block 0 — directory and FAT

Blocks are 1024 bytes. File data starts at block 4.

| Offset | Size | Contents |
|---|---|---|
| `0x000` | 1536 | directory: 64 entries of 24 bytes |
| `0x600` | 1600 | FAT: one 16-bit little-endian entry per block (3200 bytes for high density) |

FAT entry = next block in the chain; `0x8000` ends the chain; `0x0000` is free.

### Directory entry (24 bytes)

| Offset | Size | Contents |
|---|---|---|
| 0 | 10 | filename, ASCII (`0x00` in byte 0 = free slot) |
| 10 | 6 | zero |
| 16 | 1 | type: `P` program, `S` sample, `D` drum set, `O` overall |
| 17 | 3 | file length in bytes, unsigned, **including** the file header |
| 20 | 2 | starting block, unsigned |
| 22 | 2 | S900 ID = {0,0} |

### Sample file header (60 bytes)

| Offset | Size | Contents |
|---|---|---|
| `0x00` | 10 | filename, ASCII |
| `0x0A` | 6 | zero |
| `0x10` | 4 | number of sample words |
| `0x14` | 2 | sample rate, Hz |
| `0x16` | 2 | tuning: nominal pitch = value/16 (MIDI note), fine pitch = value%16 |
| `0x18` | 2 | sample loudness, signed (reference doc wrongly says zero) |
| `0x1A` | 1 | loop mode: `O` one-shot, `L` loop, `A` alternating |
| `0x1B` | 1 | zero |
| `0x1C` | 4 | end marker |
| `0x20` | 4 | start marker |
| `0x24` | 4 | loop length |
| `0x28` | 2 | pointer to loop descriptors in RAM, base 0xB6F4, 10 bytes each |
| `0x2A` | 1 | zero |
| `0x2B` | 1 | loop direction, ASCII `N` normal / `R` reverse |
| `0x2C` | 10 | zero |
| `0x36` | 3 | **sample address in sample RAM** |
| `0x39` | 3 | zero |

Records consumed from the loop-descriptor table, with `f` = whole 128 KB pages
of sample data: one-shot `2 + f`, looping `3 + f`, alternating `3 * (1 + f)` —
1002 of 1011 consecutive spacings.
The first sample on a disk sits at `0x18000` and each one after it follows at
`ceil(2 * words / 16) * 16` — exact for **1011 of 1011** consecutive pairs. So
samples tile nose to tail in memory on a 16-byte grid, at two bytes per word:
the 12-bit packing is a disk format only, expanded to 16-bit words on load.

The tuning field checks out musically: on DSKA0049 the samples `CHOP G1`,
`CHOP C2`, `CHOP F2`, `CHOP A2` carry 43, 48, 53, 57 — the exact semitone
intervals their names imply, with C3 = 60.

### Sample payload — 12-bit signed, split packing

Total payload is `1.5 * N` bytes for `N` words. The packing splits the sample
in half and stores the low nibbles of both halves interleaved up front:

```
first N bytes, as N/2 pairs; for pair i:
    byte[2i]   high nibble = low 4 bits of word i
    byte[2i]   low  nibble = low 4 bits of word (N/2 + i)
    byte[2i+1]             = high 8 bits of word i

then N/2 bytes:
    byte[N + i]            = high 8 bits of word (N/2 + i)

word = (high8 << 4) | low4, signed 12-bit (>= 2048 means subtract 4096)
```

Verified by decoding: amplitudes fall inside the 12-bit range and the
sample-to-sample delta drops by more than an order of magnitude versus any
other interpretation. Reading the payload as 16-bit little-endian is wrong.

### Program file — 38-byte header + 70-byte keygroups

Not covered by the reference document; derived from 390 programs and 1908
keygroups. Every one of the 391 programs across both sticks satisfies:

```
file length == 38 + 70 * keygroup_count
```

**Program header** (38 bytes)

| Offset | Field | Confidence |
|---|---|---|
| `0..9` | program name | confirmed |
| `10..15` | name padding: space (S900) / 0 (S950) | corpus |
| `16`, `17` | KEY to loudness, signed 16-bit | **panel** |
| `18`, `19` | load address, 16-bit LE | corpus |
| `20`, `24`, `25`, `28..37` | reserved, constant 0 | corpus |
| `21` | positional crossfade: 0 off, 255 on | **panel** |
| `22` | format marker: 255 = S900, 0 = S950 | corpus |
| `23` | keygroup count | confirmed (390/390) |
| `26` | MIDI program number, zero-based (panel shows +1) | **panel** |
| `27` | unknown (255 in 388 of 390) | - |

Taking a disk's programs in directory order, the load address advances by
`70 * (1 + keygroup_count)` in **289 of 289** consecutive pairs. So programs sit
nose to tail in the sampler's RAM, and the header occupies 70 bytes in memory
though only 38 on disk — the 38-byte on-disk header is a truncated 70-byte record.
Each 70-byte keygroup is a 24-byte header, two 22-byte velocity zones, and a link.
Fields marked *panel* were read off an S950 front panel and match exactly.

**Header**

| Offset | Field | Confidence |
|---|---|---|
| `0` / `1` | high key / low key | confirmed |
| `2` | velocity switch threshold (128 = never) | panel |
| `3..6` | VCA attack / decay / sustain / release | **panel** |
| `7` | VEL SENS to filter | **panel** |
| `8` | KEY to filter | **panel** |
| `9` | VEL SENS to attack | **panel** |
| `10` | VEL SENS to release (signed -50..50) | **panel** |
| `11` | VEL SENS to loudness | **panel** |
| `12` / `13` / `14` | WARP velocity / attack offset / time | **panel** |
| `15` / `16` / `17` | LFO delay / rate / depth | **panel** |
| `18` | flags: bit 0 constant pitch, bit 2 LFO desync, bit 3 one-shot playback | **panel** |
| `19` | output: panel value = byte + 1 (255 wraps to 0). 0 all, 1-8 mono outs, 9 left, 10 right | **panel** |
| `20` | reserved (set in 1 keygroup of 1908) | corpus |
| `21` | LFO depth from aftertouch (0 throughout the library) | **panel** |
| `22` | LFO depth from modwheel | **panel** |
| `23` | VCF envelope amount (signed -50..50) | **panel** |
| `68..69` | next keygroup, 0 = last | confirmed |

**Velocity zone** (add 24 for zone 1, 46 for zone 2)

| Offset | Field | Confidence |
|---|---|---|
| `+0..9` | sample name | confirmed |
| `+10..13` | zone 1 only: filter envelope A/D/S/R | **panel** |
| `+14`, `+15` | reserved, never written | corpus |
| `+16`, `+17` | internal sample reference | confirmed |
| `+18` | fine tune, unsigned 0..255, a fraction of a semitone upward | **panel** |
| `+19` | transpose, signed whole semitones | **panel** |
| `+20` | filter cutoff | **panel** |
| `+21` | loudness (signed) | **panel** |

The VCF envelope is blank (ASCII spaces) on 87% of disks: the S900 had no filter
envelope, so only S950-era programs write those bytes.

An unused second zone carries the placeholder name `2 SAMPLE`.

Full evidence and what remains unknown are in `S950-Disk-Format.pdf`.

## Files

| File | Contents |
|---|---|
| `Hfe.cs` | `.hfe` parser and MFM decoder: sync detection, ID/data address marks, CRC-16 verification |
| `AkaiDisk.cs` | directory, FAT chains, file reads, sample headers, keygroups |
| `Program.cs` | command line |
