# Thalmic Myo armband: protocol and algorithms (reverse engineering report)

Scope: everything needed to talk to a Myo directly over BLE (without Myo Connect or the SDK), from
C# on Windows and from an ESP32, plus how the original software uses the IMU and trains custom
classifiers.

Sources analysed:

* `MyoConnect/Myo Connect.exe` (Sep 2015, Qt5, statically linked libmyo + libble/BGAPI), IDA instance.
* Myo firmware 1.5.1970 (`Firmware/myo-firmware-1.5.1970.hex`, Motorola S-records): Kinetis K2x
  application and the embedded nRF51822 GATT relay application (extracted to `Firmware/bin`).
* Public `Dev/myo-bluetooth/myohw.h`, `Dev/pymyo`, `Dev/MyoBridge`, `Dev/MyoIMUGestureController`.
* Embedded QML and Lua extracted to `MyoConnect/extracted` (plain) and `MyoConnect/extracted/zlib` (compressed resources).

Nothing here was verified against a live armband yet. Claims are tagged **[C]** (confirmed in code) or
**[I]** (inferred). Addresses refer to the IDA databases next to the binaries (function names prefixed
`Ble_`, `Usb_`, `Imu_`, `Train_`, `fw_` were assigned during this work).

## Key findings

1. **Seven command ids missing from `myohw.h`:** set_mode_ext (0x02, EMG/IMU rates and FV window),
   imu_calibrate (0x05), set_log_mode (0x08), set_active_classifier (0x0C), classifier upload
   (0x0D/0x0E), ping (0x10) and trim (0x11), plus the factory commands 0x06 and 0x12 and the OTA
   commands 0x13..0x19. Of these, imu_calibrate is never sent by Myo Connect (section 2.5), and ping
   and trim are used only by the bundled data-collection flow, where ping serves as an in-stream
   marker rather than a latency probe (Part C 3.5).
2. **Classifier events are 6 bytes.** arm_synced carries warmup state and rotation (mrad), and event 0x07
   is warmup_completed.
3. **Two streams missing from `myohw.h`:** a feature-vector characteristic (0x0104, handle 39: 8-channel
   EMG RMS at 50 Hz) and MPF (mean power frequency). EMG can run at up to 1000 Hz, with a special 20-byte
   packing above 500 Hz.
4. **ATT handles are fixed.** The Myo's GATT server is a small nRF51 relay with a fixed table, and Myo
   Connect never does GATT discovery.
5. **Custom classifier training is done on the PC.** It uses vector quantisation (spherical k-means,
   20 codewords over RMS vectors) plus per-pose histograms, packed into a 7696-byte blob that is uploaded
   over BLE. The firmware classifies with a leaky log-probability accumulator. The model is fully
   documented and can be reimplemented.
6. **USB:** the PC talks BLE through a Bluegiga BLED112 dongle (BGAPI over a virtual COM port). The Myo's
   own USB port is only a mass-storage drive used for firmware updates (copy the `.hex` file, wait for
   `SUCCESS.TXT`) and for `INFO.TXT`. There is no USB data protocol.
7. **IMU controls:** roll/pitch/yaw formulas, the fist-twist and menu-pull detectors, the gyro-based mouse
   (sigmoid gain), and the Lua volume knob are all documented, with pseudocode for joystick, knob and
   mouse.
8. **Power:** the Myo sleeps after 30 s of inactivity even while connected, so clients must send
   `09 01 01` (never sleep).

## Contents

* Part A. BLE protocol core (GATT, handles, commands, events, streams)
* Part B. Firmware internals (command semantics, EMG/FV/MPF processing, classifier runtime, OTA, sleep)
* Part C. Link layer and USB (BLED112/BGAPI, scanning, pairing, connection, firmware update)
* Part D. IMU usage (orientation math, gesture detectors, mouse, Lua API, recommendations)
* Part E. Custom classifier training (flow, data, algorithm, blob, upload)
* Part F. Open questions and next steps


---

# Part A. BLE protocol core


Sources: Myo Connect 2015 (`Myo Connect.exe`, statically linked libmyo + libble), Myo firmware 1.5.1970
(Kinetis application + embedded nRF51822 GATT relay), public `myohw.h`, pymyo, MyoBridge.
Addresses are IDA addresses: `Myo Connect.exe` (image base 0x400000), Kinetis image (base 0x10000),
nRF51 image (base 0x14000).

Legend: **[C]** confirmed from code, **[I]** inferred, **[?]** unknown / to verify on hardware.

All multi-byte fields are little-endian (despite the "big-endian" remark in `myohw.h`, which is wrong).

## 1. Hardware architecture

| Part | Role |
|---|---|
| Freescale Kinetis K2x (Cortex-M4) | Main MCU. Runs all logic: EMG acquisition, feature vectors, classifier, IMU (InvenSense MPU + DMP), vibration motor, LEDs, flash, USB (mass storage + bootloader). Parses all BLE commands. |
| Nordic nRF51822 (Cortex-M0, S110 SoftDevice) | BLE radio. Runs a thin GATT relay app (14148 bytes, linked at 0x14000). Programmed by the Kinetis over SWD at boot/update (Kinetis 0x1E844). |
| Kinetis <-> nRF link | UART, SLIP framing (0xC0 end, 0xDB escape, 0xDC/0xDD) [C nRF 0x14538]. |

The nRF app only holds the GATT table and forwards writes to the Kinetis; the Kinetis tells the nRF
what to notify/indicate. Consequently the **ATT handle numbers are fixed** for a given nRF app
version and Myo Connect uses them hard-coded (no GATT discovery). A robust client should still discover
by UUID (MyoBridge does) and fall back to the fixed handles.

## 2. UUIDs

Vendor base UUID: `d506xxxx-a904-deb9-4748-2c7f4a124842` (16-bit short id in place of `xxxx`).

## 3. GATT table and fixed handles [C]

Handle values below are ATT handles as used by Myo Connect (`libble_attribute_write` 0x50EE50,
incoming dispatcher 0x503050) and by the nRF relay (0x146F4).

| Service | Characteristic (short UUID) | Value handle | CCCD | Props | Len | Content |
|---|---|---|---|---|---|---|
| GAP 0x1800 | Device Name 0x2A00 | 3 | - | read/write | <=22 | Name; writing it renames the Myo (persisted by Kinetis) |
| Battery 0x180F | Battery Level 0x2A19 | 17 | 18 | read/notify | 1 | Percent (initial 100) |
| Control 0x0001 | Info 0x0101 | 21 | - | read | 20 | `myohw_fw_info_t` |
| Control 0x0001 | Firmware version 0x0201 | 23 | - | read | 8 | `myohw_fw_version_t` |
| Control 0x0001 | Command 0x0401 | 25 | - | write | <=20 | Commands, section 5 |
| IMU 0x0002 | IMU data 0x0402 | 28 | 29 | notify or indicate | 20 | Section 6.2 |
| IMU 0x0002 | Motion event 0x0502 | 31 | 32 | indicate | 3 | Section 6.3 |
| Classifier 0x0003 | Classifier event 0x0103 | 35 | 36 | indicate | **6** | Section 6.4 |
| FV 0x0004 | Feature vector 0x0104 | 39 | 40 | notify or indicate | 17 | Section 6.5 (EMG RMS) |
| EMG 0x0005 | EMG data 0 0x0105 | 43 | 44 | notify or indicate | 16 or 20 | Section 6.6 |
| EMG 0x0005 | EMG data 1 0x0205 | 46 | 47 | notify or indicate | 16 or 20 | |
| EMG 0x0005 | EMG data 2 0x0305 | 49 | 50 | notify or indicate | 16 or 20 | |
| EMG 0x0005 | EMG data 3 0x0405 | 52 | 53 | notify or indicate | 16 or 20 | |
| Log 0x0006 | Log 0x0602 | 56 | 57 | indicate | 20 | Section 6.7 (text) |
| Device Information 0x180A | Manufacturer name | ? | - | read | | "Thalmic Labs" |

"notify or indicate": the characteristic is created with notify when `fw_info.stream_indicating == 0`
and indicate otherwise (nRF 0x1569E etc.). Myo Connect writes CCCD = 0x0001 (notify) or 0x0002
(indicate) accordingly (0x502690). Motion, classifier and log are always indicate (CCCD 0x0002).
Do not write 0x0003 (MyoBridge reports stream corruption).

MTU: never negotiated, default 23 (20-byte payloads). No bonding / no MITM
(nRF sec params: bond 0, mitm 0, io_caps NONE, key 7..16) [C nRF 0x146D0].

## 4. Advertising [C nRF 0x14DE4]

* Flags 0x06 (LE General Discoverable, BR/EDR not supported).
* Shortened local name (up to 8 chars).
* 128-bit service UUID list, AD type 0x06 (incomplete list; Myo Connect only parses 0x06): Control service `d5060001-a904-deb9-4748-2c7f4a124842`. Accept both 0x06 and 0x07 in new code.
* Advertising interval 800 x 0.625 ms = 500 ms, no timeout; TX power +4 dBm; appearance 960 (generic HID).

Recognise a Myo by the 128-bit UUID `d5060001-...` in the advertisement.

## 5. Commands (write to handle 25) [C app + firmware dispatcher 0x13480]

Frame: `[cmd u8][payload_size u8][payload...]`, total <= 20 bytes. Firmware reads the payload
starting at byte 2. Myo Connect sometimes writes more bytes than payload_size (zero padding); harmless.

| Cmd | Name | Payload | Notes |
|---|---|---|---|
| 0x01 | set_mode | emg u8, imu u8, classifier u8 | size 3 |
| 0x02 | set_mode_ext **(new)** | emg u8, imu u8, classifier u8, emg_rate u16 (Hz), fv_window u8, fv_shift u8, imu_rate u16 (Hz), imu_lpf u8 | size 10. Example: `02 0A 03 01 00 E8 03 64 14 32 00 00` (raw EMG @1000 Hz, IMU data @50 Hz). emg_rate clamped 10..1000 (ADC always samples at 1000 Hz). fv_window even, <=200 (clamped); fv_shift <= window. App always uses window 100, shift 20. imu_lpf = MPU low-pass override in Hz, 0 = keep default. Used by app when EMG mode is 2/3/4 or imu rate != 50. |
| 0x03 | vibrate | type u8 | 1 short 200 ms, 2 medium 700 ms, 3 long 1200 ms (strength 255) |
| 0x04 | deep_sleep | - | Myo powers off (power latch released [I]); wake by plugging USB |
| 0x05 | imu_calibrate **(new)** | - | MPU self-test, stores gyro/accel bias; result only as log line `N|IMU Cal Succeeded.` / `N|IMU Cal Failed.` (enable log mode first) |
| 0x06 | led_calibrate **(new, factory)** | 9 bytes | factory LED trim values for the two RGB LEDs; do not use |
| 0x07 | vibrate2 | 6 x (duration_ms u16, strength u8) | size 18 |
| 0x08 | set_log_mode **(new)** | mode u8 (+2 pad) | app writes `08 01 mm 00 00`; enables text log on handle 56 |
| 0x09 | set_sleep_mode | mode u8 | 0 normal, 1 never sleep |
| 0x0A | unlock | type u8 | 0 lock, 1 timed, 2 hold |
| 0x0B | user_action | type u8 | 0 single: vibrates 120 ms at strength 120 |
| 0x0C | set_active_classifier **(new)** | type u8, index u8 | type 0 builtin, 1 custom |
| 0x0D | classifier_data_chunk **(new)** | n u8 followed by n (<=18) bytes | written as full 20-byte frames: `0D len data[len] pad` |
| 0x0E | classifier_data_commit **(new)** | slot u8, total_len u16 | total_len must equal bytes buffered (7696). Slot 0 at flash 0xA000, slots 1..4 at 0x7E000 - n*0x2000. Does not activate. |
| 0x10 | ping **(new)** | u32 | firmware only logs it, `N|ping: <value>`; the host side uses that echo as an in-stream marker (Part C 3.5) |
| 0x11 | trim_event_queue **(new)** | u16 N | keep at most N queued indications (oldest dropped), logs "N|trim" |
| 0x12 | set_sku **(new, factory)** | u8 | writes SKU (fw_info byte 12); do not use |
| 0x13..0x19 | OTA **(new)** | various | PREPARE, CONTINUE, ABORT, COMMIT, CHECKSUM, SCAN, WRITE; see firmware section 8 |

Custom classifier upload sequence (Myo Connect 0x502310):
1. For offset 0..7695 step 18: write `[0x0D][n][18 data bytes, zero padded]` (20-byte write, n = min(18, remaining)).
2. Write `[0x0E][0x03][index][0x10][0x1E]` (total 0x1E10 = 7696).
3. Activate: `[0x0C][0x02][0x01][index]`.

### 5.1 Mode values

EMG mode (wire value = libmyo internal value, Qt name):

| Value | Qt name | Streams |
|---|---|---|
| 0 | None | nothing |
| 1 | Rms | FV (handle 39) only |
| 2 | Emg | filtered EMG on 43/46/49/52 |
| 3 | EmgRaw | raw EMG on 43/46/49/52 |
| 4 | (internal) | FV + all EMG |
| 5 | RmsMpf | FV (39) + MPF on EMG0 (43) |

IMU mode: 0 none, 1 data (28), 2 events (31), 3 all, 4 raw (per `myohw.h`).
Classifier mode: 0 disabled, 1 enabled (35). Note the firmware computes FV whenever the classifier is on.

## 6. Incoming data

### 6.1 Info (handle 21, 20 bytes) and version (handle 23, 8 bytes)

Info: serial[6] (= BT MAC, least significant byte first), unlock_pose u16 (always 5, double tap),
active_classifier_type u8, active_classifier_index u8, has_custom_classifier u8 (slot 0 only),
stream_indicating u8 (always 0 in 1.5.1970, so streams are notify), sku u8, OTA status bytes 13..17,
reserved. (Offsets 11 and 12 per firmware; `myohw.h` layout agrees.)

Power note [C fw]: the Myo sleeps after 30 s of inactivity even while connected and streaming.
Clients that want continuous data must send set_sleep_mode(1) (`09 01 01`).

Version: major u16, minor u16, patch u16, hardware_rev u16. Myo Connect refuses major != 1 or
minor < 5 ("The SDK requires firmware version 1.5.x") [C 0x503EE0].

### 6.2 IMU data (handle 28, 20 bytes)

`w, x, y, z` int16 / 16384 (unit quaternion) - order is **w first** [C 0x50D1A0];
accel x,y,z int16 / 2048 = g; gyro x,y,z int16 / 16 = deg/s. Default 50 Hz; rate settable via 0x02.

### 6.3 Motion event (handle 31, 3 bytes)

`type u8, a u8, b u8`. type 0 = tap (the only type): a = direction (1 X up, 2 X down, 3 Y up, 4 Y down, 5 Z up, 6 Z down), b = tap count.

### 6.4 Classifier event (handle 35, 6 bytes) [C 0x503690, nRF char len 6]

| type | Event | Bytes 1..5 |
|---|---|---|
| 1 | arm_synced | arm u8 (1 right, 2 left, 0xFF unknown), x_direction u8 (1 toward wrist, 2 toward elbow, 0xFF unknown; firmware 1.5.1970 always sends 2), warmup_state u8 (1 cold, 2 warm; 0 unknown never sent), rotation u16 (milliradians, 0..6283) [C fw] |
| 2 | arm_unsynced | - |
| 3 | pose | pose u16 (0 rest, 1 fist, 2 wave in, 3 wave out, 4 fingers spread, 5 double tap, 0xFFFF unknown) |
| 4 | unlocked | - |
| 5 | locked | - |
| 6 | sync_failed | result u8 (1 = too hard; the only value firmware 1.5.1970 sends) |
| 7 | warmup_completed | result u8 (1 success = >=6 channels warm, 2 failed = 5 min timeout) |

Older firmware sends 3-byte events; parse with a minimum length of 3 and read optional bytes.

### 6.5 Feature vector / RMS (handle 39, 17 bytes)

8 x u16 (per-channel EMG RMS over window 100 samples @1000 Hz ADC, shift 20 => 50 Hz) + 1 byte motion-artifact bitmask.
Delivered to apps as `featureVectorReceived(QList<int>, motionArtifact)`. Rate = 1000 / fv_shift Hz, independent of emg_rate. Details in Part B 3.3.

### 6.6 EMG (handles 43, 46, 49, 52)

* Normal (EMG rate <= 500 Hz; default 200 Hz): 16 bytes = 2 samples x 8 channels int8. Each
  characteristic carries consecutive samples; order of arrival across the 4 characteristics defines time order.
* High rate (EMG rate > 500 Hz via cmd 0x02) [C 0x503DA0/0x503BA0]: 20-byte packets. Characteristic pairs
  (43,46) and (49,52) together carry 40 bytes = 5 samples x 8 channels:
  packet A (43 or 49): sample0 = bytes 0..7, sample1 = 8..15, bytes 16..19 = first half of sample2;
  packet B (46 or 52): sample2 = A[16..19] + B[0..3], sample3 = B[4..11], sample4 = B[12..19].
* Mode 5 (RmsMpf): handle 43 carries 16 bytes MPF (8 values, mean power frequency) instead of EMG
  (`mpfReceived(QList<float>)`): 8 x u16 = mean power frequency in Hz x 128 [C fw].

### 6.7 Log (handle 56, 20 bytes)

ASCII text `L|message` (L = E error, W warning, N notice; I/D not generated), split into
indications of 19 text bytes + 1 flag byte (1 = last chunk). Enabled by cmd 0x08 (mode 1).
Myo Connect watches for `N|IMU Cal Succeeded` / `N|IMU Cal Failed.` (IMU calibration result, in the
log handler 0x5042C0 reached from the event router 0x503050, which raises the result and then turns
log mode off again), and the bundled data-collection flow watches `N|ping: `, `N|trim: ` and
`N|drain: ` (Part C 3.5). Note that nothing in the application ever sends 0x05, so the calibration
watcher has no producer here; see section 2.5. Details in Part B 6.

## 7. Connection sequence used by Myo Connect [C 0x503050, 0x504200, 0x5024F0]

1. Connect (BGAPI connect_direct, interval 7.5 ms, latency 0, timeout 640 ms), no pairing. New Myos are only accepted when RSSI >= -39 dBm (held next to the dongle).
2. Read handle 23 (firmware version, check 1.x with x >= 5, else "rejected" and disconnect), 21 (info; take stream_indicating), 3 (name; marks Myo as paired).
3. Enable battery CCCD (18).
4. Write CCCDs according to modes, then send set_mode (0x01) or set_mode_ext (0x02) on handle 25.
5. CCCDs: EMG mode 1 -> 40; 2,3 -> 44,47,50,53; 4 -> all of 40,44..53; 5 -> 40,44.
   IMU 1 or 3 -> 29 (notify/indicate), 2 or 3 -> 32 (indicate). Classifier -> 36. Log -> 57.
6. If log mode requested: `08 01 01 00 00`.

---

# Part B. Firmware internals


Source: IDA instance "sdxr" (Kinetis Cortex-M4 app, base 0x10000), cross-checked against
the nRF51822 relay app (instance "szv9", base 0x14000). Addresses are Kinetis addresses unless
marked "nRF". Functions renamed in the IDB with prefix `fw_` (listed per section).

Legend: [C] = confirmed directly from code/data, [I] = inferred (plausible, not fully traced).

All multi-byte values are little endian. "tick" = 10 ms (global counter at 0x2000B974,
incremented by the PIT ISR 0x165F0; every ms value in the code is divided by 10) [C].

---------------------------------------------------------------------------------------------

## 1. Architecture overview

- Main loop `fw_main` (0x15C30): init (LEDs, flash driver, bootloader self-update check
  0x137EC, factory test detection 0x12B38, classifier init 0x12EAC, EMG self check 0x13C2C,
  battery 0x122AC, nRF link 0x12908, IMU 0x14C10, classifier on 0x13100, warmup timer 0x14614),
  then loops forever over: nRF message processing (0x1291C), factory test (0x12CA4),
  IMU (0x14D8C), EMG (0x13D84), battery (0x1238C), LEDs (0x15498), inactivity (0x16374),
  OTA prepare step (0x15DC0); `WFI` when nothing to do. [C]
- EMG ADC runs at a fixed 1000 Hz (8 channels + 1 battery channel per frame), independent of
  the requested EMG streaming rate. [I, strongly supported: all EMG/FV math assumes fs = 1000]
- IMU: InvenSense MPU-9150 class device (accel + gyro + AK compass) driven by the InvenSense
  eMPL "Motion Driver 5.1" + MPL library ("eMPL 5.1" string). DMP is loaded but only used for
  tap detection and the sensor FIFO; the quaternion comes from the MPL sensor-fusion library
  running on the Kinetis (inv_get_sensor_type_quat). [C for calls, I for library identification]
- BLE: the nRF51822 is a thin GATT relay. Kinetis talks to it over UART with SLIP framing
  (section 9).

### Persistent storage map (Kinetis flash) [C]

| Address | Size | Content |
|---|---|---|
| 0x00000 | 0xA000 | bootloader (not in update image) |
| 0x0A000 | 8 KB | custom classifier slot 0 (7696 bytes used) |
| 0x0C000 | 4 KB | user settings: +0 u32 write counter, +8 .. +47: 40-byte record (below) |
| 0x0E000 | 4 KB | factory settings: 40-byte record (below) |
| 0x0F000 | 4 KB | flash-swap indicator sector (never erased by OTA prepare) |
| 0x10000 | .. 0x753F8 | application code/data |
| 0x76000, 0x78000, 0x7A000, 0x7C000 | 8 KB each | custom classifier slots 4, 3, 2, 1 |
| 0x7E000 / 0x7F000 | 4 KB each | "syspage" A/B (bootloader update bookkeeping, 0x16544/0x16560) |
| 0x80000 .. 0xFFFFF | 512 KB | upper flash bank = OTA staging area (swapped on commit) |

User settings record (RAM copy 0x200081E0, flash 0xC008) [C]:

| Offset | Size | Field |
|---|---|---|
| 0 | 32 | device name (0xFF first byte = unset, default "Myo") |
| 32 | 1 | active classifier type (1 = custom, anything else = built-in; 0xFF unset) |
| 33 | 1 | active classifier index (slot) |
| 34 | 2 | (copied, unused) |
| 36 | 4 | (unused) |

Factory settings record (RAM 0x1FFFE3CC, flash 0xE000, saved by `fw_factory_settings_save` 0x13FA4) [C]:

| Offset | Size | Field |
|---|---|---|
| 0 | 12 | gyro bias, 3 x int32 (from IMU self-test, cmd 0x05) |
| 12 | 12 | accel bias, 3 x int32 (cmd 0x05) |
| 24 | 9 | LED calibration bytes (cmd 0x06) |
| 33 | 1 | EMG hardware check flag (0xFF = not verified; set to 0 by boot self-test 0x13C2C) |
| 34 | 1 | SKU (cmd 0x12); reported in fw_info byte 12 |
| 35 | 5 | unused |

---------------------------------------------------------------------------------------------

## 2. Command path and dispatcher

GATT writes to handle 25 arrive from the nRF as UART message type 6:
`[06][handle][len][data 0..19]` (23-byte frame). The frame is queued in a 16-entry ring
(0x1FFF0394, 23 bytes each) by the RX ISR `fw_uart_rx_byte_slip` (0x1251C) and later dispatched
by `fw_uart_process_msg` (0x1291C) to `fw_cmd_dispatch` (0x13480) with pointer to `data`
(so `a1[0]` = command, `a1[1]` = payload size, payload at `a1+2`). The payload size byte is
never validated for any command. Writes to other handles are ignored. [C]

Exception: command 0x0D is consumed directly in the RX ISR and never queued (section 2.13). [C]

Commands 0x0D, 0x0F and anything above 0x19 fall into the default case (ignored). [C]

None of the commands produce a direct response; observable effects are notifications/
indications, log lines (if log mode is on), and changes of the readable fw_info characteristic
(handle 21). [C]

### 2.1 0x01 set_mode (`fw_cmd_set_mode` 0x13430) [C]

`[01][03][emg_mode][imu_mode][classifier_mode]`

Expanded into a set_mode_ext frame with defaults and passed to 0x133B6:
emg_rate = 200 Hz, fv_window = 100, fv_shift = 20, imu_rate = 50 Hz, lpf override = 0.

### 2.2 0x02 set_mode_ext (`fw_cmd_set_mode_ext` 0x133B6) [C]

| Offset (from cmd byte) | Size | Field |
|---|---|---|
| 0 | 1 | 0x02 |
| 1 | 1 | size (host uses 0x0A) |
| 2 | 1 | emg_mode |
| 3 | 1 | imu_mode |
| 4 | 1 | classifier_mode (1 = enabled, anything else = disabled) |
| 5 | 2 | emg_rate, u16 Hz (clamped 10..1000) |
| 7 | 1 | FV window size in EMG samples (1 ms each); odd values truncated to even, max 200 |
| 8 | 1 | FV window shift in samples; clamped to <= window |
| 9 | 2 | imu_rate, u16 Hz (passed to mpu_set_sample_rate) |
| 11 | 1 | MPU low-pass filter override in Hz (0 = keep driver default) |

The host value 0x1464 at +7 therefore means window = 100, shift = 20 (FV every 20 ms = 50 Hz).

Pseudocode:
```
set_mode_ext(p):
  emg = p[2]
  fv_needed = (emg in {1,4,5}) or p[4] == 1
  fv_send   = (emg in {1,4,5})
  clear "streams suppressed after connect" flag        // 0x16508
  if p[4] == 1: classifier_enable()   // 0x13100: starts EMG(200Hz)/FV(100,20)/IMU(50Hz) if off,
                                      // (re)loads classifier model, state = unsynced
  else:         classifier_disable()  // 0x1323C: if synced, emits arm_unsynced and locks
  emg_configure(emg, rate)            // 0x13D54; mode 0 and classifier off: ADC stopped
  imu_configure(imu_mode, imu_rate, lpf)  // 0x14D4C; mode 0 and classifier off: IMU low power
  if fv_needed: fv_configure(send=fv_send, mpf=(emg==5), window, shift)
  else:         fv_disable()
  send_classifier_events()
```
Values of `emg_mode` / `imu_mode` outside the documented range are accepted (no error) but
produce no stream. After a BLE connect, IMU data, motion events and classifier events are
suppressed until the first set_mode/set_mode_ext (flag at 0x200081DC, set on connect, cleared
here). EMG and FV are not gated by this flag (they are not running until configured anyway).

### 2.3 0x03 vibrate [C]

`[03][01][type]`: 1 = 200 ms, 2 = 700 ms, 3 = 1200 ms, all at strength 255, each followed by a
100 ms entry at strength 0. Other values ignored. Vibrations go into a 128-entry FIFO
(`fw_vibrate_enqueue` 0x1A55C, entry = u16 duration in ticks + u8 PWM strength); commands
append, they do not cancel.

### 2.4 0x04 deep_sleep [C code, I meaning]

`[04][00]`: drives GPIO descriptor 0x6F138 (configured as output, value 0). No other action.
Most likely releases the power latch (device fully off until woken by USB/charger). [I]

### 2.5 0x05 IMU calibration (`fw_imu_calibrate` 0x151F4) [C]

`[05][00]`. Runs the MPU self test (mpu_run_self_test, result bitmask gyro=1, accel=2,
compass=4). Device should be still and flat. [I]
- Result 7 (all pass): stores gyro bias and accel bias (scaled by accel sensitivity) into
  factory settings +0/+12, saves factory settings to flash, applies biases to MPL, logs
  `N|IMU Cal Succeeded.`
- Otherwise: logs `N|IMU Cal Failed.`, overwrites both biases with 0xFF (erased), saves, logs
  `W|MPU self-test failed.`, enables MPL online gyro auto-calibration.
- Always logs `N|IMU Gyro bias: %li %li %li` and `N|IMU Accel bias: %li %li %li`.
- If the result is failure or the flash save failed: error LED pattern (0x31934) for 5000 ms.
The host sees the result only through the log characteristic (log mode must be 1), which is
what the PC app parses ("N|IMU Cal Succeeded" / "N|IMU Cal Failed.").

[C] Myo Connect never sends this command. All 27 call sites of `libble_attribute_write` were
enumerated: the application can issue 0x01, 0x02, 0x03, 0x04, 0x07, 0x08, 0x09, 0x0A, 0x0B, 0x0C,
0x0D, 0x0E, 0x10 and 0x11, plus the device name write and the CCCD writes, and nothing else. The QMyo
metaobject exposes no calibration method either (vibrate, rampVibrationWithDuration,
cancelRampVibration, unlock, lock, notifyUserAction, deepSleep, ping, trimEventQueue, macAddress,
macAddressAsString, firmwareVersionAsString, classifierType, setClassifier,
forceExpectCalibrationGesture, setClassifierEnabled, poseType, isUnlocked). Only the result parser
exists, in the log handler 0x5042C0. [I] The stored biases are therefore written by factory tooling,
which is also where the other two commands that rewrite this record would be used (0x06 and 0x12).

**Do not send this command** unless the factory record has been read out first: it saves the record on
both paths, so a failing self test erases the gyroscope and accelerometer biases Thalmic measured for
that unit, and no command writes them back. A later successful calibration produces usable biases
again, but not the original ones.

### 2.6 0x06 LED calibration (factory) [C code, I semantics]

`[06][09][b0..b8]`: the 9 bytes are copied to factory settings +24..+32 and saved to flash.
On success an LED test pattern (0x318EC / 0x318C4) is shown for 1000 ms, on save failure the
error pattern for 2000 ms. At boot and when the test pattern starts, 0x153E0 derives 6 bytes at
RAM 0x1FFF00E8..0x1FFF00ED:
```
if any(b0,b1,b2) != 0: A = (b0>>1, b1>>1, b2>>1) else A = (b6, b7, b8)
B = (b3, b4, b5)
```
A and B are per-channel trims for the two RGB LEDs (logo LED and status LED, RGB order
assumed). Defaults (from .data): A = (0x00, 0x64, 0x32), B = (0x00, 0x96, 0x32). The consumer
of these bytes was not traced. [I]

### 2.7 0x07 vibrate2 [C]

`[07][12][dur0 u16 ms][str0 u8] ... x6` (18 payload bytes). Each entry with dur != 0 is
enqueued (duration ms / 10 ticks, strength 0..255 PWM). Entries with duration 0 are skipped.

### 2.8 0x08 set_log_mode (`fw_cmd_set_log_mode` 0x156EC) [C]

`[08][01][mode]`: stores mode at 0x20007AC4. mode 1 = forward log lines to the log
characteristic (handle 56); any other value = off. See section 6.

### 2.9 0x09 set_sleep_mode (0x16450) [C]

`[09][01][mode]`: kicks the inactivity timer and stores mode: 1 = never sleep, anything else =
normal (sleep after 30 s of inactivity). Reset to 0 on BLE disconnect.

### 2.10 0x0A unlock (`fw_cmd_unlock` 0x131AC) [C]

`[0A][01][type]`: 0 = lock, 1 = timed unlock, 2 = hold unlock. Ignored while the arm is not
synced. See section 3.6.

### 2.11 0x0B user_action [C]

`[0B][01][type]`: type 0 (single) gives a 120 ms vibration at strength 120. Other types ignored.

### 2.12 0x0C set_active_classifier [C]

`[0C][02][type][index]`: writes type/index to user settings (+32/+33), saves the user
settings sector (0xC000, increments the write counter at +0), then reloads the classifier
(`0x13168`): type 1 selects the custom blob in slot `index` (if the slot is not blank),
anything else selects the built-in model. Reloading resets the sync state (the user must sync
again). fw_info (handle 21) is not refreshed immediately; it is rebuilt on the next link
(re)start or OTA command.

### 2.13 0x0D classifier_upload_chunk (handled in RX ISR 0x1251C, 0x125C8) [C]

Condition checked on the raw UART frame: `frame[0]==6 && frame[1]==25 && frame[3]==0x0D`.
```
len = frame[4]                         // payload size byte of the command
if loaded + len <= 7696:               // loaded = u32 at 0x2000B964
    memcpy(0x20009B54 + loaded, frame + 5, len)
    loaded += len
// else: chunk silently dropped
```
- Chunks are appended strictly in arrival order; there is no offset/sequence field.
- The frame is not queued (so it is not rate-limited by the 16-entry command queue).
- Buffer: RAM 0x20009B54, 7696 (0x1E10) bytes.
- `loaded` is only reset to 0 by a successful-size 0x0E (at the start of the flash commit).
  After a size mismatch the partial data stays and further chunks keep appending. There is no
  other reset (not on connect/disconnect).

### 2.14 0x0E classifier_upload_finish (0x135BA, `fw_classifier_commit_to_flash` 0x13674) [C]

`[0E][03][index][size u16]`
```
if size != loaded: log E "Custom classifier data not loaded (expected %d, loaded %d)"; return
loaded = 0
addr = (index == 0) ? 0xA000 : 0x7E000 - index*0x2000
if addr < 0x753F8: log E "Out of flash for classifier %d (at %p, code end %p)"; fail
erase 2 sectors (8 KB) at addr; write 7696 bytes from 0x20009B54
success: log N "Classifier written successfully"
failure: log W "Failed to write classifier to flash!"; log E "Unable to add custom classifier %d"
```
Slots: 0 = 0xA000, 1 = 0x7C000, 2 = 0x7A000, 3 = 0x78000, 4 = 0x76000; index >= 5 fails.
The write always copies 7696 bytes regardless of `size`. The committed slot is not activated
automatically (use 0x0C). fw_info.has_custom_classifier reflects slot 0 only.

### 2.15 0x10 ping [C]

`[10][04][u32 value]`: if log level > 1 logs `N|ping: %lu` with the value (visible only with log
mode 1). No other effect on the armband.

The log line is the whole point: it is pushed through the same indication queue as the classifier and
motion events, so it keeps its position in the stream and marks the moment the command arrived. That
is how the only known consumer uses it, to bracket a recorded gesture; see Part C 3.5.

### 2.16 0x11 trim_event_queue (`fw_cmd_trim_ind_queue` 0x12A9C) [C]

`[11][02][max u16]`: operates on the Kinetis indication queue (events waiting to be sent to the
nRF as indications: classifier events, motion events, log lines; 32 entries of 23 bytes at
0x1FFF054C, count at 0x1FFF0830).
```
dropped = 0
if count > max: dropped = count - max; drop the 'dropped' oldest entries; count = max
push to the FRONT of the queue an indication on handle 56:  "N|trim: <max>(-<dropped>)"
    (19 text bytes + last-chunk flag 1), sent regardless of log mode
```
The u16 is the maximum number of queued indications to keep. The one known consumer calls it with 0 to
clear the queue and with 500 on a repeating timer to hold a fixed pre-gesture padding; see Part C 3.5.

### 2.17 0x12 set_sku (factory) [C]

`[12][01][sku]`: stores the byte at factory settings +34, saves; error LED 5 s if the save fails.
Reported in fw_info byte 12 (0xFF reported as 0). SDK meaning: 0 unknown, 1 black, 2 white [I].

### 2.18 0x13..0x19 OTA (`fw_ota_command` 0x15E80) - see section 8.

---------------------------------------------------------------------------------------------

## 3. EMG, feature vector, MPF

Functions: `fw_emg_start` 0x13A80, `fw_emg_stop` 0x13BCC, `fw_emg_process` 0x13D84,
`fw_emg_filter` 0x15B28, `fw_fv_configure` 0x14094, `fw_fv_process` 0x1417C.

### 3.1 Acquisition and filtering [C]

- 1000 Hz frames of 9 x int16 in a 512-frame ring (0x1FFFBF98): channels 0..7 = EMG,
  word 8 = battery voltage ADC.
- EMG samples: `x = adc - 2048` (12-bit, mid-scale removed).
- Filtering (all modes except emg_mode 3): cascade of biquads applied per channel, in place,
  output rounded to int16:
  - `fw_emg_add_lowpass(1000, 200)`: 2nd-order Butterworth low-pass, fc = 200 Hz (prototype
    b = [0.1441, 0.28821, 0.1441], a = [1, -0.67749, 0.25391] at 0.5 rad, frequency-warped to
    fc). [C coefficients, I "Butterworth"]
  - `fw_emg_add_notch(1000, f)` for f = 60, 50, 120, 100, 180, 150, 240, 200, 300, 250 Hz:
    notch biquads (mains 50/60 Hz and harmonics). Max 12 sections.
- emg_mode 3 ("raw") skips both the filter setup and the filter; everything downstream (EMG
  stream and FV) then uses unfiltered samples.

### 3.2 EMG streaming (emg_mode 2, 3, 4) [C]

Decimation: `acc += emg_rate; if acc >= 1000 { acc -= 1000; emit }` (emg_rate clamped 10..1000,
default 200). Emitted sample per channel: `s = clamp((x + 8) >> 4, -128, 127)` (int8, i.e. ADC
counts / 16).

Packets rotate over handles 43, 46, 49, 52 (table at 0x3141B, the bytes "+.14") as
notifications (UART msg type 8):
- emg_rate <= 500 Hz: 16-byte packet = 2 samples x 8 channels (s0[0..7], s1[0..7]).
- emg_rate > 500 Hz: 20-byte packets; samples are packed continuously: after 3 samples (24 bytes)
  bytes 0..19 are sent, after 5 samples (40 bytes) bytes 20..39 are sent, then restart. So a pair
  of packets carries 5 samples; the second packet starts with the last 4 channels of sample 2.

### 3.3 Feature vector (RMS), MPF, motion-artifact byte (`fw_fv_process`) [C]

Per EMG frame (1000 Hz) each channel sample is stored as float into a window ring of W samples
(W = fv window) and, every second frame, into a 500-sample ring (1 s at 500 Hz).
Every `shift` samples once at least W samples exist:
```
for ch in 0..7:
    X = kiss_fftr(window[ch], W)                       // real FFT, W must be even
    P_j = Re(X_j)^2 + Im(X_j)^2, j = 0..W/2
    mpf[ch] = (sum_j j*P_j / sum_j P_j) * (1000 / W)   // mean power frequency, Hz
    rms[ch] = sqrt( sum(window[ch]^2) / W )            // ADC counts (after filter)
if 1 s ring full:
    for ch: mpf1s[ch] = sum_{j=0..250} j*P_j / sum P_j  over FFT of 500 samples (1 Hz bins)
    artifact bit ch = (rms > 800 and mpf1s < 50) or (rms > 1000 and mpf1s < 70)
```
FV packet, handle 39, 17 bytes (notify), sent when emg_mode in {1,4,5}:

| Offset | Size | Field |
|---|---|---|
| 0 | 16 | 8 x u16 rms[ch] = (unsigned) rms (truncated) |
| 16 | 1 | motion-artifact bitmask (bit ch set per rule above; 0 during the first second) |

MPF packet, handle 43, 16 bytes (notify), only in emg_mode 5: `8 x u16 = (unsigned)(mpf[ch]*128 + 0.5)`
(MPF in Hz, Q7 fixed point).

FV rate = 1000/shift Hz (default 50 Hz). A shift of 0 would divide by zero (not guarded).
The FV/rms floats, mpf floats and the artifact mask are passed to the classifier
(`0x1324C`) every FV frame when the classifier is enabled.

### 3.4 emg_mode summary [C]

| emg_mode | ADC | h39 FV (17 B) | h43 MPF (16 B) | h43/46/49/52 EMG | Filter |
|---|---|---|---|---|---|
| 0 | off unless classifier on | - | - | - | yes |
| 1 | on | yes | - | - | yes |
| 2 | on | - | - | yes | yes |
| 3 | on | - | - | yes | no (raw) |
| 4 | on | yes | - | yes | yes |
| 5 | on | yes | yes | - | yes |
| >= 6 | on | - | - | - | yes |

### 3.5 Warmup and cold-muscle compensation (in `fw_fv_process`) [C]

- Warmup timer/flags reset by 0x1461A (at boot and via classifier callback when the sync state
  machine restarts). Warm flag at 0x20003ABC.
- Each FV frame while not warm and the 1 s ring is full:
  - if more than 300000 ms (5 min) elapsed since reset: warm = 1, and if synced send
    warmup_completed with result 2 (timeout).
  - per channel: if 1 s RMS (sqrt(mean(x^2)) over the 500-sample ring) < 150 and mpf1s < 50 Hz,
    mark the channel as warm (sticky). When more than 5 channels (>= 6) are warm: warm = 1 and,
    if synced, send warmup_completed result 1 (success).
- While not warm, the custom classifier is active (user settings type == 1), and synced, the
  RMS values given to the classifier (not the transmitted FV) are boosted:
  `if mpf1s > 90 and rms <= 500: g = (mpf1s - 90)/55.555 + 0.1; if rms > 400: g *= (500-rms)/100; rms *= (1+g)`.
- `fw_send_warmup_completed` (0x14058) also sets the LED sync state to 2 (warm).

---------------------------------------------------------------------------------------------

## 4. Classifier events (handle 35, indications, 6 bytes)

Built by `fw_send_classifier_events` (0x12F4C), which compares the current state with the last
sent state and emits only changes. Suppressed while "streams suppressed after connect" is set.

| Type | Bytes | Meaning |
|---|---|---|
| 1 arm_synced | `01 arm xdir warmup rot_lo rot_hi` | sent when arm/xdir or rotation changes |
| 2 arm_unsynced | `02 00 00 00 00 00` | arm byte = 0xFF |
| 3 pose | `03 pose_lo pose_hi 00 00 00` | pose u16 |
| 4 unlocked | `04 00 00 00 00 00` | |
| 5 locked | `05 00 00 00 00 00` | |
| 6 sync_failed | `06 01 00 00 00 00` | only reason 1 exists in this firmware |
| 7 warmup_completed | `07 res 00 00 00 00` | res 1 = success, 2 = failed (timeout) |

### 4.1 arm_synced fields (`fw_on_arm_synced` 0x1306C) [C]

- arm: internal index mapped by table 0x2B727 `[0xFF, 1, 2]`: 1 = right, 2 = left, 0xFF unknown.
- x_direction: internal index mapped by 0x2B72A `[1, 2, 0xFF]`: 1 = toward wrist,
  2 = toward elbow. The sync state machine always sets index 1 on a normal sync, so this
  firmware reports x_direction = 2 (toward elbow) [C code, I that it is always so in practice].
- warmup_state: 2 (warm) if the warm flag is set, else 1 (cold). 0 (unknown) never sent.
- rotation: `u16 = (unsigned)(angle_rad * 1000)`, angle in [0, 2*pi] radians (the code traps if
  outside), i.e. milliradians 0..6283. It is the rotation of the electrode ring relative to the
  model's reference, estimated during the sync gesture (section 10.3). The same angle is
  quantized to a channel offset `round(angle/(2*pi)*8) mod 8` used to rotate features.
- Side effect: LED sync state 1/2 and vibration: if the classifier is enabled and the state
  went from unsynced to synced: 120 ms pause then 200 ms at 255.

### 4.2 Pose values [C]

Pose callback 0x1303C, map table 0x6F07C: 0 rest, 1 fist, 2 wave_in, 3 wave_out,
4 fingers_spread, 5 double_tap, 0xFFFF unknown.

### 4.3 sync_failed / sync progress (`fw_on_sync_progress` 0x12E3C) [C]

The sync state machine reports a per-frame result code; the callback handles two values:
- 8 (gesture in progress, valid): ramp vibration: 20 ms entries with strength starting at 100
  and increasing by 4 per FV frame (max 255).
- 5 (too many saturated/active channels, "too hard"): emits `06 01` sync_failed.
Other internal codes (2 = arm orientation out of range, 4 = channel energy profile wrong,
6 = energy too low, 7 = rotation estimate inconsistent, 0 = success) produce no event.
So only sync_failed result 1 exists on the wire; host enum values 2..6 are never produced by
this firmware.

### 4.4 Unsync [C]

`fw_on_arm_unsynced` (0x13210): if synced: lock (no buzz), arm = 0xFF, rotation = 0, sync state 0,
arm_unsynced event, and (if classifier enabled) vibration 500 ms@255, 120 ms off, 300 ms@180,
120 ms off, 200 ms@100. Triggers: the classifier sees "invalid" frames (pose unknown) at both
ends of an 11-frame history (armband removed / heavy artifacts, section 10.5), classifier
disabled via set_mode, or BLE disconnect (0x13388 re-enables the classifier state and
reinitializes).
The log string "Unlocking on unsync" (0x164D2) depends on a flag (0x200081D8) that is never set
in this build: dead code.

### 4.5 Lock / unlock logic [C]

State byte 0x1FFF0974: 0 locked, 1 timed, 2 hold. Unlock timestamp at 0x1FFF0980.
- Unlock command (0x0A type 1/2) only if arm is synced. From locked: vibration 150 ms@255,
  120 ms off, 50 ms@255 (double buzz). Records time and current arm roll angle.
- Lock (0x0A type 0) or automatic lock: if it was unlocked, vibration 300 ms@150.
- Timed unlock expires after 2000 ms (checked every FV frame in 0x1324C); pose detections do
  NOT extend it (the host SDK re-sends unlock to extend).
- While timed-unlocked, if the arm roll angle decreases by more than 0.17453 rad (10 deg)
  relative to the value at unlock (sign flipped depending on x_direction byte), the device locks
  early (0x132D4). [C code, I interpretation]
- Unsync and disconnect force lock.
- fw_info.unlock_pose is hard-coded to 5 (double_tap). The firmware itself never unlocks on a
  pose; unlocking is host driven.

---------------------------------------------------------------------------------------------

## 5. IMU

Functions: `fw_imu_configure` 0x14914, `fw_imu_process` 0x14D8C, `fw_imu_tap_cb` 0x147D0,
`fw_imu_lowpower` 0x14BA4, init 0x14C10.

### 5.1 Configuration [C]

```
mpu_set_sensors(0x79 = gyro XYZ + accel + compass); mpu_configure_fifo(0x78)
mpu_set_sample_rate(imu_rate); mpu_set_compass_sample_rate(imu_rate)
mpu_set_gyro_fsr(2000)   // +-2000 dps
mpu_set_accel_fsr(16)    // +-16 g
if lpf != 0: log N "Overriding low-pass filter to %uHz (was %uHz)"; mpu_set_lpf(lpf)
MPL: gyro/accel/compass sample period = 1e6/rate us
orientation: gyro/accel matrix {0,1,0; 1,0,0; 0,0,-1}, compass identity
dmp_load_motion_driver_firmware; dmp_set_orientation; dmp_register_tap_cb(fw_imu_tap_cb)
dmp_enable_feature(TAP); dmp_set_interrupt_mode(2); dmp_set_fifo_rate(rate)
tap: axes XYZ (7), threshold param 8 (eMPL units mg/ms), min tap count 1, tap time 100 ms
mpu_set_dmp_state(1)
```
The eMPL driver sets the LPF to about rate/2 by default and the DMP FIFO rate is limited to
200 Hz, so useful imu_rate values are about 4..200 Hz (default 50). [I for limits]
Logs at level I (3, not sent by default): "MPU gyroscope configured with sampling rate %uHz and
full-scale range of %u." etc.
When imu_mode is 0 and the classifier is disabled, the IMU is put in low-power accelerometer
motion-interrupt mode: threshold 500 mg, 10 ms, 40 Hz (sub_1BE84(500, 10, 40)).

### 5.2 IMU data packet, handle 28, 20 bytes (notify) [C]

imu_mode 1 or 3:

| Offset | Type | Field | Scale |
|---|---|---|---|
| 0 | int16 x4 | quaternion w, x, y, z | Q14: value/16384 (MPL q30 >> 16) |
| 8 | int16 x3 | accelerometer x, y, z (calibrated, MPL) | value/2048 g (q16 >> 5) |
| 14 | int16 x3 | gyroscope x, y, z (calibrated, MPL) | value/16 deg/s (q16 >> 12) |

imu_mode 4 ("raw"): quaternion as above, then raw accel from the DMP FIFO (sensor LSB at
+-16 g, 2048 LSB/g), then raw gyro (sensor LSB at +-2000 dps, 16.4 LSB/(deg/s)). [C layout, I LSB]

Quaternion source: MPL 9-axis fusion (inv_get_sensor_type_quat), not the DMP quaternion. [C]
Samples are produced at the MPU sample rate (the requested rate; the decimation accumulator in
0x14EFE is effectively 1:1). IMU data is not sent in factory-test mode.

### 5.3 imu_mode summary [C]

| imu_mode | h28 data | h31 motion events |
|---|---|---|
| 0 | - | - (IMU low power unless classifier needs it) |
| 1 | calibrated | - |
| 2 | - | yes |
| 3 | calibrated | yes |
| 4 | raw | - |

### 5.4 Motion events, handle 31, 3 bytes (indication) [C]

`[00][direction][count]` from the DMP tap callback; type 0 = tap is the only type.
direction (eMPL): 1 X up, 2 X down, 3 Y up, 4 Y down, 5 Z up, 6 Z down. count = number of taps
reported by the DMP (>= 1). Sent only for imu_mode 2 or 3. Every tap is also fed to the
classifier (double-tap detection, section 10.4).

### 5.5 Other IMU uses [C]

- Activity detection: if the accelerometer vector (q16 >> 6 per axis) changes by more than
  2^16 (squared distance) versus a reference and the movement lasts more than 90 ms, the
  inactivity timer is kicked.
- Accelerometer magnitude > 4 g spike detector (0x132B0) feeding an optional classifier
  callback (0x1FFF0974+46588, not registered in this build).
- Orientation quaternion (x,y,z,w float) is pushed into the classifier history (roll angle via
  0x17D14) every IMU sample; used by the sync gesture and the timed-unlock roll lock.
- IMU calibration: see command 0x05. At boot, stored factory biases are applied ("Calibrating IMU
  with factory calibration data"), else online auto calibration is used.

---------------------------------------------------------------------------------------------

## 6. Log characteristic (handle 56, indications, 20 bytes) [C]

`fw_log(level, fmt, ...)` (0x156F8):
- Text = 2-char prefix + formatted message (max 256), prefix table 0x31ABC:
  level 0 `E|` error, 1 `W|` warning, 2 `N|` notice, 3 `I|` info, 4 `D|` debug.
- Level threshold byte 0x1FFF00EE, default 2 (.data): E, W and N messages are generated,
  I and D are not. There is no command to change the threshold.
- Messages are always printed to the debug stdout; they are forwarded to BLE only when log mode
  (cmd 0x08) == 1.
- BLE framing: the text (including the terminating NUL and any trailing "\n" in the format) is cut
  into 19-byte chunks; each 20-byte indication = 19 text bytes + byte 19 flag
  (1 = last chunk of the message, 0 = more follow).
- Log indications go through the 32-entry indication queue (section 9.3).

Useful messages for the host: "N|IMU Cal Succeeded." / "N|IMU Cal Failed.", "N|ping: %lu",
"N|trim: %u(-%u)", "N|Classifier written successfully", "E|Custom classifier data not loaded
(expected %d, loaded %d)", "N|drain: Filled queue, %lu indications dropped while draining",
OTA messages ("N|Preparing for OTA update", "N|OTA update failure: %s", ...).

---------------------------------------------------------------------------------------------

## 7. fw_info (handle 21, 20 bytes) and fw_version (handle 23, 8 bytes)

Both are pushed to the nRF (which serves them as readable values), not notified.

### 7.1 fw_info, built by `fw_send_fw_info` (0x1270C), UART msg `[07][21][20][data]` [C]

| Offset | Size | Field | Source |
|---|---|---|---|
| 0 | 6 | serial number = BLE MAC, least significant byte first (nRF address order) | nRF msg 9 |
| 6 | 2 | unlock_pose = 5 (double_tap), constant | |
| 8 | 1 | active classifier type (user settings +32; 0 if 0xFF) | |
| 9 | 1 | active classifier index (+33; 0 if 0xFF) | |
| 10 | 1 | has_custom_classifier = slot 0 (0xA000) not blank | |
| 11 | 1 | stream_indicating = always 0 (so the nRF uses notifications for streams) | |
| 12 | 1 | sku (factory +34; 0 if 0xFF) | |
| 13 | 1 | OTA state (section 8) | 0x2000B96C |
| 14 | 2 | OTA checksum / error code | 0x2000B96E |
| 16 | 2 | OTA length in 8-byte units (or offset>>3 after CHECKSUM) | 0x2000B970 |
| 18 | 2 | 0 | |

Sent at every link start (after connect/disconnect/boot) and after every OTA command/state
change. Bytes 13..17 overlay the SDK "reserved" area.

### 7.2 fw_version, UART msg 3 (0x1285E) [C]

`[03][major u16 = 1][minor u16 = 5][patch u16 = 1970][hardware_rev u16 = 2]`, stored by the
nRF and served as handle 23 (8 bytes).

### 7.3 Device name [C]

On link start the Kinetis sends msg 10 `[0A][name, 22 bytes]` from user settings +0, or "Myo"
if unset. When the host writes the GAP device name (handle 3), the nRF sends msg 11 with 32
bytes; the Kinetis copies them to user settings +0 and saves the settings sector.

---------------------------------------------------------------------------------------------

## 8. OTA update (commands 0x13..0x19) [C]

State byte 0x2000B96C (fw_info byte 13), status u16 (byte 14), length u16 (byte 16).

| State | Name |
|---|---|
| 0 | INACTIVE |
| 1 | PREPARING (background erase/copy running) |
| 2 | READY |
| 3 | SCANNED |
| 4 | WRITING |
| 5 | COMMITTED (swap done, waiting for reset) |
| 6 | FAILED (status = error code) |

Error codes (status field in state 6): 1 = command in wrong state, 2 = flash write/erase error,
3 = attempt to write same flash location twice (non-blank and different), 4 = bootloader image
mismatch or image checksum failure.

Commands (all ignored in state 5; every command kicks the inactivity timer; every state change
re-sends fw_info):

| Cmd | Name | Frame | Accepted in | Action |
|---|---|---|---|---|
| 0x13 | PREPARE | `[13][00]` | 0 | state 1; main loop erases 128 x 4 KB sectors of 0x80000..0xFFFFF (skipping 0x8F000), one per loop pass, then copies 0x0..0xEFFF (bootloader + persistent data) to 0x80000.. 8 bytes per pass; then state 2, status 0, length 0, log "Finished preparing for OTA update". Erase failure: FAILED(2) |
| 0x14 | CONTINUE | `[14][00]` | 0 | resume without erasing: requires bank 2 bootloader area identical (word 0 and memcmp 0x4..0xEFFF vs 0x80004..), else FAILED(4); then state 2, status 0, length 0 |
| 0x15 | ABORT | `[15][00]` | != 0 | state 0 |
| 0x16 | COMMIT | `[16][00]` | any | verify bootloader copy and image checksums (below), else FAILED(4); flash swap (init/swap/complete through the Kinetis FTFE swap commands with indicator at 0xF000); state 5 |
| 0x17 | CHECKSUM | `[17][08][offset u32][length u32]` | 2,3,4 | status = Fletcher16(0x80000+offset, length), length field = offset>>3, state 2 |
| 0x18 | SCAN | `[18][08][offset u32][maxlen u32]` | 2,3,4 | walk 8-byte blocks from 0x80000+offset while block != all 0xFF and within maxlen; status = Fletcher16 of scanned blocks, length = blocks scanned (8-byte units), state 3 |
| 0x19 | WRITE | `[19][12][block u16][16 data bytes]` | 2,3,4 | addr = 0x80000 + 8*block; if addr != expected next addr: running checksum = 0xFFFF, running length = 0; if target not blank: must equal data else FAILED(3); else program 16 bytes (flash error: FAILED(2)); running checksum updated (Fletcher16 continued), length += 2; state 4 |

Fletcher16 (`fw_fletcher16` 0x12D28): init 0xFFFF (sum1 = 0xFF, sum2 = 0xFF); per byte
sum1 += b; sum2 += sum1; end-around-carry reduction mod 255; result = sum1 | (sum2 << 8).
WRITE continues the previous checksum, so status after a contiguous run = Fletcher16 of the
whole run.

Image verification at COMMIT (`fw_ota_verify_image` 0x12DD4): pointer word at image offset
0x101FC (flash 0x901FC in bank 2) + 0x80000 points to a list of records
`{u16 type, u16 record_len, u32 offset, u32 size, u32 checksum}`; type 1 records are checked with
Fletcher32 (`fw_fletcher32` 0x12D8A: byte-wise, sum1 = sum2 = 0xFFFF, end-around carry mod
65535, result = sum1 | sum2 << 16); type 0 ends the list (success); 0xFFFF = fail.
Verified against the running 1.5.1970 image: records at 0x75390:
{1, 16, 0x10410, 0x64F80, 0x0CD2F0E7} and {1, 16, 0x10000, 0x190, 0xE89C7057}, then type 0.
Both checksums recomputed and match. [C]

Completion: after COMMIT (state 5) the device resets when the inactivity timeout next fires
("Resetting on sleep to finish OTA update", 0x16330); the swapped bank becomes active after
reset. There is no explicit "reset now" command.

The host gets no notifications for OTA; it polls fw_info (handle 21) bytes 13..17.

---------------------------------------------------------------------------------------------

## 9. Kinetis to nRF51822 UART link

### 9.1 Framing [C]

- SLIP: END 0xC0, ESC 0xDB, ESC_END 0xDC, ESC_ESC 0xDD. Every message is a fixed 23-byte
  payload (zero padded) framed as `C0 <escaped 23 bytes> C0` (`fw_uart_slip_tx` 0x1240C).
- RX frames up to 23 bytes, zero padded, queued in 16 slots (0x1FFF0394).
- Link start (`fw_uart_link_start` 0x1281E): power/enable the nRF via GPIO 0x6F168, wait for raw
  byte 0x02 from the nRF, then send msg 7 (fw_info), msg 3 (fw_version), msg 10 (name),
  msg 2 (start), wait for raw byte 0x03; then link up. Link stop (0x127D4) drops the GPIO.
- Watchdog: if no heartbeat (nRF msg 10) for more than 200 ticks (2 s) the Kinetis resets
  itself (0x12930). Unexpected message types light the error LED for 200 ms.

### 9.2 Message types [C]

Kinetis to nRF:

| Type | Layout | Meaning |
|---|---|---|
| 2 | `[02]` | configuration done, start services/advertising |
| 3 | `[03][u16 x4]` | fw_version value (handle 23) |
| 7 | `[07][15][14][20 bytes]` | set fw_info value (handle 21) |
| 8 | `[08][handle][len][data]` | notify (handle 17 goes to the Battery Service) |
| 9 | `[09][handle][len][data]` | indicate (queued, flow controlled) |
| 10 | `[0A][22-byte name]` | set GAP device name |

(nRF also implements 1 = power off (sd_power_system_off), 4 = set advertising interval u16,
5 = advertising start, 6 = advertising stop; the Kinetis application never sends them.)

nRF to Kinetis:

| Type | Meaning |
|---|---|
| 1 | link error (UART error on nRF side): Kinetis restarts the link |
| 4 | connected: suppress streams until set_mode, LED "connected", reset event cache |
| 5 | disconnected: classifier re-enabled/reset, lock, FV/IMU/EMG back to defaults (streams off), sleep mode 0, LED; link restart |
| 6 | GATT write `[06][handle][len][data]` (only handle 25 processed) |
| 7 | debug text bytes to Kinetis stdout |
| 9 | BLE address, 6 bytes LSB first (used for fw_info serial; also triggers factory test output when the test pin is strapped) |
| 10 | heartbeat |
| 11 | device name written (32 bytes) |
| 12 | `[0C][n]` number of indications in flight on the nRF |

### 9.3 Indication queue and flow control [C]

Indications (msg 9: classifier events h35, motion events h31, log h56) are queued in 32 x 23
bytes (0x1FFF054C). One is sent to the nRF when `2*in_flight <= 32` (in_flight from msg 12,
incremented locally per send). If the queue overflows (> 31 entries) the firmware enters a
"draining" mode in which all new indications are dropped until the queue is empty, then logs
"N|drain: Filled queue, %lu indications dropped while draining". Command 0x11 trims the queue.
The queue is cleared on connect and disconnect. Notifications (msg 8: EMG, FV, IMU, battery)
bypass the queue and go straight to the UART.

### 9.4 GAP parameters (nRF app) [C]

- Preferred connection parameters (nRF 0x1438E, sd_ble_gap_ppcp_set): min interval 6
  (7.5 ms), max interval 6 (7.5 ms), slave latency 0, supervision timeout 400 (4 s).
- TX power +4 dBm (SVC 0x77 with 4). Appearance 960 (0x03C0, generic HID).
- Advertising: connectable undirected, interval 800 x 0.625 ms = 500 ms, no timeout; flags 0x06,
  shortened device name (8 chars), one 128-bit service UUID (AD type 0x06). (msg 4 could change the interval; not used.)
- The nRF only builds its GATT table after msg 2, using the fw_info/fw_version it received.

---------------------------------------------------------------------------------------------

## 10. Classifier runtime (sync, warmup, poses) - including the custom blob

Classifier core object at 0x1FFF0984 (about 46.6 KB). Entry point per FV frame:
`0x1324C` (timed-unlock expiry, then if enabled `fw_cls_process_frame` 0x19CF0 with
rms[8] floats (possibly warmup-boosted), mpf[8] floats (window MPF), artifact mask).
Callbacks registered in 0x12EAC: sync (0x1306C), unsync (0x13210), state-restart (0x12E38:
restart warmup timer), pose (0x1303C), sync progress (0x12E3C).
Frame rate = FV rate (50 Hz with the default window/shift). Note that the host's
set_mode_ext window/shift also change the classifier input rate. [C]

### 10.1 Model selection and slots [C]

- 5 custom slots (index 0..4, addresses in 2.14). Type 1 + index selects a slot; a slot is
  "present" if its 7696 bytes are not all 0xFF (0x13704). Anything else (or blank slot) uses the
  built-in model.
- On load (`fw_cls_custom_load_blob` 0x16A58) the blob is copied into the core object:

| Blob offset | Meaning | Firmware use |
|---|---|---|
| +0 u32 (host writes 1) | version/format | not read by the firmware |
| +4 u32 (host writes 0) | "remap by sync" flag | copied to core word 0; if non-zero, at every sync the codebook is re-oriented: channel order mirrored for arm index 2 (0x18810) and rotated by the sync channel offset (0x1882A) (0x167B0). 0 = codebook used as uploaded |
| +8 u32 numClasses | must be <= 7 | number of classes scored |
| +12 u32 K | must be <= 128 | codebook size (entries 1..K-1 searched) |
| +16 float[7][128] | P[c][k] | class likelihoods |
| +0xE10 float[128][8] | codebook | entry 0 unused in search (0 = inactive) |

### 10.2 Custom classifier algorithm (`fw_cls_custom_classify` 0x16870) [C]

```
// input: rms[8] (FV RMS, ADC counts, warmup boost applied), prev_pose, invalid flag
if invalid_frame: return 0xFFFF                          // unknown
active = count(rms[ch] >= 200.0)                         // FIXED threshold 200.0 (0x168F8)
if active <= 1: k = 0
else:
    n = rms / ||rms||_2
    k = argmax_{j=1..K-1} dot(n, codebook[j])            // ties keep lowest j, needs dot > 0
for c in 0..numClasses-1:
    acc[c] = 0.9 * acc[c] + log2(P[c][k])                // exponential forgetting, factor 0.9
p[c] = 2^acc[c] / sum_c 2^acc[c]
best, second = two largest p
margin = (log2 p[best] - log2 p[second]) / max(|log2 p[best]|, 15.0)
if margin < T[prev_pose][best]: result = 0 (rest) else result = best
if result >= numClasses: result = 0xFFFF
```
- The activity threshold is not in the blob and not adaptive: 200.0 RMS counts. (Host training
  used 2 x |mean rest vector|; firmware ignores that.)
- acc[] is a leaky sum (no fixed window): effective memory about 10 frames (0.2 s at 50 Hz).
- Hysteresis table T (7x7 floats at 0x32200, row = previous reported pose, column = candidate):
  0.5 everywhere, except 0.9 for candidate rest (col 0; irrelevant, rest is returned anyway) and
  for "opposite" transitions fist to fingers_spread (row 1 col 4), wave_in to wave_out (2,3),
  wave_out to wave_in (3,2). Rows/cols 5,6 are 0.
- The custom path has no additional debounce: the result is reported directly (pose events are
  sent on every change). Double tap (pose 5) is injected by the tap detector (10.4).

### 10.3 Sync gesture, arm and rotation (`fw_cls_sync_state` 0x197E8, `fw_cls_sync_gesture_check` 0x19568) [C/I]

States: 0 reset, 1 collecting/syncing, 2 synced.
- Entering state 1: per-channel thresholds set to 120.0 (150.0 for mode 1; the app uses mode 2),
  per-channel max RMS reset, pose unknown, x-dir index 2 (unknown).
- In state 1 every frame: RMS pushed into a history ring; per-channel running max updated.
  When enough frames are collected the frame window is checked:
  - reset to state 0 if the window fails the "bad frame" profile checks (0x18E28, thresholds
    from table 0x6EF58 + 56*mode, e.g. mode 2: all channels >= 500, or a statistic >= 1900).
  - else `fw_cls_sync_gesture_check` returns a code (see 4.3); code 8 = frame consistent with the
    sync gesture, counted; after 50 consecutive good frames (20 when a "fast" flag is set) the
    result is 0 = success. Criteria used: arm orientation (IMU) within +-22.5 deg (or +-45),
    RMS norm > 300 (> 180 once started), per-channel energy profile check, rotation estimate
    stable within 0.196 rad between frames, at most 3 channels above the squared-max test
    ("too hard" = code 5).
  - On success: arm = consistency test between the IMU roll-angle sector during the gesture and
    the EMG peak channel (0x18E64, 8 sectors of 45 deg): consistent = internal 1 (reported
    right), otherwise 2 (left) [I on the physical meaning]; x-dir index 1 (reported 2 = toward
    elbow); rotation: best fit of 42 candidate angles (0.025 rad steps, +-0.5 rad around a coarse
    estimate) of the averaged normalized EMG profile against a template (0x6F00C / 0x6F02C),
    wrapped into [0, 2*pi] (0x193A4); channel offset = round(angle*8/(2*pi)) mod 8.
  - state 2, pose 0, callback arm_synced.
- Warmup restart callback is called whenever the machine returns to state 0.

### 10.4 Double tap (pose 5) (`fw_cls_double_tap_detect` 0x17B18) [C]

IMU tap events (any direction) are recorded into a per-frame history. A double tap is two
taps 7..25 frames apart (140..500 ms at 50 Hz) with the first one at least 25 frames back in
history, confirmed by EMG (0x17844): last up to 75 frames, max RMS <= 1650, all channel peaks
<= 600, at least 2 channels with two distinct EMG peaks (70..800, 4..24 frames apart, rise > 40
and dip > 15), and inter-channel correlation >= 0.4 for more than 20 of the 28 channel pairs.
On detection pose = 5 is reported and held for 20 frames (0x19C0A / 0x199C4), then rest.

### 10.5 Built-in classifier (`fw_cls_builtin_classify` 0x16CD8) [C]

Used when no custom blob is active; requires sync (arm 1/2).
- Features per frame: 16 = 8 x (rms / ||max-RMS vector||) + 8 x MPF, both re-ordered for the
  arm (mirror for arm 2) and rotated by the sync channel offset (0x1886E).
- Standardized with mean/std tables (struct 0x1FFF00F0: 16 features, mean 0x322C4, std 0x32304),
  pushed into a 25-frame history; input = 5 frames spaced 5 apart (t, t-5, .., t-20) = 80 values
  (0.5 s span).
- Network (struct 0x1FFF013C, 8 layers, weights in flash): Dense 80 to 256 (W 0x5AA58,
  b 0x32344), PReLU, Dense 256 to 128 (W 0x32A58, b 0x32758), PReLU, Dense 128 to 64 (W 0x52A58,
  b 0x32958), PReLU, Dense 64 to 5 (W 0x6EA58, b 0x32744), Softmax. Classes: rest, fist, wave_in,
  wave_out, fingers_spread.
- Output = argmax, then majority vote over the last 10 decisions (ties = rest), then
  post-processing 0x17380 (20-frame decision history, 4-of-5 agreement when entering a pose,
  pose held while RMS stays within 1.3x of the entry profile, 10-frame refractory after a pose
  ends, fist/fingers_spread rejected if IMU taps occurred in the tap history window).
- "Invalid frame" flag (armband off/artifact): profile test on table 0x6EF58 or more than 2
  channels flagged in the motion-artifact masks of the last 3 frames; invalid frames give pose
  0xFFFF; unknown at both ends of the 11-frame pose history triggers unsync (4.4).

---------------------------------------------------------------------------------------------

## 11. Battery, sleep, inactivity, LEDs

### 11.1 Battery (`fw_battery_update` 0x1238C) [C]

- Each ADC frame provides a voltage index `v = (100*adc - 99400) / 418` (0x13DD2); when the EMG
  ADC is off an alternative path uses `(100*x - 207100)/870` (0x13F3A).
- Every 16 frames a sample is taken; after 16 samples the mode (most frequent value, clamped to
  100) is computed; it must repeat 8 times before it is accepted.
- Percent = discharge-curve table 0x2B5DC[v] (101 entries, 0..100), sent as 1-byte notify on
  handle 17 (the nRF Battery Service; standard BAS only notifies on change [I]).
- LED battery state: > 15 % normal, 6..15 % low, <= 5 % critical.

### 11.2 Inactivity / sleep (`fw_inactivity_check` 0x16374) [C]

- Timer kicked by: synced classifier frames, IMU movement (> 90 ms), IMU interrupt while DMP off,
  OTA commands and PREPARE progress, command 0x09. Other commands (set_mode, vibrate, ...) do NOT
  kick it; BLE connection alone does not prevent sleep.
- If sleep mode != 1 and more than 30 s (0x752F ms) without activity: log I "Inactivity timeout
  reached"; if OTA state 5, reset now (finishes the update); verify accelerometer (else error LED
  forever); IMU low-power motion interrupt (500 mg, 10 ms, 40 Hz); LEDs off; nRF link stopped;
  EMG stopped; wake pin armed (0x6F0F8); Kinetis enters a low-leakage stop mode (0x211B0) and
  never returns (wake = reset). [C sequence, I exact power mode]
- Deep sleep command 0x04: see 2.4.

### 11.3 LEDs (patterns only summarized) [C]

Two RGB LEDs (6 PWM channels, pins 0x1FFF00D0..0x1FFF00E4), pattern engine 0x15498 with
patterns in flash (0x31884..0x31A78). Pattern depends on connected state (0x1566C), sync state
0/1/2 (0x15660), battery state and error/test overrides (0x15678 / 0x1559C).

---------------------------------------------------------------------------------------------

## 12. Easily confused points

- UART msg 3 carries fw_version (1, 5, 1970, 2); the preferred connection parameters are set inside the
  nRF app (7.5 ms / 7.5 ms / latency 0 / 4 s).
- The classifier upload buffer is at 0x20009B54; the `loaded` counter is at 0x2000B964.
- fw_info byte 11 (stream_indicating) is always 0 in this firmware; byte 12 is the SKU;
  bytes 13..17 carry OTA state/status/length.
- Only sync_failed reason 1 exists; x_direction is always reported as 2 (toward elbow) after a
  normal sync.

---

# Part C. Link layer and USB


Source: "Myo Connect.exe" (IDA instance qntp, imagebase 0x400000). Statically linked libble (namespace
libble::ble11x) + libmyo + Qt5 UI. Addresses below are function starts unless noted. Names prefixed
Ble_/Usb_ were assigned during this pass.

Legend: [C] = confirmed from code/data, [I] = inferred (reasoned from context, not traced to the end).

---

## 1. BGAPI transport to the BLED112 dongle

### 1.1 Dongle detection and serial port

- [C] `BluetoothDongle` (vtable 0xFF239C) polls for the dongle with `Ble_FindComPortByVidPid` (0x516030),
  called from 0x510F00 (initial) and 0x5110D0 (timer re-poll). VID/PID come from globals
  0x10913B0 = 0x2458, 0x10913B4 = 0x0001 (Bluegiga BLED112 CDC, matches drivers/usbserial.inf).
- [C] Detection method: SetupDiGetClassDevs(interface class GUID) + SetupDiEnumDeviceInterfaces, match
  substring `vid_2458&pid_0001` in the device path, then SetupDiOpenDevRegKey and read value `PortName`
  (e.g. "COM5").
- [C] When a port is found and no connection object exists, 0x5112F0 constructs a `BLE11xConnection`
  (ctor 0x513A40, `Ble_BLE11xConnection_ctor`). If the port disappears or the connection is in error
  state 9, the connection object is destroyed (-> "DongleLost"; "DongleDetected"/"DongleLost" are
  hub events, strings at 0xFE0508/0xFE0518). Error 995 (ERROR_OPERATION_ABORTED) on the timer is ignored.
- [C] Serial port: boost::asio serial_port wrapper `AsioSerialPort` (vtable methods 0x51E800 open,
  0x51E510 close, 0x51E430 async read, 0x51E4A0 async write). No `set_option` call (baud, parity, flow)
  was found for the serial port; the port is opened with boost/Windows defaults. [I] Baud rate is irrelevant
  for the BLED112 (USB CDC ACM); use 115200 8N1, no flow control in own code.
- [C] No BGAPI "packet mode" length prefix is used: packets are raw BGAPI (4-byte header + payload).

### 1.2 Framing

BGAPI 1.1 header (4 bytes) as built by `Ble_BgapiBuildCommand` (0x516A50) from the command table at
0xFF2C50 (8 bytes per entry: header dword + parameter-format nibbles) and parsed by
`Ble_BgapiDispatchPacket` (0x51C1B0):

| Byte | Meaning |
|------|---------|
| 0 | bit7 = 1 event / 0 command-response; bits 6..3 technology (0 = BLE); bits 2..0 = payload length high bits |
| 1 | payload length low byte |
| 2 | class id |
| 3 | command/event id |
| 4.. | payload, little endian; uint8array = 1-byte length + data; bd_addr = 6 bytes LSB first |

### 1.3 BGAPI commands actually sent [C]

(Index = entry index in the table at 0xFF2C50, as passed to 0x516A50; call sites listed.)

| Index | Class,Id | Command | Params sent | Sent from |
|-------|----------|---------|-------------|-----------|
| 1 | 0,1 | system_hello | - | 0x51F260 (handshake start), 0x519C30 (pre-built keep-alive packet) |
| 8 | 0,8 | system_get_info | - | 0x51FD90 |
| 6 | 0,6 | system_get_connections | - | 0x51F6A0 |
| 12 | 0,12 | system_whitelist_clear | - | 0x5202F0 |
| 10 | 0,10 | system_whitelist_append | bd_addr, addr_type | 0x5202A0, 0x5206D0 |
| 28 | 3,0 | connection_disconnect | connection | 0x515B30 (API), 0x51F5B0 (handshake cleanup) |
| 29 | 3,1 | connection_get_rssi | connection | 0x515B80 |
| 41 | 4,4 | attclient_read_by_handle | connection, u16 handle | 0x517600 |
| 42 | 4,5 | attclient_attribute_write | connection, u16 handle, uint8array | 0x517640 |
| 58 | 6,2 | gap_discover | mode = 1 (gap_discover_generic) | 0x523360 |
| 59 | 6,3 | gap_connect_direct | bd_addr, addr_type=0, int_min, int_max, timeout, latency | 0x523CE0 |
| 60 | 6,4 | gap_end_procedure | - | 0x51FA60, 0x5215F0, 0x521BA0, 0x5233B0, 0x523DD0 |
| 61 | 6,5 | gap_connect_selective | int_min, int_max, timeout, latency | 0x5212C0 |

NOT used anywhere [C] (no call site with that table index): gap_set_scan_parameters (6,7),
gap_set_mode, connection_update (3,2), any sm_* (class 5: no encryption / bonding),
attclient_find_information / read_by_group_type / read_by_type (no GATT discovery),
attclient_write_command (4,6), attclient_indicate_confirm, system_reset.

### 1.4 BGAPI responses/events handled [C]

Dispatcher 0x51C1B0 routes to listener interface slots:

| Type | Class,Id | Name | Listener slot |
|------|----------|------|---------------|
| rsp | 0,1 | system_hello | +44 |
| rsp | 0,6 | system_get_connections | +52 |
| rsp | 0,8 | system_get_info | +48 |
| rsp | 0,10 | system_whitelist_append | +60 |
| rsp | 0,12 | system_whitelist_clear | +56 |
| rsp | 3,1 | connection_get_rssi | +32 |
| rsp | 6,2 | gap_discover | +8 |
| rsp | 6,3 | gap_connect_direct | +12 |
| rsp | 6,4 | gap_end_procedure | +20 |
| rsp | 6,5 | gap_connect_selective | +16 |
| evt | 3,0 | connection_status | +24 |
| evt | 3,4 | connection_disconnected | +28 |
| evt | 4,1 | attclient_procedure_completed | +36 |
| evt | 4,5 | attclient_attribute_value | +40 |
| evt | 6,0 | gap_scan_response | +4 |

Everything else (including the responses to attclient_read_by_handle / attclient_attribute_write /
connection_disconnect, system_boot, attclient_indicated, connection_version_ind / feature_ind) is
silently dropped. [I] Indications are confirmed automatically by the BLED112 stack; an ESP32 central
must make sure its stack confirms indications (NimBLE/Bluedroid do by default).

### 1.5 Dongle handshake (HandshakeProcedure, vtables 0xFF3B48/0xFF3B5C) [C]

Run once per dongle open inside `Ble_BLE11xConnection_ctor` (0x513A40), which blocks on a condition
variable until the handshake finishes or a 500000-unit timer expires ([I] boost microseconds = 0.5 s;
on expiry: "timeout after waiting", exception InitializationTimeout).

1. Send `system_hello` (0x51F260), state 1.
2. On hello rsp (0x51FD90): send `system_get_info`, state 2.
3. On get_info rsp (`Ble_Handshake_onGetInfoRsp`, 0x51FA60): compare (major, minor, patch) with the
   minimum stored in the procedure (ctor 0x51EE40: words 1, 2, 1) using lexicographic compare 0x51EDE0.
   - Minimum dongle firmware = **1.2.1** (build number ignored). Lower: error "Firmware version is
     a.b.c, minimum required version is ..." -> exception VersionTooLow -> UI string
     "Bluetooth Dongle firmware version insufficient: " (0x5141A0).
   - Otherwise send `gap_end_procedure` (stop any running scan/connect), state 3.
4. On end_procedure rsp (0x51F6A0): result 0 or 0x0181 (wrong state, i.e. nothing was running) is OK,
   else "Unable to turn off discovery, received result: ". Send `system_get_connections`, state 4.
5. On get_connections rsp (0x51F920): remember `maxconn`, allocate a bitset of maxconn slots, state 5.
   The dongle then emits one `connection_status` per slot.
6. For every `connection_status` (0x51F5B0): if flags bit0 (connected) is set -> send
   `connection_disconnect(connection)`; else mark slot done. `connection_disconnected` (0x51F4F0) marks the
   slot done. When all slots are done -> handshake complete (state 6).

Other results map to exceptions in 0x513A40: UnexpectedResult ("Unexpected result while communicating
with BLE dongle: ", 0x514140), PacketRetrievalException, ParsingError ("Unable to parse data from dongle: ").

### 1.6 Dongle keep-alive / watchdog [C, units I]

`PacketReaderWriter` (ctor 0x519C30) pre-builds a `system_hello` packet. Its timer handler 0x51A960
re-arms a 500000-unit timer ([I] 0.5 s). On each tick, if no traffic was seen, it sends the pre-built
`system_hello`; if the previous probe is still unanswered (flag +124 clear) it raises
PacketReaderWriterException "Dongle stopped responding" (0xFF35F8), which tears the connection down
and leads to DongleLost / re-detection.

---

## 2. Scanning and Myo recognition

### 2.1 Scan parameters [C]

- `gap_discover(mode = 1 = generic)`; scan interval/window/active are never set by the app
  (no gap_set_scan_parameters), so the BLED112 defaults apply. [I] BLED112 default is active scanning,
  interval 0x4B (46.875 ms), window 0x32 (31.25 ms); scan responses are therefore received.
- Scan stop: `gap_end_procedure`.

### 2.2 Advertisement parsing (DiscoveryProcedure, vtables 0xFF3FF4/0xFF4010) [C]

`Ble_ParseScanResponseAdData` (0x522FC0) is called for every `gap_scan_response` event. Event layout:

| Offset | Size | Field | Used |
|--------|------|-------|------|
| 0 | 1 | rssi (int8, dBm) | yes |
| 1 | 1 | packet_type | no |
| 2 | 6 | sender bd_addr | yes |
| 8 | 1 | address_type | no |
| 9 | 1 | bond | no |
| 10 | 1 | data length | yes |
| 11 | n | AD structures | yes |

AD structures are walked as (len, type, data[len-1]). Only two AD types are looked at:

- **Type 0x06 (Incomplete List of 128-bit Service UUIDs)** with len >= 17: the first 16 bytes are taken
  as a UUID. If the procedure has a UUID filter list, the UUID must be in it; then the device is reported
  as (address, uuid, rssi). Note: AD type 0x07 (Complete list) is NOT recognised.
- Type 0x08 (Shortened Local Name): copied into a temporary string that is then discarded. [I] Name is
  not used for recognition.

No manufacturer data (0xFF), flags, TX power, firmware/hardware/"sku" field is parsed from the
advertisement. [C]

### 2.3 Myo filter and pairing proximity (libmyo) [C]

`Ble_PairingFilterAdvert` (0x50E290), called from the "device found" hub handler 0x50DBE0 only while the
hub is in pairing mode (state field +44 == 2):

1. UUID must equal the 16 bytes at 0xFF21A0:
   `42 48 12 4A 7F 2C 48 47 B9 DE 04 A9 01 00 06 D5` = UUID **d5060001-a904-deb9-4748-2c7f4a124842**
   (Myo control service), little-endian in the AD data.
2. The address must not already be in the known list.
3. **RSSI >= host->minPairingRssi()**, which is a constant **-39 dBm** (0x5129E0 `mov al, 0D9h`; the mock
   host 0xFF2658 returns the same). This is the "hold the Myo next to the dongle" pairing rule.

If accepted, the address becomes the connect target (state "connecting") and the host connect method is
called with that single address (0x50DBE0 -> host vtable +28).

### 2.4 "Paired", "not paired" and "rejected" [C]

Per-Myo object (ctor 0x501FA0, 128 bytes) flags: +55 connected, +56 streaming/ready, +57 paired,
+58 rejected, +72 BGAPI connection handle.

- "paired" (+57) is set in 0x504200 the first time the device name (handle 3) read completes after
  connect; a "paired" event is emitted. Unpair (0x507C00 -> 0x505DF0) clears +57 and disconnects
  ("Cannot unpair while pairing" guard).
- "rejected" (+58) is set by `Ble_Myo_reject` (0x5049E0, log "warning: rejecting myo <addr>. <reason>"),
  which then disconnects. Callers:
  - `Ble_Myo_checkFirmwareVersion` (0x503EE0): firmware major != 1 or minor < 5 ->
    "Incompatible firmware version a.b.c. The SDK requires firmware version 1.x.x, minimum ...".
  - 0x503050 at 0x50350D: bad attribute data length on handle 23 (firmware version not 8 bytes).
- A rejected Myo is removed from the reconnect list on disconnect (0x5069E0 case 2 -> 0x50D980) and every
  API command then throws `Unable to "<cmd>"; Myo has been rejected`.

---

## 3. Connection

### 3.1 Connection parameters [C]

`GapCoordinator` is constructed in 0x513A40 (0x517D20) with:

| Field | Value | Meaning |
|-------|-------|---------|
| conn_interval_min | N | units of 1.25 ms |
| conn_interval_max | N | same as min |
| timeout | 64 | 64 x 10 ms = 640 ms supervision timeout |
| latency | 0 | slave latency 0 |

N comes from the dongle's max connection count M (get_connections rsp, 0x515E50):
M == 1 -> 6, M == 2 -> 8, otherwise 2*M. With stock BLED112 firmware (M = 3) N = 6 = **7.5 ms**.
Validators: interval >= 6 ("Minimum allowed connection interval is 7.5ms (6 units of 1.25)", 0x5214A0),
timeout 10..3200 (0x521C80).

Packet for a direct connect (0x523CE0):

| Bytes | Value |
|-------|-------|
| 00 0F 06 03 | header (len 15, class 6, id 3) |
| 6 bytes | Myo bd_addr (LSB first) |
| 00 | address type public |
| 06 00 | interval min |
| 06 00 | interval max |
| 40 00 | timeout 640 ms |
| 00 00 | latency |

No connection parameter update is ever requested by the central, no encryption/bonding, no MTU
exchange (BGAPI 1.1 BLED112, ATT MTU stays 23; all writes are <= 20 bytes). [C]

### 3.2 Direct vs selective connect, reconnect [C]

- GapCoordinator/BluetoothDongle (0x5159E0 / 0x514D00): if the target list has exactly one address (or a
  slot already has a pending direct connect) -> `ConnectDirectProcedure` (factory +20, 0x51D0D0,
  vtables 0xFF40A0/0xFF40D0); otherwise -> `SetWhitelistProcedure` (whitelist_clear + one
  whitelist_append per address) followed by `ConnectSelectiveProcedure` (gap_connect_selective with the
  same interval/timeout/latency).
- ConnectDirectProcedure with more than one address cycles through them with a timer of 10000000
  units ([I] 10 s per address) (0x523740, 0x5242E0).
- A procedure completes when both the connect_direct rsp (result, connection handle; flag bit0) and the
  matching `connection_status` for the same address (flag bit1) have been seen (0x5240B0 / 0x523FE0).
- Only one GAP procedure runs at a time; GapCoordinator ends scanning (`gap_end_procedure`) before
  connecting and vice versa ([I] from the state handling in 0x518E70/0x518F50/0x523DD0).
- Reconnect (libmyo host state machine, 0x50D8A0 / 0x50DA50 / 0x50E130 / 0x50E210): the hub keeps the
  list of known (paired) Myo addresses. Whenever it is idle and the list is non-empty it calls
  connect(list). On disconnect of a paired, non-rejected Myo (0x5039D0 -> 0x507840 -> 0x50E130) the
  address is put back into the list and connecting restarts immediately. There is no retry limit or
  back-off: the dongle simply keeps a connect procedure pending until the Myo advertises again.
- The pairing counter (+48) is decremented on every successful connect in pairing mode; at 0 pairing ends
  and the hub returns to connecting to the known list.

### 3.3 GATT: no discovery, hardcoded handles [C]

No attclient discovery command is ever sent; all accesses use the fixed handles listed in Part A section 3.
There is no per-firmware handle validation. Validation is only:
- handle 23 (firmware version) must be exactly 8 bytes and major == 1, minor >= 5, else reject;
- reads of 3/21/23 are only accepted before the Myo is "ready" (+56), notification handles only after.

ATT operations are queued (0x516C60 / 0x516D50 push into a deque, 0x517570) and completion is taken
from `attclient_procedure_completed` / `attclient_attribute_value`. Writes use
`attclient_attribute_write` (ATT Write Request, with response) for both commands and CCCDs.

### 3.4 Ordered sequence after connect until streaming [C]

1. `connection_status` (flags connected) -> hub event "connected" -> `Ble_Myo_onConnected_readInfo`
   (0x5044D0): set +55 connected, store connection handle.
2. read_by_handle **23** (firmware version, 8 bytes: major, minor, patch, hardware_rev as u16).
3. read_by_handle **21** (firmware info, 20 bytes).
4. read_by_handle **3** (device name).
5. Handle 23 value -> `Ble_Myo_checkFirmwareVersion`: reject + disconnect if major != 1 or minor < 5.
6. Handle 21 value -> fw info parse (0x503E70): unlock pose, active classifier, stream_indicating
   (+54), etc.
7. Handle 3 value -> name stored (0x503940), then 0x504200: first time -> paired=1 + "paired" event;
   then `set_mode` sequence 0x5024F0:
   1. `Ble_Myo_writeCccds` (0x502690):
      - CCCD 18 (battery) = 01 00 (notify), always;
      - EMG CCCDs by EMG mode: mode 2/3 -> 44, 47, 50, 53; mode 1 -> 40; mode 4 -> 44, 47, 50, 53, 40;
        mode 5 -> 40, 44; value 01 00, or 02 00 if fw info stream_indicating;
      - IMU data CCCD 29 (imu mode 1 or 3), same notify/indicate value;
      - motion CCCD 32 (imu mode 2 or 3) = 02 00 (indicate);
      - log CCCD 57 (if log requested) = 02 00;
      - classifier CCCD 36 (if classifier enabled) = 02 00.
   2. Command on handle 25: `01 03 emg imu cls` or, when EMG mode is 2/3/4 or IMU rate != 50,
      `02 0A emg imu cls emg_rate_u16 64 14 imu_rate_u16 00` (fv_window 100, fv_shift 20; see Part A section 5).
   3. Conditionally `08 01 01 00 00` (set_log_mode 1) via 0x504EC0.
8. +56 ready = 1 and the "connected/ready" event is emitted; data now arrives as attribute_value events
   routed by handle in 0x503050.

### 3.5 Keep-alive, trim, battery, RSSI

- [C] No periodic Myo-level keep-alive exists in libble/libmyo. `ping` (0x5048B0, command
  `10 04 u32`, 6 bytes) and `trim event queue` (0x505B90, command `11 02 u16`, 4 bytes) are
  exposed as Qt slots (`ping(response)` and `trimEventQueue(maxLength)` in the QMyo metaobject,
  reached from the Qt static metacall 0x46E970 via 0x4705D0 / 0x470C70), so they can only be called
  by name. The link supervision relies on the 640 ms supervision timeout.
- [C] Both are called from exactly one place in the shipped resources: the gesture recording page of
  the bundled data-collection flow (`collector`, QML resource 0016972C, constants 0015D679). Together
  they bracket a gesture inside the firmware's indication queue, which is buffered and therefore
  lags the user:
  ```
  while waiting for the user:  trimEventQueue(500) on a repeating 500 ms timer
      (gestureStartPaddingSize = 500, "roughly the same as the interval" at 1000 Hz EMG, so a fixed
       half second of pre-gesture padding is kept without letting a backlog build up)
  space bar pressed:           trimEventQueue(500), then ping(2)
  space bar released:          ping(3)
  take finished or abandoned:  trimEventQueue(0)
  ```
  The page then finds the two markers in the stream by matching the log prefix `N|ping: ` against
  the values 2 and 3, and also watches `N|trim: ` and `N|drain: ` to account for dropped events. So
  `ping` is an in-stream marker that says where the gesture starts and stops, not a latency probe:
  the echo keeps its place in the queue, which is the point. A comment in the page names the reason:
  "Small differences are likely do to delay getting the pings into the Myo event queue due to BT
  interference".
- [C] Nothing else calls either command. The consumer UI (armband manager, calibration wizard,
  connectors) never does, and neither does the link management.
- [C] The data-collection flow itself is Thalmic's own: its intro page reads "Thanks for helping out!
  The pose and gesture data will be used to help us improve gesture ...", it records seven items
  (Rest, Fist, FingersSpread, WaveIn, WaveOut, DoubleTap, SetupGesture) over at least three
  sessions, and it has a "field mode" with skin conductivity readings, environment measurements and a
  randomised 1 to 5 minute warm-up. It is backed by a C++ `DataRecorder` class whose slots
  (`recordCurrentGestureEvent`, `recordWarmUpStartedEvent`, `recordArmPlacementEvent`,
  `recordSkinConductivity`, `recordMetadataEvent`, `onFeatureVectorData`, `updateRecordingState`) are
  also called from the consumer pages for telemetry. [I] No QML instantiates the flow's container, so
  it is presumably opened from C++ under a condition not reachable in normal use.
- [C] Battery: CCCD 18 notify is enabled on every connect; an explicit read of handle 17 is only done on
  request (`request battery level`, 0x504C80). No polling timer in libmyo.
- [C] RSSI: `connection_get_rssi` on request only (0x511630 -> 0x515B80); result goes up as an RSSI
  event (0x5069E0 case 5 -> 0x504860). The advertisement RSSI is only used for the -39 dBm pairing gate.

---

## 4. USB side (UsbDetector)

### 4.1 What is watched [C]

`UsbDetector` (vtable 0xFE2BAC; Qt signals: detectedUsbMyoChanged(detected, macAddress,
firmwareVersion), detectedUsbFirmwareMismatch(fromVid, fromPid, toVid, toPid),
detectedFirmwareUpdateSuccessMyo, detectedFirmwareUpdateFailureMyo, lostFirmwareUpdateMyo; slots
detectUsbMyo, stopDetectingUsbMyo; property hasDetectedUsbMyo). It uses
`Usb_FindMassStorageDriveByVidPid` (0x4A92A0): enumerate disk interfaces, CM_Get_Parent +
CM_Get_Device_ID, match `VID_xxxx&PID_xxxx` in the parent USB device id, match storage device number
(IOCTL via 0x4A9A80) against every logical drive from GetLogicalDriveStrings, return the drive root.

| Globals | VID:PID | Device | Handler |
|---------|---------|--------|---------|
| 0x108F294/0x108F298 | **29FF:0001** | Myo armband (application firmware) as USB Mass Storage | `Usb_Detector_pollMyoDrive` 0x456D50, `Usb_Detector_pollFirmwareUpdateResult` 0x4568B0 |
| 0x108F28C/0x108F290 | **15A2:0200** | Freescale (Kinetis) bootloader mass-storage device | `Usb_Detector_pollBootloaderDrive` 0x456C20 |
| 0x10913B0/0x10913B4 | 2458:0001 | BLED112 dongle (COM port) | BluetoothDongle, section 1.1 |

The Myo exposes **only a mass-storage interface** to the app; there is no HID/vendor/CDC data interface
and no libusb/WinUSB code in the executable (no libusb/WinUsb imports or strings). [C]

### 4.2 Myo plugged in (29FF:0001) [C]

`Usb_Detector_pollMyoDrive` (timer driven):
1. Find the drive, list root with name filter `????????.??` (hidden files included).
2. Read `<drive>/INFO.TXT` (`Usb_ReadInfoTxtJson`, 0x456680): whole file as UTF-8 JSON object.
   Keys referenced by the app: `mac_address` (0xFE2C24), `hardware_revision` (0xFE2C04), firmware version
   fields. [I] Layout (from firmware strings):
   `{"version": {"major", "minor", "patch", "hardware_revision"}, "hardware": {"mac_address", "imu_calibrated"}}`.
3. Emit detectedUsbMyoChanged(true, mac, firmwareVersion). The guide page stores the MAC
   (setMyoMacAddress) and asks `firmwareUpdater.canUpdate(mac, firmwareVersion)`.
4. Drive gone -> detectedUsbMyoChanged(false, "", {}).

[I] Charging is not detected by the app; USB presence is used only for setup (MAC for pairing UI) and
firmware update.

### 4.3 Bootloader drive (15A2:0200) [C]

`Usb_Detector_pollBootloaderDrive`: when the Freescale bootloader drive appears it emits
detectedUsbFirmwareMismatch(0x15A2, 0x0200, 0x29FF, 0x0001). The UI (0x420610) shows
"Update Myo armband firmware." and opens
`https://getmyo.com/firmware/update?fromusb=15a2.0200&tousb=29ff.0001` (each value "%1.%2" as 4-digit
hex). The app itself does not program a device sitting in this bootloader.

---

## 5. FirmwareUpdater (how firmware update is really done)

**Firmware update = copy a file to the Myo's USB mass-storage drive.** No BLE OTA and no USB DFU is
performed by Myo Connect. [C]

1. Bundled images: `Resources/firmware/*.2.hex` are copied at start (0x458880) into the per-user
   `firmware/` directory if newer than what is there ("Cannot create firmware file directory." on
   failure). File name = version `major.minor.patch.hwrev.hex` (parsed by 0x474AF0; `.2` = hardware
   revision 2, Rev D). The newest cached version is also kept in QSettings (`revDFirmwareVersion`,
   "Could not load cached Rev D Firmware Version.", 0x457620).
2. canUpdate(mac, version): true if newest cached (major, minor, patch) > version from INFO.TXT
   (compare 0x474D70; hwrev not compared).
3. update() (slot 0x425C10):
   1. refuse if already updating (+40);
   2. src = `<firmware dir>/<newest>.hex` (0x459040), dst = Myo drive root (UsbDetector);
   3. set updating, emit updateStarted;
   4. run `Usb_FirmwareUpdater_copyHexToMyoDrive` (0x425850) in QtConcurrent:
      `QFile::copy(src, <drive>/file.hex)` (literal "file.hex", 0xFE0364);
   5. UsbDetector enters result-watch mode (0x456640) and a timeout QTimer is started.
4. Result detection (`Usb_Detector_pollFirmwareUpdateResult`, 0x4568B0):
   - drive vanished -> lostFirmwareUpdateMyo;
   - any `*.hex` still in the root -> still flashing, wait;
   - `SUCCESS.TXT` present -> read INFO.TXT, emit detectedFirmwareUpdateSuccessMyo(new version);
   - failure file present -> detectedFirmwareUpdateFailureMyo. Note: the app looks for the literal
     **"FFAILED.TXT"** (0xFE2C50, length 11), while firmware strings say FAILED.TXT; [I] failure is
     therefore probably only noticed through the timeout / lost-device path.
5. [I] The device side (firmware strings: STARTED.TXT/READY.TXT/SUCCESS.TXT/FAILED.TXT, OTA flash swap)
   flashes the copied S-record file, reboots and re-enumerates.

The .hex files are **Motorola S-records** (S0 header "myo-core-application-REV_D-no-checksum.h", S2
24-bit records, S8 entry), not Intel HEX. The app never parses them; it copies them verbatim.
No BLE command is needed to enter update mode (plugging the Myo into USB exposes the drive).

drivers/dfu.inf (VID 2458 PID FFFE, WinUSB "libusb 1.0") is the Bluegiga BLED112 **dongle** DFU
bootloader driver. [I] It is shipped for dongle firmware upgrades by an external tool; Myo Connect contains
no code for it.

### 5.1 .hex region map (Kinetis application image) [C]

| File | Ranges (absolute flash address) | Entry (S8) |
|------|---------------------------------|------------|
| Resources/firmware/1.5.1931.2.hex | 0x10000-0x1018F, 0x101F8-0x10273, 0x10410-0x69271 | 0x0001226D |
| Firmware/myo-firmware-1.5.1970.hex | 0x10000-0x1018F, 0x101F8-0x10273, 0x10410-0x753B1 | 0x0001226D |
| Firmware/myo-firmware-1.1.5-revd.hex | 0x10000-0x1018F, 0x101F8-0x1026F, 0x10410-0x56281 | 0x0001226D |

Vector table at 0x10000 (initial SP 0x20010000, reset 0x1226D); 0x10400-0x1040F (Kinetis flash
configuration field position relative to the app base) is left out. Everything below 0x10000 is the
resident bootloader (not in the files). [I] The embedded nRF51822 image is contained inside this
Kinetis image and programmed by the Kinetis over SWD (per firmware analysis, separate instance).

---

## 6. Checklist for a C# / ESP32 central

1. Scan actively; match AD type 0x06 containing UUID d5060001-a904-deb9-4748-2c7f4a124842 (also accept
   0x07 for robustness). For "pairing" emulate the RSSI >= -39 dBm gate if desired.
2. Connect: public address, interval 7.5 ms (min = max = 6), latency 0, supervision timeout 640 ms.
   No pairing/bonding/encryption, keep MTU 23.
3. Read 23 (check major == 1, minor >= 5), 21, 3.
4. Write CCCD 18 = 01 00, data CCCDs (notify 01 00 or indicate 02 00 per fw info byte 11; motion,
   classifier, log always 02 00), then the set_mode command on 25 (write with response).
5. Keep-alive is not required; reconnect by re-issuing the connect whenever the link drops.

---

# Part D. IMU usage


Source: "Myo Connect.exe" (IDA instance qntp, imagebase 0x400000). All addresses are VAs in that
image. Functions analysed in this pass were renamed in the IDB with the prefix `Imu_`.

Legend: [C] = confirmed from decompilation/disassembly, [I] = inferred (naming, usage, intent).

## 0. Data flow and conventions

- libmyo-like hub callbacks feed the Qt object `QMyo` (metaobject 0x108F2D8). Signal indices:
  3 armSynced(ts, arm, xDirection, rotation(float), warmupState), 4 armUnsynced, 7 poseChanged(ts, pose),
  8 orientationReceived(ts, QQuaternion), 9 accelerometerReceived(ts, QVector3D),
  10 linearAccelerometerReceived(ts, QVector3D), 11 gyroscopeReceived(ts, QVector3D). [C]
- QMyo fields: +36 current pose, +40 arm, +44 xDirection, +48 armSynced(bool), +92 warmupState,
  +100 rotation (float from arm-synced event). Setter: 0x470390. [C] (+36 = pose: [I], set together
  with poseTypeChanged in 0x4737A0.)
- Enums (libmyo numbering) [C from Lua getters 0x49D2B0 / 0x49D520 and usage]:
  - arm: 0 = right, 1 = left (2 = unknown).
  - xDirection: 0 = towardWrist, 1 = towardElbow.
  - pose: 0 rest, 1 fist, 2 waveIn, 3 waveOut, 4 fingersSpread, 5 doubleTap, 0xFFFF unknown.
- Units: quaternion unit-norm; accel in g; gyro in deg/s (converted to rad/s only in the mouse mover). [C]
- Orientation reorder: hub delivers x,y,z,w; QMyo emits QQuaternion (memory: w,x,y,z) (0x473730);
  every consumer reorders back to a float[4] = {x, y, z, w} before math. All formulas below use the
  true components w,x,y,z. [C]
- IMU samples arrive at 50 Hz by default (Part A section 6.2); nothing in the consumers uses timestamps,
  all detectors are per-sample. [C]

## 1. Orientation math

### 1.1 Euler angles (shared helpers) [C]

```
roll  = atan2( 2*(w*x + y*z), 1 - 2*(x*x + y*y) )          // Imu_QuatRoll  0x478BC0
pitch = -asin( 2*(w*y - x*z) )                              // Imu_QuatPitch 0x478B70 (no clamp!)
yaw   = -atan2( 2*(w*z + x*y), 1 - 2*(y*y + z*z) )          // Imu_QuatYaw   0x478C50
```

Notes:
- These are the standard aerospace ZYX Euler formulas with pitch and yaw sign-flipped (libmyo SDK
  sample uses +asin and +atan2). The asin argument is NOT clamped to [-1,1]; clamp it in a port.
- Result range: roll and yaw in (-pi, pi], pitch in [-pi/2, pi/2]. Radians everywhere.
- Wrap-around: `Imu_AngleDiffWrapPi` 0x4792C0: `d = b - a; if d > pi: d -= 2pi; if d < -pi: d += 2pi`.
  Used by fist-twist for per-sample roll deltas. The Lua knob scripts do the same in degrees
  (`degreeDiff`, +-180). [C]

### 1.2 Effect of arm / xDirection / rotation

- xDirection == towardElbow (1): roll and pitch are negated. Done in Lua getRoll/getPitch
  (0x49B6C0 / 0x49A420, script field +208 == 1), in FistPullDown and PitchMapping (QMyo+44 == 1),
  and in FistTwist on the roll delta. Yaw is never negated. [C]
- arm (left/right) does NOT change the angle math. It only selects the asymmetric fist-twist
  thresholds (sec. 3.1) and is exposed to Lua (scripts swap waveIn/waveOut for left arm). [C]
- The arm-synced `rotation` value (u16 milliradians, stored at QMyo+100) is not read by any of the
  IMU consumers analysed here (detectors, pointer mover, Lua getters). [C for those functions;
  I that it is unused elsewhere.]
- Mouse mover: the forearm axis in the device frame is +X if xDirection == towardWrist, else -X
  (flag at AbstractPointerController+76, set in lambda 0x414020 on armSynced, cleared on
  armUnsynced 0x414070). [C]

### 1.3 Zeroing / centering

- There is no global reference orientation. Everything is either absolute Euler (PitchMapping,
  FistPullDown, Lua getters) or relative to the pose start:
  - FistTwist stores the quaternion when fist starts and then integrates per-sample roll deltas.
  - FistPullDown stores the pitch at fist start as `start`.
  - Lua knob scripts store `rollReference = deg(getRoll())` at fist on-edge.
- Mouse pointer is gyro-rate based (no absolute orientation reference); `centerMousePosition` /
  `pointerMover.center()` just put the cursor at normalized (0.5, 0.5). [C]

### 1.4 World-frame helpers used by Lua [C]

- `getOrientationWorld` 0x49D310: returns 3 numbers = q * (s,0,0) * conj(q), s = +1 towardWrist,
  -1 towardElbow, i.e. the forearm direction as a world-frame unit vector.
- `getAccelWorld` 0x49ADB0: q * (ax,ay,az) * conj(q) (raw accel incl. gravity, rotated to world).

## 2. linearAccelerometerReceived (gravity removal) [C]

`Imu_QMyo_EmitLinearAccel` 0x46FE00, called right after accelerometerReceived from 0x472E80:

```
g = 0.8*g + 0.2*a        // per-axis exponential low-pass, state in QMyo+80..+88, init 0
lin = a - g              // emitted as linearAccelerometerReceived (units g)
```
It is a plain first-order high-pass in the device frame (alpha 0.2 at 50 Hz, corner about 1.8 Hz);
no quaternion is used. Consumers of linearAccel were not found among the detectors.

## 3. Gesture detectors

Common base (AdvancedGestureDetector / QObject): +8/+12 QPointer<QMyo>, +16.. connection list,
+28 enabled (ctor sets 1). Detectors connect themselves to QMyo signals in their `setMyo` virtual
(e.g. 0x4177D0 FistTwist, 0x417190 FistPullDown, 0x418050 PitchMapping, 0x418570 PoseHold,
0x417BB0 Movement). AdvancedGestureInformer signals (metaobject 0x550338): 0 began, 1 cancelled,
2 detected, 3 progressChanged(float). AdvancedGestureProvider (0x108E024) re-emits them with
AdvancedGestureType {FistTwistGesture=0, PoseHoldGesture=1} and the Myo MAC for QML. [C]

### 3.1 FistTwistGestureDetector (ctor 0x4173B0) [C]

Fields: +32 allowLeft=1, +33 allowRight=1, +34 active, +36 last pose, +40 current quat (x,y,z,w),
+56 previous quat, +72 progress, +76 gyro vec (deg/s).
Signals: fistTwistLeftGestureDetected, fistTwistRightGestureDetected + Informer signals.

onPose 0x417770:
```
if lastPose == fist and active: emit cancelled; active = false
if pose == fist: prevQ = curQ; progress = 0; active = true; emit began
lastPose = pose
```
onGyroscope 0x417560: store gyro vector.
onOrientation 0x417580 (every sample while active and prevQ != 0):
```
ratio  = sqrt(gy^2 + gz^2) / |g|                 // off-axis share of rotation (X = forearm axis)
scale  = ratio >= 1 ? 0.25 : ratio >= 0.5 ? 1.75 - 1.5*ratio : 1.0     // 0x417460
d      = wrapPi(roll(curQ) - roll(prevQ))
if xDirection == towardElbow: d = -d
d     *= scale
// asymmetric per-direction angle needed for full progress (0x417900):
if arm == left: negDiv = 0.15, posDiv = 0.25  else: negDiv = 0.25, posDiv = 0.15   (radians)
progress += d >= 0 ? d/posDiv : d/negDiv
progress  = clamp(progress, allowLeft ? -1 : 0, allowRight ? 1 : 0)
emit progressChanged(progress)
if progress <= -1: emit fistTwistLeft; emit detected; active = false
if progress >=  1: emit fistTwistRight; emit detected; active = false
prevQ = curQ
```
So a twist of 0.15 rad (8.6 deg) in the "easy" direction or 0.25 rad (14.3 deg) in the other,
integrated while holding a fist, triggers. Off-axis wrist/arm motion attenuates the integration
(down to x0.25) to reject whole-arm swings. No time limit, no hysteresis other than the reset on
fist release.
Used by: PresentationController (0x414D70, this+52) and GuideWindow (`fistTwistDetector`,
0x464110). UI meaning [I from QML 00163C59/00170189]: fist + clockwise twist = zoom in,
fist + counter-clockwise twist = turn on presentation pointer.

### 3.2 FistPullDownGestureDetector (ctor 0x416AB0) [C]

Fields: +32 last pose, +36 startPitch, +40 current pitch p, +44 QList<float> of per-sample pitch
deltas (max 50), +48 canStart, +49 active, +50 isReversed (Q_PROPERTY).
Signals: 0 canStartChanged(bool), 1 started, 2 updated(float progress), 3 completed(bool success),
4 isReversedChanged.

onOrientation 0x416DF0:
```
p = pitch(q); if xDirection == towardElbow: p = -p
delta = p - p_prev; history.append(delta); keep last 50
if !active and !isReversed:
    if canStart and p <= 0.8:            { canStart = false; emit canStartChanged(false) }
    elif !canStart and p > 0.88 and pose == rest: { canStart = true; emit canStartChanged(true) }
if active:
    progress = isReversed ? (p - 0.5)/(startPitch - 0.5)
                          : 1 - (p - 0.3)/(startPitch - 0.3)
    emit updated(progress)
```
onPose 0x416FA0 (first calls HandlePoseEdge(lastPose, false) 0x417040 for the pose that ended):
```
// pose ended:
if lastPose == fist and active:
    predicted = mean(history)*20 + p          // mean per-sample delta extrapolated 20 samples (0.4 s)
    success = isReversed ? predicted > 0.2 : predicted < 0.5
    emit completed(success); reset (active=canStart=false, start=0, history clear)
// new pose:
if pose == fist and (isReversed ? p < 0.5 : p > 0.8):
    emit started; active = true; canStart = false; startPitch = p
elif pose == rest and !canStart and !isReversed and p > 0.88: emit canStartChanged(true)
lastPose = pose
```
Angles: 0.88 rad = 50 deg (arm raised), 0.8 rad = 46 deg (hysteresis), 0.3 rad = 17 deg (progress
1), 0.5 rad = 29 deg. Used by AppMenuController 0x45C5B0 (+296) and GuideWindow. UI [I from QML]:
"Raise arm, hold fist, pull down" opens the Quick Launch menu; `pullUp`/isReversed = hold fist
and raise arm to close it.

### 3.3 PitchMappingGestureDetector (ctor 0x417CD0) [C]

onOrientation 0x417E70:
```
p = pitch(q); if xDirection == towardElbow: p = -p
hist.append(p); keep last 35                  // 0.7 s at 50 Hz
emit pitchMappingUpdated(mean(hist) + 0.4)
emit absolutePitchMappingUpdated(p + 0.4)
```
onPose 0x418030: pose == fingersSpread (4) -> emit pitchMappingDetectedSelection.
Used by AppMenuController and GuideWindow: arm pitch scrolls through menu entries, spread fingers
selects [I].

### 3.4 MovementGestureDetector (ctor 0x417A70) [C]

onGyroscope 0x417B40: `moving = sqrt(gy^2 + gz^2) > 3.0` (deg/s, off-axis rate only, forearm
twist ignored); emits movementGestureDetectorMovingChanged on every change. No filtering or
hysteresis. Used by KeyboardController 0x460290 (+192) [I: pauses/dwell for on-screen keyboard].

### 3.5 PoseHoldGestureDetector (ctor 0x418190(pose, intervalMs)) [C]

Two repeating QTimers: hold timer = interval, progress timer = interval/3.
onPose 0x418460: if lastPose == target: Cancel (0x418500: if hold timer still running emit
cancelled; stop both; progress = 0). If pose == target: start both timers, emit began.
Progress tick 0x4184E0: progress += 1/3, emit progressChanged. Hold timeout: emit detected.
(Timers are not single-shot, so detected/progress keep firing while the pose is held [C]; UI
probably ignores repeats [I].) PresentationController uses PoseHold(pose=2 waveIn, 1000 ms).

## 4. Pointer / mouse

Class layout (AbstractPointerController ctor 0x413BE0): +32 active, +36 IMouseMoverParameterSource*,
+40 MouseMover struct {+0 paramSrc, +4 dx, +8 dy, +12 remX, +16 remY, +20 quat x,y,z,w (init 0,0,0,1),
+36 towardWrist flag}. Slots: orientation lambda 0x413FA0 stores quat (0x4784C0); gyro lambda
0x4140B0 -> 0x413820: `MouseMover::onGyro(g)`; if active, virtual[13](dx, dy) and emit
positionUpdated. [C]

### 4.1 MouseMover::onGyro 0x477EC0 [C]

```
w_dev   = gyro_deg_s * pi/180                                // device-frame angular rate
w_world = q * w_dev * conj(q)                                // rotate to world (z = up [I])
a       = q * (s,0,0) * conj(q),  s = towardWrist ? +1 : -1  // forearm axis in world
h       = (-a.y, a.x, 0)                                     // z x a: horizontal, perpendicular to arm
r       = shortestArc(h -> (0,1,0)), normalized              // 0x477C20
u       = -w_world.z                                         // yaw rate about vertical  -> X
v       = (r * w_world * conj(r)).y  ( = dot(w_world, h/|h|) ) // pitch rate about h     -> Y
ApplyGainCurve(u, v)
```
Result: horizontal cursor speed = arm yaw rate, vertical = arm pitch rate, independent of forearm
roll and of how the band is rotated on the arm. Sign check: arm pointing +X world, raising the
hand gives v < 0 (cursor up, screen y down-positive); turning left (+Z rotation) gives u < 0.
No deadzone, no low-pass on the rate. [C math, I screen-direction interpretation]

### 4.2 Gain curve 0x4784E0 (sigmoid pointer acceleration) [C]

```
speed = sqrt(u*u + v*v)                          // rad/s
A = clamp(paramSrc.acceleration(), 0, 1)         // vtbl[0]; prefs "mouseAcceleration", default 0.3
S = clamp(paramSrc.sensitivity(), 0, 1)          // vtbl[1]; prefs "mouseSensitivity",  default 0.5
k   = 1.3481359*(1-A) + 1.68517*A                // sigmoid steepness (1/(rad/s))
mid = (0.4*S + 0.7*(1-S)) * 2.96706 + 0.1745329  // sigmoid centre, rad/s (2.967 = 170 deg, 0.1745 = 10 deg)
gain = 0.83 * (58.280807 + 8688.874 / (1 + exp(-k*(speed - mid))))
dx = gain * u / 60 ;  dy = gain * v / 60         // pixels (or pointer units) per IMU sample
// sub-pixel carry:
remX += frac(dx); dx = trunc(dx); if |remX| > 1: dx += trunc(remX); remX -= trunc(remX)   (same for y)
```
Defaults (A=0.3, S=0.5): k = 1.449, mid = 1.806 rad/s. Speed/gain table at 50 Hz:
0.1 rad/s -> 1.0 px/sample (51 px/s); 0.25 -> 3.1 (153 px/s); 0.5 -> 8.3 (413 px/s);
1.0 -> 29 (1465 px/s); 2.0 -> 139 (6930 px/s). Gain range 539 (slow) .. 7260 (fast).
Note the fixed /60 even though IMU runs at 50 Hz.
- PreferencesMouseMoverParameterSource: vtbl 0x4684D0 -> Imu_Prefs_GetMouseAcceleration 0x4434F0,
  0x4684E0 -> Imu_Prefs_GetMouseSensitivity 0x4435B0 (QSettings). [C]
- FixedAccelMouseMoverParameterSource (KeyboardController 0x460290): acceleration = member
  initialised to 0.0, sensitivity from prefs. [C]

### 4.3 Controllers (virtual[13] = move(dx,dy), virtual[14] = setNormalized(x,y)) [C]

- MousePointerController: move -> `this+80` OS mouse object vtbl+8(dx,dy) (relative move);
  setNormalized 0x414620: x_px = lerp(0, GetSystemMetrics(SM_CXSCREEN), x), same for y (primary
  screen). center = setNormalized(0.5, 0.5) (0x413F80).
- PositionPointerController (ctor 0x4147F0): internal rect (0,0)-(1000,1000); move adds delta and
  clamps (0x478880); x/y properties = pos/1000 (QML `pointerMover`).
- DeltaPositionPointerController: same as Position but also stores the last delta.
- OverlayPointerController 0x414760: presentation overlay; adds dx/1000, dy/1000 to the overlay
  object's normalized coordinates.

## 5. Lua scripting API (IMU related)

Registration in 0x49D760 (std::function lambdas). Script host object fields: +156 quat (x,y,z,w,
raw from orientationReceived, 0x49FC20), +172 accel (raw g, 0x49F810), +184 gyro (deg/s, 0x49F910),
+200 armKnown, +204 arm, +208 xDirection (0x49F2E0 on armSynced), +220/+224/+228/+232 pointer and
scroll controllers, +236 mouseControlEnabled. onPeriodic is driven by a 10 ms QTimer (0x447F60). [C]

| Lua | impl | returns |
|---|---|---|
| getRoll | 0x49B6C0 | roll(q), negated if towardElbow |
| getPitch | 0x49A420 | pitch(q) = -asin(..), negated if towardElbow |
| getYaw | 0x49B870 | yaw(q) = -atan2(..), never negated |
| getAccel | 0x49B5E0 | ax, ay, az raw (g, incl. gravity) |
| getAccelWorld | 0x49ADB0 | q*a*conj(q) |
| getGyro | 0x49AD40 | gx, gy, gz raw deg/s |
| getOrientationWorld | 0x49D310 | forearm axis in world (x-axis * +-1) |
| getArm | 0x49D2B0 | "right"/"left"/"unknown" |
| getXDirection | 0x49D520 | "towardWrist"/"towardElbow"/"unknown" |
| controlMouse(bool) | 0x49CE30 | enables mouse pointer controller (+232 vtbl+12) |
| centerMousePosition | 0x49A3A0 | pointer vtbl+20 = center() |
| mouseScrollBy(n) | 0x49F190 | scroll controller (+224) vtbl+16(n) |
| mouseControlEnabled | 0x49A750 | bool +236 |

On armUnsynced (0x49F300) the host disables mouse control, centres/zeros the presentation pointer
and clears armKnown. [C]

Virtual knob in shipped scripts (Spotify/iTunes/VLC/Netflix .myo): on fist on-edge store
`rollReference = deg(getRoll())`, `volumeSince = now`, unlock hold. In onPeriodic (10 ms):
`rel = wrap180(deg(getRoll()) - rollReference)`; if |rel| > 34 deg repeat volume up/down every
100 ms; elif |rel| > 11.5 deg repeat every 330 ms; sign of rel = direction (positive = up). Reference
is NOT re-zeroed after each step (rate control, not position control). [C from scripts]

## 6. Recommendations for our C# / ESP32 libraries

All angles in radians unless stated. q = (w,x,y,z) from the IMU packet (/16384), normalise it.

### 6.1 Common helpers
```
Roll(q)  = atan2(2(wx+yz), 1-2(x^2+y^2))
Pitch(q) = -asin(clamp(2(wy-xz), -1, 1))
Yaw(q)   = -atan2(2(wz+xy), 1-2(y^2+z^2))
if xDirection == TowardElbow: Roll = -Roll; Pitch = -Pitch
Wrap(a) = a > pi ? a-2pi : a < -pi ? a+2pi : a
```

### 6.2 (a) Joystick axes from arm orientation (derived, position control)
```
on engage (fist on-edge or explicit centre): ref = {Yaw, Pitch, Roll}
each sample:
  dYaw   = Wrap(Yaw - ref.yaw);  dPitch = Pitch - ref.pitch;  dRoll = Wrap(Roll - ref.roll)
  X = shape(dYaw,   dz=3 deg, full=30 deg)     // left/right
  Y = shape(dPitch, dz=3 deg, full=25 deg)     // up/down
  Z = shape(dRoll,  dz=11.5 deg, full=34 deg)  // twist axis, knob thresholds from scripts
shape(a, dz, full) = sign(a) * clamp((|a|-dz)/(full-dz), 0, 1)
optional smoothing: moving average of 35 samples (PitchMapping) or 1-pole alpha 0.2
```
Deadzone/full-scale values are our proposal; the app itself has no absolute joystick mode (it
uses absolute pitch only for menu mapping: mean of 35 samples + 0.4 rad offset). Yaw drifts
(no magnetometer), so re-reference on every engage as the app does for twist and knob.
Alternative drift-free rate mode: feed MouseMover u,v (6.4) into an integrator with leak.

### 6.3 (b) Fist-twist volume knob
Option 1 (as shipped scripts, rate control, recommended):
```
on fist on:  ref = deg(Roll); since = now; active = true
on fist off: active = false
every 10 ms while active:
  rel = wrap180(deg(Roll) - ref)
  if |rel| > 34   and now - since > 100: step(sign(rel)); since = now
  elif |rel| > 11.5 and now - since > 330: step(sign(rel)); since = now
```
Option 2 (detent/incremental, from FistTwistGestureDetector): integrate per-sample
`d = Wrap(roll_k - roll_{k-1}) * scale(offAxis)` (sign flipped for towardElbow), emit one step each
time the accumulator passes +-1 using divisors 0.15 rad / 0.25 rad (swap by arm), then subtract 1
(the app resets and stops instead; for a knob keep going). scale(r) with
r = sqrt(gy^2+gz^2)/|g|: 1 for r<0.5, 1.75-1.5r for 0.5..1, 0.25 above.
Positive direction: roll increasing (after towardElbow fix) = volume up, matching scripts.

### 6.4 (c) Mouse pointer (exact port of 0x477EC0 + 0x4784E0)
```
per IMU sample (50 Hz):
  wDev   = gyro_deg * pi/180
  wWorld = rotate(q, wDev)
  a      = rotate(q, (towardWrist ? 1 : -1, 0, 0))
  h      = normalize(-a.y, a.x, 0)       // guard |h| ~ 0 (arm vertical): skip v or keep last h
  u = -wWorld.z ; v = dot(wWorld, h)
  speed = hypot(u, v)
  k   = 1.3481359 + (1.68517-1.3481359)*A          // A in [0,1], default 0.3
  mid = 2.96706*(0.7 - 0.3*S) + 0.1745329          // S in [0,1], default 0.5
  gain = 0.83*(58.280807 + 8688.874/(1 + exp(-k*(speed-mid))))
  fx = gain*u/60 + remX ; fy = gain*v/60 + remY
  dx = trunc(fx); dy = trunc(fy); remX = fx - dx; remY = fy - dy
  moveRelative(dx, dy)            // screen y down-positive: raising arm -> v<0 -> cursor up
centre: set cursor to (0.5, 0.5) of the screen on enable
```
On ESP32 (BLE HID mouse) clamp dx,dy to int8 per report and carry the excess. For a different
sample rate replace /60 by sampleRate*1.2 to keep the same feel (app: 50 Hz with /60). Optional
tiny deadzone (for example speed < 0.02 rad/s -> 0) to stop gyro-noise creep; the app has none.

### 6.5 Other gestures worth porting
- Menu pull: FistPullDown thresholds (start pitch > 0.8 rad with fist, rest-pose arming at
  > 0.88 rad, success if pitch + 20*meanDelta < 0.5 rad at fist release).
- "Moving" flag: sqrt(gy^2+gz^2) > 3 deg/s.
- Pose hold: 1000 ms with 3 progress ticks.
- Linear accel: g = 0.8g + 0.2a; lin = a - g.

---

# Part E. Custom classifier training


Source: Myo Connect.exe (IDA instance "qntp", imagebase 0x400000). All functions named
below with the prefix `Train_` were renamed in the IDB by this analysis.

Legend: [C] = confirmed from code/QML, [I] = inferred (plausible, not proven by code).

## 1. Components and entry points

| Address | Name (IDB) | Role |
|---|---|---|
| 0x40FE70 / 0x40FFF0 | moc Trainer qt_metacall / qt_static_metacall | Qt glue for class Trainer |
| 0x454FD0 | Train_Trainer_createCustomProfile(uint64 mac) | opens TrainingWindow (qrc:/trainingwindow.qml), registers Trainer as a stream-config requester |
| 0x4553C0 | Train_Trainer_startTraining(uint64 mac) | libmyo training dataset create (Trainer+60 = dataset) |
| 6 (inline in 0x40FFF0) | Trainer::stopTraining() | frees dataset (0x501220), re-evaluates stream config (0x43D230) |
| 0x454EF0 | Train_Trainer_collectData(pose) | runs 0x454F40 in QtConcurrent (VoidStoredMemberFunctionPointerCall2<Trainer, void*, void*, libmyo_pose_t>) |
| 0x454F40 | Train_Trainer_collectDataWorker | calls libmyo collect (0x500D30) then emits dataCollected(pose) |
| 0x455160 | Train_Trainer_progressCallback | collectionProgress (Trainer+64, double) = progressByte / 255.0; emits collectionProgressChanged |
| 0x454F70 | Train_Trainer_createAndLoadModel | train model (0x5014C0), upload (0x500770, slot 0), activate custom classifier (0x470780(1)), emit classifierChanged |
| 0x455270 | Train_Trainer_sendCollectedData | annotate appId / softwareVersion / uuid, POST collected data as JSON to Thalmic (telemetry) |
| 4 (inline) | Trainer::closeTrainerWindow() | closes the window |

Trainer Qt meta methods (from moc data at 0x5562a0, confirmed [C]):
`0 dataCollected(pose)` signal, `1 collectionProgressChanged()` signal, `2 classifierChanged()` signal,
`3 createCustomProfile(uint64_t macAddress)`, `4 closeTrainerWindow()`, `5 startTraining(uint64_t macAddress)`,
`6 stopTraining()`, `7 collectData(pose)`, `8 createAndLoadModel()`, `9 sendCollectedData()`,
property `collectionProgress` (double).

libmyo C-API layer (names inferred from their error strings and the public libmyo naming style [I]):

| Address | Inferred API | Notes |
|---|---|---|
| 0x500F50 | libmyo_training_create_dataset(myo, out_dataset, err) | "Unable to create training dataset, Myo has been rejected"; new(72) -> 0x5091F0 |
| 0x500D30 | libmyo_training_collect_data(dataset, pose, cb, user, err) | -> 0x509E40 |
| 0x5014C0 | libmyo_training_train_model(dataset, out_model, err) | -> 0x50C9E0, returns new(7696) blob |
| 0x500770 | libmyo_training_load_model(myo, slot, model, err) | "model must not be NULL"; -> 0x502310 |
| 0x5009D0 | libmyo_training_annotate_training_data(dataset, name<=80, value<=255, err) | |
| 0x501240 | libmyo_training_send_training_data(dataset, err) | JSON ("application/json") via 0x50B8F0 |
| 0x501220 | libmyo_training_free_dataset(dataset) | |
| 0x4FEA60 | libmyo_set_active_classifier(myo, type, index, err) | -> 0x505310 |

Qt "Myo" object (meta object 0x108F2D8): `42 classifierType()` (reads MyoDevice+52), `43 setClassifier(type)` =
0x470780 (calls libmyo_set_active_classifier(myo, type, 0) and emits `27 classifierTypeChanged`).

## 2. User-facing flow (QML) [C]

The training wizard QML is zlib-compressed inside the qrc (the earlier extraction missed it). All 76
compressed QML resources were decompressed to `MyoConnect/extracted/zlib/<fileoffset>.qml`. Relevant files:
`0016E6A9.qml` (trainingwindow), `00177D5C.qml` (trainingrecording), `00166763.qml` (trainingplayground),
`00180985.qml` (armband manager, Calibration tab), `001674AE.qml` (popup with "http://customprofile" link).
Uncompressed pages already in `MyoConnect/extracted`: `00176753.qml` intro, `0017FC70.qml` arm placement,
`001843E3.qml` writing profile, `00179A6F.qml` sync gesture.

Entry: Armband Manager > Calibration tab > "Create a Profile" (`trainer.createCustomProfile(mac)`), or the guide popup
link. Page order (trainingwindow.qml `qmlPages`):

1. trainingintro: "Please make sure you've worn Myo for about 2 minutes before creating a calibration profile."
   (warm-up is only a textual recommendation; no code check of the warm-up state in the wizard).
2. trainingarmplacement: choose arm (Left/Right) from a dropdown (required), "Myo logo must face up",
   "must be worn in the exact same position as when the profile was generated". USB direction recorded as
   "towards wrist" (telemetry only). On Continue: `recorder.recordArmPlacementEvent(...)`,
   `trainer.startTraining(myo.macAddress())`.
3. trainingrecording: gestures in this UI order: Fist, FingersSpread, WaveIn, WaveOut, Rest.
   For each: prompt + looping video; user presses SPACE -> `trainer.collectData(gesture)`; the video jumps to
   a per-gesture start frame (Fist 8, FingersSpread 11, WaveIn 8, WaveOut 14, Rest 0) and freezes at the
   hold frame (38, 41, 36, 44, 0). A (hidden) progress bar documents intended timing: first 1/3 of the
   recording = "REST", remaining 2/3 = "GESTURE" (the user relaxes first, then performs and holds the pose).
   When `dataCollected` fires: "Recorded!" with "Do it again" (re-collects same pose; old samples replaced)
   or "Continue". After the 5th gesture: `trainer.createAndLoadModel()`, `trainer.sendCollectedData()`.
4. trainingwritingprofile: fixed 12000 ms timer ("Please wait while we create the calibration profile...").
   The upload itself is synchronous inside createAndLoadModel; the 12 s is just UI delay [C] (maybe to let the
   firmware finish writing flash [I]).
5. trainingsyncgesture: on show `trainer.stopTraining()`; waits for `myo.onArmSynced` (user performs the sync gesture
   with the new classifier active).
6. trainingplayground: live pose display to test; "Re-calibrate" goes back to trainingarmplacement;
   "Complete Profile" -> `trainer.closeTrainerWindow()`.

Selecting profiles later: Calibration tab buttons call `setClassifier(mac, ClassifierTypes.BuiltIn|Custom)`;
the "Custom Profile" button is visible only if `itemHasCustomClassifier` (fw info byte +10).

## 3. Streaming configuration during training [C]

Trainer implements a stream-config requester interface (secondary vtable 0xFE2B28, arbitrated by 0x43D230, which picks
the highest-priority requester):

| Slot | Impl | Value |
|---|---|---|
| priority | 0x455230 | 3 if the Myo is the training Myo, else 0 |
| EMG mode | 0x465410 | 5 = RmsMpf (wire emg mode 5: feature vector handle 39 + MPF on handle 43) |
| EMG rate | 0x50A190 | 0 (default, no set_mode_ext) |
| IMU mode | 0x4551D0 | 3 (data + events) |
| slot +16 | 0x455210 | false while the training window exists (-> 0x470C10 / 0x4FC5A0; unlock/sleep related [I]) |
| classifier enabled | 0x4551E0 | false while window + dataset exist -> set classifier mode off (0x4FBBA0 -> MyoDevice+92, cls byte in set_mode) |

So during recording the host requests `set_mode [01 03 05 03 00]` (EMG=5, IMU=3, classifier off) [I: exact
bytes derived from 0x5024F0 logic], and CCCDs 40 and 44 are enabled. Only handle 39 (feature vector) is used
for training; MPF is not used.

## 4. Data collection [C]

Handle 39 notification (17 bytes: 8 x u16 LE + 1 byte motion artifact) -> `Train_MyoDevice_onFeatureVector` (0x5047B0).
If MyoDevice+112 (training dataset pointer, set by 0x505AA0) is non-null, a 40-byte Event is built
(`Train_makeFeatureVectorEvent` 0x50D290) and pushed into the dataset's event queue:

| Offset | Type | Field |
|---|---|---|
| 0 | u32 | event type = 0x80000000 (private "training feature vector" event) |
| 4 | ptr | MyoDevice* |
| 8 | u16[8] | feature vector (8 EMG channels, "RMS" features) |
| 24 | u8 | motion artifact byte |
| 32 | u64 | timestamp (from BLE layer) |

`Train_Dataset_collectPose` (0x509E40), per pose:
1. Pose must be one of {0,1,2,3,4} (table 0xFF1ECC) = rest, fist, wave_in, wave_out, fingers_spread
   (libmyo_pose_t); else "pose is not a pose that can be trained".
2. Set MyoDevice training pointer, clear this pose's stored vectors, t0 = now (0x4FCC00).
3. Pump the event queue for at most 2500 ms (0x507860, timeout arg 2500 ms) with predicate 0x509690:
   - event type 3 (disconnected) -> abort flag, stop.
   - type 0x80000000 with timestamp >= t0: append to sample vector; call progress callback
     `cb(user, level, progress)` with level = u8(sum(8 u16) / 16384 * 255) (unused by Trainer) and
     progress = (255*count)/100 (integer); stop when count == 100.
   - type 6 (orientation event [I]) with timestamp >= t0: appended to a second vector (only used by the JSON telemetry).
4. Clear MyoDevice training pointer. Disconnected -> "Myo disconnected while gathering training samples".
   count < 100 -> "Not enough samples to train pose (numsamples=N pose=P)".
5. Store vectors in dataset map[pose].

So each pose = exactly the first 100 feature vectors after SPACE, i.e. about 2.0 s at the firmware's ~50 Hz
feature-vector rate [I: rate inferred from 100 samples within 2.5 s timeout]. No filtering, no labels
other than the pose index; the rest/active split is found later by thresholding.

Dataset object (0x5091F0, 72 bytes) [partly I]: +0 MyoDevice*, +4 event queue (mutex/condvar), +32 std::map<pose, {vector<Event40> fv; vector<Event40> orient;}>.

## 5. Training algorithm (0x50C9E0 -> 0x524680 -> 0x5249E0) [C]

Conceptually: vector-quantization codebook (spherical k-means on normalized 8-channel RMS vectors) +
per-pose discrete histogram (multinomial naive Bayes / discrete-emission model) with Laplace smoothing.
Codeword 0 is reserved for "inactive" (low energy) frames.

### 5.1 Input matrix

`Train_Dataset_trainModel` (0x50C9E0): for pose p = 0..4 (in that order), requires exactly 100 samples
("Invalid number of samples in collected sensor data: "), builds `float X[5][100][8]` where
`X[p][i][ch] = (float)u16 feature[ch]` (event bytes 8..23). Then `blob = new byte[7696]` zeroed and
`Train_BuildModelBlob(X, 5, 100, blob)`.

### 5.2 Constants

| Name | Value | Where |
|---|---|---|
| classes C | 5 | 0x50CB34 |
| samples per class N | 100 | 0x50CB34 |
| channels D | 8 | everywhere |
| k-means clusters | 20 | 0x524AF7 (first arg of 0x525160) |
| codebook size K | 21 (20 + zero codeword), max 128 | 0x525160 |
| k-means max iterations | 1000 | 0x52533F |
| activity threshold factor | 2.0 x norm(mean(rest)) | 0x524ADA, 0x524DAD |
| min channels over threshold | 2 | 0x5247EE |
| unused value | 0.75 x norm(0.5*(mean(rest)+mean(class 3))) stored at model+11824, not exported | 0x524A9A |
| max active samples | 700 (stack arrays) | 0x524EA0 / 0x525160 |

### 5.3 Pseudocode (float32 arithmetic unless noted; norm uses double sqrt then cast to float)

```
norm(v) = sqrt(sum v[d]^2)
cos(a,b) = dot(a,b) / sqrt(dot(a,a)*dot(b,b))

train(X[C][N][8]):
  muRest = mean over i of X[0][i]                      // 0x524890
  thr    = 2.0 * norm(muRest)                          // model+11788

  // ---- codebook (0x525160) ----
  code[0] = 0 vector
  A = all X[c][i] (c = 0..C-1, i = 0..N-1, in order) with norm(X[c][i]) >= thr
  if count(A) < 20: K = 1 (codebook = only zero vector)   // degenerate
  else:
    for j in 0..19: cent[j] = A[ round((j + 0.5) * count(A) / 20.0) ]   // C round(), half away from zero
    sphericalKMeans(cent, A, maxIter = 1000)              // 0x524EA0
    for j in 0..19: code[j+1] = cent[j] / norm(cent[j])
    K = 21

  // ---- per-class histograms ----
  for c in 0..C-1:
    if c == 0: s = 0; e = N-1
    else: (s, e) = findActiveSegment(X[c], muRest)        // 0x524D10
    cnt[0..127] = 0
    for i in s..e: cnt[quantize(X[c][i])]++
    M = e - s + 1
    if c > 0: M -= cnt[0]; cnt[0] = 0                     // inactive frames ignored for pose classes
    for k in 0..K-1: P[c][k] = (cnt[k] + 1.0f) / ((float)K + (float)M)
  if K != 1: copy codebook to model+7684 (not exported)

sphericalKMeans(cent[20], A, maxIter):
  assign[] = garbage (memset with byte (count & 0xFF)) so first pass always "changes"
  for it in 0 .. maxIter-1:
    changed = false
    for each a in A (index n):
      best = argmin_j (1 - cos(cent[j], a))  // first minimum wins; strict '>' compare, init FLT_MAX
      if assign[n] != best: changed = true; assign[n] = best
      size[best]++
    cent[j] = sum of assigned a / size[j]    // plain mean of UNnormalized samples; empty cluster -> zero vector
    if !changed: break
  // note: a zero centroid gives NaN cosine and is never selected again; after final
  // normalization it becomes NaN in the blob (edge case, not handled by the original code)

findActiveSegment(Xc[N], muRest):                          // 0x524D10
  t = 2.0 * norm(muRest)
  first = first i with norm(Xc[i]) >= t   (if none: first = 0)
  last  = last  i with norm(Xc[i]) >= t   (if none: last = N-1)
  if first > last: first = 0; last = N-1
  s = max(0, first - 1); e = min(N-1, last + 1)

quantize(x):                                              // 0x524770
  if (number of channels d with x[d] >= thr) < 2: return 0   // per-channel compare against thr (a norm!)
  xn = x / norm(x)
  best = 0; bestDot = 0.0
  for k in 1..K-1: dk = dot(code[k], xn); if dk > bestDot: bestDot = dk; best = k
  return best
```

The firmware presumably runs the same quantizer on-device and picks the pose maximizing the (log-)product of
P[c][k] over a sliding window [I - firmware side not analysed here].

## 6. Model blob layout (7696 = 0x1E10 bytes, little endian) [C]

Written by `Train_BuildModelBlob` (0x524680) from the 11828-byte model struct (0x524950 init):

| Blob offset | Size | Type | Content | Source |
|---|---|---|---|---|
| 0x0000 | 4 | u32 | 1 (constant; format/version or "valid" marker [I]) | hard-coded |
| 0x0004 | 4 | u32 | 0 (constant) | hard-coded |
| 0x0008 | 4 | i32 | number of classes = 5 | model+11780 |
| 0x000C | 4 | i32 | codebook size K (21 normally, 1 if degenerate) | model+11784 |
| 0x0010 | 3584 | float32[7][128] | P[class][codeword]; rows 0..4 = pose 0..4 (rest, fist, wave_in, wave_out, fingers_spread); only columns 0..K-1 used; rows 5..6 and unused columns 0 | model+4 |
| 0x0E10 | 4096 | float32[128][8] | codebook; row 0 = zeros, rows 1..K-1 = unit-length centroids; rest 0 | model+3588 |

No checksum, no threshold, no rest-mean in the blob (the activity threshold is NOT transmitted; the
firmware must derive its own inactivity gate [I]). Class index = libmyo_pose_t value.

Model struct (not transmitted) extra fields: +0 flag (1 at init, 0 after successful train), +7684 codebook
copy, +11788 thr, +11792 muRest[8], +11824 unused 0.75*norm(...) value.

## 7. Upload, activation, persistence, readback

### 7.1 Upload (0x502310, under MyoDevice mutex; requires paired and not rejected) [C]

```
for off = 0; off < 7696; off += n:
  n = min(18, 7696 - off)
  write handle 25 (command char), 20 bytes: [0D, n, blob[off .. off+n-1], zero padding to 20 bytes]
  (0x502220 after each write: wait/complete [I])
write handle 25, 5 bytes: [0E, 03, slot, 10, 1E]    // 0x1E10 = 7696, slot = 0 from Trainer
MyoDevice+108 (has_custom_classifier) = 1
```
428 chunk packets (427 x 18 bytes + 1 x 10 bytes). The 0x0E "index" byte is the classifier slot the blob is
stored into; Myo Connect always uses slot 0. Firmware confirms 5 slots (0..4; slot 0 at flash 0xA000), see Part B 2.14 [C].

### 7.2 Activation [C]

`Train_QMyo_setClassifier(1)` (0x470780) -> libmyo_set_active_classifier(myo, type=1, index=0) -> 0x505310 stores
MyoDevice+52 = type, +53 = index, and 0x504F50 writes handle 25: `[0C, 02, (type == 1 ? 1 : 0), index]`
(4 bytes). Built-in: `[0C 02 00 00]`, custom: `[0C 02 01 00]`. Then Qt signal classifierTypeChanged, and Trainer
emits classifierChanged.

### 7.3 Persistence

The blob is not stored on the PC: no file, QSettings or registry key references it (only the in-memory
dataset; freed on stopTraining). The model lives in the armband flash (Part B 2.14) [C], which is why "has custom"
survives reconnects. The only PC-side output is the telemetry upload of raw collected data (JSON, "trainer"
section, annotations appId/softwareVersion/uuid) by sendCollectedData (0x501240 / 0x50B8F0), a dead Thalmic
endpoint today.

### 7.4 Readback from firmware (fw info, handle 21, parsed by 0x503E70) [C]

| fw info byte | MyoDevice field | Meaning |
|---|---|---|
| +6 (u16) | +48 | unlock pose |
| +8 | +52 | active classifier type (0 built-in, 1 custom) |
| +9 | +53 | active classifier index |
| +10 | +108 | has_custom_classifier (0/1) |
| +11 | +54 | stream_indicating (always 0 in firmware 1.5.1970) |

Qt `classifierType()` returns MyoDevice+52; armband list `itemHasCustomClassifier` reflects +108. Values are
read only at connect and updated locally on setClassifier / upload.

## 8. Reimplementation checklist (C#)

1. Connect, set mode with EMG mode 1 or 5 (feature vector on handle 39; CCCD 40 = 0x0001) and classifier off.
2. For pose in {0 rest, 1 fist, 2 wave_in, 3 wave_out, 4 fingers_spread} (collect order is free; UI order was
   fist, spread, in, out, rest): after a start trigger take the first 100 handle-39 packets
   (`ushort[8]` from bytes 0..15), fail if fewer than 100 within 2.5 s. Instruct the user: relax ~0.7 s, then perform
   and hold the gesture.
3. Build `float[5,100,8]`, run section 5 exactly (float32; `Math.Round(x, MidpointRounding.AwayFromZero)` for the
   init index; strict comparisons as documented).
4. Serialize per section 6 into a zeroed 7696-byte buffer (`BinaryPrimitives` LE; P rows stride 128 floats; codebook
   at 0x0E10).
5. Upload with 0x0D chunks (write-with-response, one at a time), then `[0E 03 00 10 1E]`, then `[0C 02 01 00]`.
6. Verify by reading handle 21 again after reconnect: byte 10 == 1, bytes 8/9 == 1/0.

C# core sketch:
```csharp
// Original accumulates the sum of squares in float32 and converts to double only for sqrt.
static float Norm(float[] v)
{
  float s = 0f;
  for (int i = 0; i < 8; i++)
  {
    s += v[i] * v[i];
  }
  return (float)Math.Sqrt(s);
}
```

## 9. Unknowns / caveats

- Firmware-side inference (activity threshold 200.0, leaky log-probability accumulator, decision margins,
  blob words +0 unused and +4 re-orient flag) is documented in Part B 10.2; slots in Part B 2.14.
- The firmware does not validate the blob contents (only the total length on 0x0E).
- Firmware upload buffer pitfall (Part B 2.13): the 0x0D byte counter is only reset by a 0x0E whose size
  matches. After an interrupted upload the partial data stays in RAM and later chunks append after it, so
  the next upload is rejected. Recover by power-cycling the Myo (RAM cleared) before re-uploading.
- Exact BLE timing between chunks (0x502220 behaviour not analysed; assume write-with-response). 0x0D
  chunks bypass the firmware's 16-entry command queue, so write-with-response pacing is sufficient.
- Degenerate cases (fewer than 20 active frames overall, empty k-means clusters -> NaN codewords) are uploaded
  as-is by the original code.
- The 0.75 * norm(...) value (uses class 3 = wave_out mean) and the model+7684 codebook copy are computed but
  never exported (dead code or leftovers).

# Part F. Open questions and next steps

## To verify on hardware

* The advertised name length (nRF code builds a shortened name of 8 chars; confirm with a scan).
* set_mode_ext at 1000 Hz raw EMG: packet sizes (20 bytes) and the 5-samples-per-pair packing.
* FV (handle 39) values and the motion-artifact bitmask; MPF scaling (Hz x 128).
* That UUID discovery through InTheHand.BluetoothLE finds all services (including FV 0x0004 and Log
  0x0006) and that indications (classifier, motion, log) are delivered like notifications.
* Behaviour of firmware versions older than 1.5 (3-byte classifier events, no FV/MPF).

## Not fully traced

* The built-in classifier's post-processing math (summary only, Part B 10.5).
* The meaning of the custom blob's word +4 (re-orient flag) in practice; the LED trim consumer.


