# Myo research

Reverse engineering notes, model data and experiments for the Thalmic Myo armband and
Myo Connect. The main targets are Myo Connect for Windows (September 2015) and armband
firmware 1.5.1970, hardware revision 2 (Rev D).

This repository records how the original software works, including limitations and unsuccessful
approaches. It is a research archive; the maintained implementations live in separate projects.
There is no CI workflow or release process.

## Start here

| File or directory | Purpose |
|---|---|
| [myo_research.md](myo_research.md) | Main report: BLE services, commands and streams, firmware internals, BLED112 transport, USB updates, IMU controls and custom classifier training |
| [builtin_classifier/builtin_classifier.md](builtin_classifier/builtin_classifier.md) | Factory classifier reimplementation specification, feature processing, network, history bug, pose filtering and reference vectors |
| [builtin_classifier/builtin_classifier_1.5.1970.json](builtin_classifier/builtin_classifier_1.5.1970.json) | Extracted factory network weights, biases, normalization tables and constants with firmware addresses |
| [fw_dump/README.md](fw_dump/README.md) | Investigation of reading flash through BLE OTA checksum and scan commands; capabilities, ambiguity and practical limits |
| [fw_dump/experiments](fw_dump/experiments/) | Standalone .NET 8 console experiments reproducing the flash-readout calculations |
| [fw_dump/library](fw_dump/library/) | Archived MyoSharp memory-reading and flash-dumping sources, including demo UI fragments; reference copies, not a standalone library |
| [Myo Connect.exe.i64](Myo%20Connect.exe.i64) | Compressed IDA database for the analysed Windows executable, with research-assigned function names |

## What the research covers

- Direct BLE communication without Myo Connect: fixed GATT handles, UUIDs, commands,
  six-byte classifier events, high-rate EMG packing, RMS and mean power frequency streams.
- Custom profiles: PC-side training, the 7696-byte model format and firmware inference.
- Factory recognition: the 80-256-128-64-5 neural network and its stateful pose filtering.
- IMU processing and the original application's gesture, pointer and volume controls.
- Firmware updates and recovery, including the separate Kinetis application and nRF51 relay.
- Flash-readout experiments: Fletcher16 reveals byte values modulo 255, so 0x00 and 0xFF
  cannot be distinguished through checksums alone. Scan adds limited information; this is
  not a general, exact memory-dump interface.

## Running the offline experiments

Install the .NET 8 SDK. From the repository root:

```sh
dotnet run -c Release --project fw_dump/experiments -- ../MyoFirmware/research/binaries/kinetis_1.5.1970_at_0x10000.bin
```

Supply the path to a decoded application binary loaded at 0x10000, not a `.hex` S-record file.
The experiments run locally against that file and do not connect to or modify an armband.
The more expensive overflow experiment can take time; use Release configuration.
In the original workspace, where the sibling firmware folder is named `Firmware`, omitting
the argument finds `../Firmware/research/binaries/kinetis_1.5.1970_at_0x10000.bin` by walking
up from the executable directory. An explicit argument works with any checkout layout.

The archived files in `fw_dump/library` require the corresponding MyoSharp APIs and WPF demo
context. See the flash-readout notes before attempting to reuse them. They are intentionally
not included in the experiments project.

## Reading the notes and opening IDA

Addresses are firmware addresses or executable virtual addresses, as stated by each document;
they are not generally file offsets. Firmware application binaries load at 0x10000, the extracted
bootloader at 0, and the nRF51 relay at 0x14000.

The reports distinguish code-confirmed findings (`[C]`) from inferences (`[I]`). They were written
in stages: historical statements about untested behavior, temporary scratch paths, old workspace
paths such as `Firmware/bin`, and references to tools not included here can remain in those notes.
These are provenance, not current setup instructions. Use this README for the current layout.
Matching offline test vectors establishes consistency with the port, not complete equivalence
to a physical device in every state.

Open `Myo Connect.exe.i64` in a compatible IDA installation. If IDA asks for the input executable,
select your own matching `Myo Connect.exe`. The executable, installer, extracted application
resources and firmware IDA databases are not included here. The database may retain original
workspace paths; the database's annotations do not require those paths to exist.

## Related projects and source material

- [MyoFirmware](https://github.com/darksimpson/MyoFirmware): original and patched firmware,
  patcher sources, decoded binaries and firmware IDA databases.
- MyoSharp: the C# library and Windows demo developed from this research.
- MyoEsp32: the ESP32 implementation in the original workspace.
- The public `myo-bluetooth/myohw.h` header and the pymyo, MyoBridge and
  MyoIMUGestureController projects were reference inputs; they are not vendored here.

The JSON model data and IDA database derive from third-party software. Their inclusion does not
assert a redistribution license for Thalmic's firmware, model weights or application. No source
license has been selected for this archive yet.
