# Reading Myo flash over the air

Research notes on dumping a Myo armband's flash through Bluetooth alone, with no debugger. It works,
it is exact for the part that matters most, and it is too slow and too incomplete to be worth carrying
in the library, which is why the code was taken back out of MyoSharp and parked here.

Firmware studied: 1.5.1970 (Kinetis application image, hardware revision 2, Rev D).

## Why this exists

A Myo holds one thing that exists on no other device and that no command can write back: the factory
calibration record at flash 0xE000, with the gyroscope and accelerometer biases Thalmic measured for
that unit. Everything else can be restored from files. The question was whether that record, the user
settings, the trained classifier slots and the firmware itself can be read out over BLE.

## The read channel

The firmware update commands double as a read channel. Two of them never write anything:

| Command | Arguments | Answer, in the firmware info characteristic |
|---|---|---|
| Checksum, 0x17 | offset u32, length u32 | Fletcher16 over that range |
| Scan, 0x18 | offset u32, maximum length u32 | number of leading 8-byte blocks that are not all 0xFF |

The address is computed as `0x80000 + offset` in 32-bit arithmetic, **and neither argument is
bounds-checked**, so the wraparound puts every address in the chip within reach: offset 0xFFF80000
lands on 0x0, the bank the armband is running from. Offset 0x1FF70000 reaches SRAM, and 0x3FF80000
reaches the peripheral registers, which is a good reason to refuse that range (reading some registers
clears flags and empties FIFOs underneath the running firmware).

Both commands need the update state machine in a ready state, so a read session is an update session:
Prepare, read, Abort. Prepare erases the spare bank, which is scratch space the armband does not run
from, and copies 0x0..0xEFFF into it. Nothing is ever committed.

## Two bytes per request

Seeded with 0xFFFF, after n bytes the accumulators hold `sum1 = 255 + Sum(b)` and
`sum2 = 255 + Sum((n - i) * b)`, each folded at the end into 1..255 where 255 stands for zero. So
everything is arithmetic modulo 255. Over two bytes, with 255 and 765 both congruent to zero:

```
s1 = b0 + b1        (mod 255)
s2 = 2*b0 + b1      (mod 255)
```

Two equations, two unknowns:

```
b0 = s2 - s1        (mod 255)
b1 = 2*s1 - s2      (mod 255)
```

Two bytes out of every request, and that is the ceiling: there are only ever two sums, so three
unknowns would be underdetermined. Experiment 1 inverts all 65536 pairs with no errors.

## The blind spot, and that it is absolute

Modulo 255, `0xFF` and `0x00` are the same value. No checksum, at any length or offset, can separate
them.

The overflow fold looks like a way out, because it is non-linear and makes a long range depend on the
magnitudes rather than only the residues. It is not. The fold is

```
Fold(v) = ((v>>16) + (v&0xFFFF)) >> 8  +  (((v>>16)&0xFF) + (v&0xFF)) & 0xFF
```

Writing v as bytes B3..B0, the first term is `B3 + B1 + carry` and the second is
`B2 + B0 - 256*carry`, so

```
Fold(v) = B3 + B2 + B1 + B0 - 255*carry
```

Since 2^8 is congruent to 1 modulo 255, the sum of a value's bytes is congruent to the value, and the
carry term is a multiple of 255. **Fold(v) is congruent to v modulo 255**: it moves the magnitude of
the running sums around and never changes the result. The whole Fletcher16, folds and all, depends on
the data modulo 255 alone, so flipping a byte between 0x00 and 0xFF cannot change the answer at any
range length.

Experiment 4 confirms it three ways: the congruence holds for two million random values, a flip leaves
Fletcher16 untouched over 20000-byte ranges that fold three times each, and 3000 real positions stay
indistinguishable at ranges up to 64 KiB although the first fold lands after about 6400 bytes. For
contrast, Fletcher32 works modulo 65535 and does notice the same flip every time, which is what makes
an image's own checksum records able to prove a dump correct.

Do not revisit this. The long-range escape hatch does not exist.

## What settles those bytes instead

**Scan**, anywhere. It compares against 0xFF byte by byte, so it is exact, and with a maximum length
of 8 it answers "are these eight bytes all 0xFF". It only informs when the other seven bytes are
already known to be 0xFF, so its reach is erased runs and their edges. The window may start at any
address, which in principle settles the unaligned edges of a run and the single byte just past each
end. Experiment 3 measures what that is worth on the real image: 24580 undecided with aligned blocks
only, 24556 with every placement and edge growth. Twenty-four bytes for nine times the requests, so
the shipped code did not bother.

**A compared write**, below 0xF000 only. Prepare mirrors that area into the spare bank, and the Write
handler compares instead of programming when the target 16-byte block is not blank, reporting a
mismatch as error 3. Offering a candidate value is therefore an exact equality test, which settles
every undecided byte in the mirrored area: the bootloader, classifier slot 0, the user settings and
the factory calibration. A mismatch leaves the session failed and is recovered with Abort then
Continue, which needs no new erase. Preconditions: the session must have begun with a fresh Prepare,
the mirror must still match (checked), and the block must hold a byte known to be neither 0x00 nor
0xFF, because otherwise the staged block might be blank and the firmware would program the candidate
rather than compare it, confirming nothing.

**A reference image**, for the rest. Consulted only at addresses the file defines and only when the
reference byte is itself 0x00 or 0xFF. For an application dump the result is then written as
S-records and loaded back through the image validator, which runs the bootloader's own checks
including the image's Fletcher32 records, so the dump either proves itself safe to flash or says why
not.

## What came out of it

| Area | Address | Exactness |
|---|---|---|
| Bootloader | 0x0..0x9FFF | exact, compared writes |
| Classifier slot 0 | 0xA000..0xBFFF | exact, compared writes |
| User settings | 0xC000..0xCFFF | exact, compared writes |
| Factory calibration | 0xE000..0xEFFF | exact, compared writes |
| Application firmware | 0x10000..0x75FFF | about 24500 undecided bytes, needs a reference image, then provable |
| Classifier slots 1 to 4 | 0x76000..0x7DFFF | undecided bytes cannot be settled at all |

Measured on the stock application image (experiment 2): 25628 bytes are literally 0x00 or 0xFF, 5.9%
of the image, and 3176 of them are really 0xFF, so guessing 0x00 everywhere is wrong about three
thousand times.

Three ordinary GATT reads also expose flash exactly, with no session at all, and make good
cross-checks: the version characteristic (8 bytes at 0x10188), the device name (32 bytes at 0xC008)
and firmware info (the Bluetooth address, plus the active classifier type and index at 0xC028 and
0xC029).

## Why it was taken out of the library

* **Too slow to be a feature.** A command write plus an info read per two bytes, and the info
  characteristic cannot notify, so the read-back cannot be avoided. About a hundred bytes a second:
  under a minute for the factory page, a minute or two per classifier slot, seven minutes for the
  bootloader, several hours for the application.
* **Incomplete where it would matter.** Classifier slots 1 to 4 are outside the mirror, have no
  internal checksum and no reference file to borrow from, so a slot dump can only report how many
  bytes it could not settle.
* **Reading is not restoring.** The factory calibration is the reason to dump at all, and nothing can
  write it back over BLE. The backup needs an SWD debugger to be of use. Classifier slots can be
  restored with the normal upload command and the application over USB or BLE, so for those a dump is
  only a convenience.
* **It commandeers the update machinery** for hours: an erase cycle per session, no streaming while
  open, and it cannot run while an update is staged.

What stayed in the library is the one practical finding: IMU calibration (command 0x05) overwrites the
factory record on both the pass and the fail path, clearing the biases when the self test fails, and
nothing writes them back. The warning is on the method and on the demo button.

## Other paths checked and rejected

Every command handler from 0x01 to 0x19 was read. None returns memory; the outputs are vibration,
flash writes, log text and the OTA status fields. Commands 0x0D and 0x0F fall through to the default
case, so there is no hidden read command.

The log subsystem has about forty format strings, all taking scalars from fixed locations. There is no
host-controlled pointer and no hex-dump helper, so it cannot be aimed at memory. The only lines that
print device-specific stored data are the IMU bias ones, inside the routine that has just overwritten
them.

Factory test mode needs the PTA12 strap and writes to the UART console, not over BLE.

The Commit verification path looked promising and is worth recording as a near miss. It reads a record
table pointer from the staging bank, which can be written, and computes
`Fletcher32(record.offset + 0x80000, size)` with the same wraparound, so it can be aimed at live flash
and Fletcher32 does see the difference between 0x00 and 0xFF. It fails as an oracle because the only
observable is state 5 against state 6, which is one bit; a correct guess carries on into the bank
swap; and a wrong guess poisons the record chain permanently, since flash cannot be rewritten and the
pointer at 0x101FC is write-once, so each probe would need a fresh Prepare.

Three commands write the factory page, which is worth knowing before experimenting: 0x05 as described
above, and the factory commands 0x06 (nine bytes of LED calibration at record offset 24) and 0x12 (one
byte at offset 34). MyoSharp does not expose the latter two.

Factory record layout, confirmed from `fw_factory_settings_save` at 0x13FA4 (RAM copy 0x1FFFE3CC,
40 bytes written to flash 0xE000):

| Offset | Size | Field |
|---|---|---|
| 0 | 12 | gyroscope bias, three int32 |
| 12 | 12 | accelerometer bias, three int32 |
| 24 | 9 | LED calibration, written by command 0x06 |
| 33 | 1 | EMG self-check flag, 0xFF when never verified |
| 34 | 1 | written by command 0x12 |

## What is in here

```
library/      the code as it was removed from MyoSharp
  MyoMemoryReader.cs    session handling, the read primitives, the resolution passes
  MyoFlashLayout.cs     addresses, classifier slot map, factory record offsets
  MyoFlashDumper.cs     per-region dumps, reference pass, report
  SRecordWriter.cs      flashable S-record output
  MainWindow.Dump.cs    the demo tab's code behind
  DumpTab.xaml.txt      the demo tab's markup and its two tooltip strings
experiments/  a self-contained project that reproduces every number quoted above
```

The library sources are reference copies, not a buildable project: they were written against MyoSharp
and use `MyoDevice`, `MyoCommands`, `FirmwareInfo`, `FirmwareChecksums` and `MyoFirmwareImage`. To put
them back, take them from git history in `Dev/MyoSharp`:

```
git show 4d7fd6d   # the dump feature as added
git show 0c096a4   # the fold finding
git show 57c278d   # the calibration warning and the factory record decode
```

The experiments need only the raw application image and .NET 8:

```
cd experiments
dotnet run -c Release
```

## Status

Everything above is reasoned from the firmware and verified offline. **None of the dumping code was
ever run against an armband.** Unproven in particular: the compared-write path, the reading of Scan's
return field as a block count, and whether a session survives hours of uninterrupted commands.
