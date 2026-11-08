# Myo firmware 1.5.1970: built-in pose classifier, re-implementation spec

Target: Kinetis application image (Thumb-2, hard float, base 0x10000). All addresses are
firmware addresses (file offset in `kinetis_1.5.1970_at_0x10000.bin` = address - 0x10000).
Markers: [C] confirmed from disassembly/decompilation, [I] inferred.
"core" = classifier object at RAM 0x1FFF0984 (`&byte_1FFF0974[16]`).
Companion data file: `builtin_classifier_1.5.1970.json` (all weights, biases, mean/std,
constants, with addresses and byte lengths).
Reference implementation + test vector generator (C#):
scratchpad `builtin/tool/Program.cs` (copy it next to your port if useful); its output is
reproduced in section 9.

---------------------------------------------------------------------------------------------

## 0. Summary

- One built-in model only. It is used whenever the active classifier is not a present custom
  slot (user settings type != 1, or type 1 with a blank slot). fw_info type 0 = built-in [C]
  (0x19A0C sets core+0xB5B8 = 1 "built-in" when no custom blob loads).
- Per frame (FV rate, 50 Hz by default): 16 features = 8 x (rms / maxRmsScalar) + 8 x MPF (Hz),
  both oriented by arm (mirror) and sync channel offset (rotate), standardized with
  mean/std tables, pushed into a 25-frame history; the network input is 5 history frames,
  nominally t, t-5, t-10, t-15, t-20 (0.5 s), BUT the ring gather has an index bug (section 3.4)
  that must be emulated for bit-exact behaviour [C].
- Network: Dense 80->256, ReLU, Dense 256->128, ReLU, Dense 128->64, ReLU, Dense 64->5,
  Softmax, all float32. The layers are typed PReLU, but the alpha pointers point into .bss that
  is never written, so alpha = 0 (pure ReLU) [C code / I no hidden writer].
- Decision: class c only if p[c] > 0.5, else rest [C]. Then 10-frame majority vote (ties = rest)
  [C], then a stateful post-processor (0x17380) that gates pose entry by EMG energy, requires
  5 of 5 agreeing decisions, holds the pose while selected channels stay active, applies a
  10-frame refractory period, and rejects fist / fingers_spread around IMU taps [C].
- Classes: 0 rest, 1 fist, 2 wave_in, 3 wave_out, 4 fingers_spread (pose ids on the wire are
  identical, table 0x6F07C = 0,1,2,3,4,5) [C]. Double tap (5) comes from a separate detector.
- No poses before sync: the classifier only runs in sync state 2 [C]. Three per-sync
  parameters are needed besides arm and offset: maxRmsScalar, enterThr and prof[8]
  (section 2). They are not transmitted; the host must reconstruct them (section 2.3).
- Not bit-exact from BLE in principle: the firmware uses float rms and mpf; the host only gets
  trunc(rms) (u16) and round(mpf*128)/128. Everything after the inputs can be reproduced
  exactly (float32 order of operations given below).

---------------------------------------------------------------------------------------------

## 1. Inputs and call chain [C]

`fw_fv_process` 0x1417C, each FV frame:
1. rms[ch] = sqrtf(sum(x^2)/W) (float), sent as u16 trunc on handle 39 (bytes 0..15), byte 16 =
   motion-artifact mask (bit ch).
2. MPF (emg_mode 5): u16 = (unsigned)(mpf*128 + 0.5) on handle 43.
3. Warm-up boost of rms (section 7.3) - normally not applied for the built-in model.
4. `0x1324C(rms[8], mpf[8], mask)` -> `fw_cls_process_frame` 0x19CF0(core, rms, mpf, mask).
   The FV/MPF notifications of a frame are queued before any classifier event produced by the
   same frame (notify at 0x143FA/0x14512, classifier call at 0x1451E).

Host inputs: rmsF[ch] = rms_u16[ch] (optionally + 0.5 to remove the truncation bias [I]),
mpfF[ch] = mpf_u16[ch] / 128f, mask = byte 16 of the FV packet. Use emg_mode 5 to get both.

---------------------------------------------------------------------------------------------

## 2. Sync dependence and per-sync parameters

### 2.1 States [C] (`fw_cls_sync_state` 0x197E8)
- core byte 0: 0 reset, 1 syncing, 2 synced. State 0 lasts exactly one frame: the next frame
  enters state 1 (thresholds core+0x44[8] = 120.0 (150.0 for sync mode 1; mode is 2, set at
  init by 0x12EAC -> 0x19B22(core, 2)), maxRms core+0x64[8] = 0).
- The classifier (built-in path) runs only in state 2. In states 0/1 the frame pushes 0 (or
  0xFFFF if the frame is invalid) into the 11-frame pose history and nothing else.
- Back to state 0 (= unsync if it was 2): (a) the 11-frame pose history (core+0xA4, float
  entries) is non-empty and both its oldest and newest entries are 65535.0 (0x19778); (b) in
  state 1, the 15-frame RMS window fails the profile test 0x18E28 (mode 2: per-channel max over
  the window has max >= 1900, mean >= 1500 and min >= 1100); (c) classifier disabled / reload /
  disconnect.

### 2.2 Values set at sync success (frame S, `0x19A6C`) [C]
| Name | Storage | Value |
|---|---|---|
| arm | core dword 2, copied to builtin+4 (core+0x8DEC) | 1 or 2 (reported arm byte: 1 right, 2 left; same numbers) |
| offset | core dword 3, copied to builtin+0 (core+0x8DE8) | `(int)roundf(fmodf(A + 2pi, 2pi) / 2pi * 8) % 8`, A = sync angle in [0, 2pi], 2pi = 0x40C90FDB (0x191DC) |
| angle A | core dword 4 | reported as rotation u16 = (unsigned)(A*1000) |
| maxRmsScalar | max over ch of core+0x64[ch] | running per-channel max of rms over every frame processed in state 1 (from state-1 entry up to and including S). Not updated after sync. |
| enterThr | core+0x4C84 (pp state +0x8C) | `fminf(fmaxf(0.7f * norm(meanSnap), 600f), 1300f)` (0x18DEC), meanSnap = per-channel mean of the 15-frame RMS snapshot (core+0x264C) |
| prof[8] | core+0x44 copied to pp state +0x94 | per-channel `prof = fminf(prof, mean15[ch])`, start 120.0, updated every state-1 frame on which the gesture check runs (0x193FC) |
| pp state +0x90 | | 200.0 (stored, never read by the built-in path) |

- norm(v) = sqrtf(sum v[i]*v[i]) accumulated in order i=0..7 from 0.0f (0x17BCC).
- Ring means (0x18356): sum from the oldest entry to the newest (per channel, float adds), then
  divide each sum by the entry count (float).
- The 15-frame RMS window (core+0x1378, cap 15) receives every state-1 frame's rms (push before
  any check). The gesture check (0x19568) runs on every state-1 frame once that window holds 15
  frames and passes 0x18E28.
- The snapshot ring (core+0x264C) is a copy of the 15-frame window taken by 0x17F96 on the
  frame where the consecutive-good-frame counter goes from 10 to 11. Sync succeeds when that
  counter reaches 50 (20 if the "fast" flag core+0x1C is set; it is 0 in this build).
  Therefore, if nothing interrupts the final streak, the snapshot = rms frames S-53..S-39
  (inclusive) [I derived from the code paths; C for each step].
- arm and x-direction: x-direction is not used by the classifier. A debug override path
  (globals 0x20008A6C/0x20008A7C) can change the reported arm/angle without changing the
  internal arm/offset [C], never set in normal operation [I].

### 2.3 Host-side reconstruction [I]
The host sees every FV frame (if no packet loss) and the arm_synced event at frame S (the event
is queued after frame S's FV/MPF notifications). Shadow the firmware:
- Start a "syncing" span at classifier enable (set_mode with classifier on) and after every
  arm_unsynced; while syncing, track maxRms = max over all rms values, a 15-frame window and
  prof[ch] = min(120, min of 15-frame means once the window is full).
- On arm_synced (frame S): maxRmsScalar = maxRms; enterThr = clamp(0.7*norm(mean(rms[S-53..S-39])),
  600, 1300); prof as tracked; arm from the event; offset from the rotation (A = rot/1000f,
  error < 1 mrad; the offset can differ only if A*8/2pi is within ~0.0013 of k+0.5).
- Rarely, state 1 restarts silently (rule 2.1 a/b): restart the shadow when 2 invalid frames
  10 frames apart are seen (section 6.1) or the window profile test fails.
- If the host connects to an already-synced armband it cannot know these values; reasonable
  defaults: enterThr 600..1300 (use the clamp of the sync-gesture energy if seen), prof = 120,
  maxRmsScalar = max rms seen while the user performed the sync gesture.

---------------------------------------------------------------------------------------------

## 3. Feature pipeline [C]

### 3.1 Per-frame normalization (0x19D5E..0x19D86)
```
s = rmsMaxScalar                          // max of core+0x64[0..7] (0x17C1C: start x[0], replace if s < x[i])
r[ch] = rms[ch] / s                       // float div; raw (unboosted) rms
m[ch] = mpf[ch]                           // Hz, not scaled
```

### 3.2 Orientation (0x16CD8 -> 0x1886E on r and on m separately)
```
if arm == 2: v[i] = v_old[(6 - i) & 7]    // mirror 0x18810: swaps 0<->6, 1<->5, 2<->4; 3, 7 fixed
             off = -offset
else         off = offset                 // arm must be 1 or 2, else frame rejected (0xFFFF)
k = off >= 0 ? (off & 7) : 8 - ((-off) & 7)
v[i] = v_old[(i + k) % 8]                 // std::rotate left by k (0x1882A), verified by emulation
```
Closed form: arm 1: f[i] = x[(i + offset) & 7]; arm 2: f[i] = x[(6 - i + offset) & 7].

### 3.3 Standardization and history (0x16C06)
```
f[0..7] = oriented r, f[8..15] = oriented m
z[i] = (f[i] - mean[i]) / std[i]          // mean 0x322C4, std 0x32304 (16 floats each); float sub, then div
hist.Push(z)                              // ring: cap 25, 16 floats per slot, storage 75 slots (0x17F4A)
if hist.Count <= 24: vote.Push(0); return 0          // first 24 frames after a reset
x = hist.Gather(n = 25, step = 5)         // 5 slots -> 80 floats, newest first
```
Ring semantics (0x17F4A): `if (count > 0) head = (head+1) % cap; slot[head] = z;
if (count < cap) count++ else tail = (tail+1) % cap.`

### 3.4 Gather bug (0x18466) [C]
```
idx = head
for (i = 0; i < 25; i += 5):
    if (idx < 0) idx = cap - idx          // BUG: should be cap + idx
    append slot[idx]                      // slots 25..74 are never written -> all zeros
    idx -= 5
```
Effect: only when head >= 20 are the frames t, t-5, t-10, t-15, t-20. For head h < 20 the
first wrapped frame reads an all-zero slot (25 + 5k - h, i.e. standardized 0 = feature mean) and
the following frames come from slots shifted by +10k* - 2h. head cycles 0..24 with the frame
counter, so 20 of every 25 frames use a corrupted input. Emulate it literally (75-slot array).
History reset (0x16B3E) clears slots 0..24 and count/head/tail; it happens at unsync, classifier
reload and double tap. It is NOT reset at sync success (it is already empty because nothing is
pushed in states 0/1).

### 3.5 Feature vector layout
x[16*k + j], k = 0..4 gather order (k=0 newest), j = 0..7 oriented normalized rms,
j = 8..15 oriented MPF; all standardized.

---------------------------------------------------------------------------------------------

## 4. Network [C]

Descriptor (RAM .data, init image at flash 0x75008 copied to 0x1FFF0000, 0x370 bytes):
- 0x1FFF00F0 (flash 0x750F8): {16, mean 0x322C4, std 0x32304}.
- 0x1FFF013C (flash 0x75144): {num_classes 5, num_layers 8, frame_step 5, layers 0x1FFF011C}.
- Layer struct (32 B): u8 type (0 dense, 1 prelu, 2 softmax), in, out, bias*, weights*,
  in_buf*, out_buf*, alpha*.

| # | Type | In -> Out | Weights (row-major W[out][in]) | Bias | Struct |
|---|---|---|---|---|---|
| 0 | dense | 80 -> 256 | 0x5AA58, 81920 B | 0x32344, 1024 B | 0x1FFF016C |
| 1 | prelu | 256 | alpha* 0x2000840C (.bss = 0) | | 0x1FFF014C |
| 2 | dense | 256 -> 128 | 0x32A58, 131072 B | 0x32758, 512 B | 0x1FFF00FC |
| 3 | prelu | 128 | alpha* 0x20008510 (.bss = 0) | | 0x1FFF018C |
| 4 | dense | 128 -> 64 | 0x52A58, 32768 B | 0x32958, 256 B | 0x1FFF01AC |
| 5 | prelu | 64 | alpha* 0x20008A68 (.bss = 0) | | 0x1FFF01CC |
| 6 | dense | 64 -> 5 | 0x6EA58, 1280 B | 0x32744, 20 B | 0x1FFF01EC |
| 7 | softmax | 5 | | | 0x1FFF020C |

Arithmetic (all float32, Cortex-M4F VMLA = multiply then add, two roundings):
```
dense (0x16DEE/0x17BF0): acc = 0f; for i in 0..in-1: acc = acc + W[o*in+i]*x[i]; y[o] = acc + b[o]
prelu (0x16E6E):         y = (x <= 0f) ? alpha*x : x            // alpha = 0f
softmax (0x16EE4):       m = x[0]; for i: if (m < x[i]) m = x[i]
                         e[i] = expf(x[i] - m) (newlib expf 0x28784); s = sum e in order; p[i] = e[i]/s
decision (0x16BCC):      best = 0.5f; cls = 0; for i in 0..4: if (p[i] > best) { best = p[i]; cls = i }
```
- Alpha: the three alpha pointers are referenced only by the layer structs; no code or data
  writes them, .bss is zeroed at boot (0x1FFF0370..0x2000C11C) [C]. If you prefer to treat this
  as a firmware bug and use real PReLU, there are no alpha values anywhere to recover [I].
- C# note: do not use FMA; `acc = acc + w * x` with float locals matches. MathF.Exp may differ
  from newlib expf by 1 ulp; this can only matter when a probability is within ~1e-7 of 0.5.

---------------------------------------------------------------------------------------------

## 5. Decision pipeline after the network

### 5.1 Majority vote (0x1857E, 0x18632) [C]
- Ring of the last 10 decisions (values 0..4) with a per-class histogram. Push also happens
  with value 0 when: the history has <= 24 frames, the frame is invalid, the arm is invalid,
  or any internal error.
- Majority: best = 0; tie = false; for c = 1..4: if cnt[best] < cnt[c] { best = c; tie = false }
  else if cnt[best] == cnt[c] { tie = true }. Result = tie ? 0 : best.
- The classifier returns 0 for the first 24 frames (without voting), 0xFFFF for invalid frames,
  else the majority result.
- Vote reset to rest: in 0x19CF0, when the previous reported pose (core+0x14 at frame start) is
  non-zero and the post-processed result of this frame is 0, the ring is filled with 10 zeros
  (0x18606). Also cleared and filled with zeros on history reset (0x16B3E).

### 5.2 Post-processor (0x17380) [C]
Runs on every frame whose classifier output is not 0xFFFF. State `pp` at core+0x4BF8:
```
int enter;          // +0x00  (energy gate passed, waiting for stability)
int locked;         // +0x04  (entry stable; agreement + hold logic active)
int switchArmed;    // +0x08  (sudden energy jump seen while holding)
int mask[8];        // +0x0C  (hold channels)
int maskCount;      // +0x2C  (not used)
int refractory;     // +0x30
int cur;            // +0x34  last valid final result (= core dword 4875, written by 0x19CF0)
int hidx;           // +0x38
int dhist[20];      // +0x3C  raw decisions (input of this function)
float enterThr;     // +0x8C  (sync)
float unused200;    // +0x90
float prof[8];      // +0x94  (sync)
float prevRms[8];   // +0xB4  rms of the previous processed frame
Ring50x8 ering;     // +0xD4  rms ring, cap 50 (only the last <= 5 entries and count are used)
```
Reset 0x1702C (at unsync, reload, double tap): enter, locked, switchArmed, mask, maskCount,
refractory, cur, hidx, dhist, prevRms = 0. enterThr/prof/ering are not cleared there (ering is
cleared at every energy-gate evaluation).

Entry weights w(c) (0x1709C): rest 1.0, fist 0.6, wave_in 0.6, wave_out 0.9,
fingers_spread 0.5, (double_tap 0.0).

```
int PostProcess(float[] rms /*raw rms*/, int p /*vote result*/, TapRing taps)
  prevD = dhist[(hidx + 19) % 20]
  dhist[hidx] = p
  if (cur == 0 && p != 0)
    if (prevD == p) { if (Norm(rms) <= Norm(prof)) p = 0; }
    else p = 0;
    enter = 0; locked = 0;
  if (enter == 0)                                   // 0x17100
    if (Norm(rms) > enterThr * w(p)) enter = 1; else p = 0;
    ering.Clear();
  if (enter != 0 && locked == 0)                    // 0x171A0
    ref = (ering.Count == 0) ? prevRms : ering.Mean();
    nref = Norm(ref);
    ering.Push(rms);
    rel = (Norm(rms) - nref) / nref;
    if (rel > 0.1f) { if (cur == 0) p = 0; }        // still rising (returns p if cur != 0)
    else
      locked = 1;
      k = Min(ering.Count, 5);
      avg = ering.MeanOfLast(k);                     // oldest of the k first
      thr = Max(avg) * 0.4f;
      mask[ch] = avg[ch] > thr ? 1 : 0; n = popcount;
      while (n > 3) { drop the active channel with the smallest avg (strict <, lowest index on tie); n--; }
      maskCount = n;
  if (locked != 0)
    agree = number of j in {hidx-4 .. hidx} (mod 20) with dhist[j] == p
    if (agree <= 4) p = 0;                          // all 5 must agree
    if (cur != 0) p = Hold(rms, p);                 // 0x17270
  if (p == 0 && cur != 0) refractory = 10;
  else if (refractory > 0) { refractory--; if (p != 0) { p = 0; dhist[hidx] = 0; } }
  if ((p == 4 || p == 1) && cur == 0 && TapRejected(taps)) { p = 0; dhist[hidx] = 0; }
  hidx = (hidx + 1) % 20;
  prevRms = rms;
  return p;

int Hold(float[] rms, int p)                        // 0x17270
  c = cur;
  if (switchArmed != 0 && p != c && p != 0) { switchArmed = 0; c = p; }
  for ch: thr[ch] = fminf(fmaxf(prof[ch] * 1.3f, 50f), 120f)
  below = #ch(rms[ch] <= thr[ch]); belowMasked = #ch(mask[ch] && rms[ch] <= thr[ch])
  if (below == 8) { enter = locked = switchArmed = 0; return 0; }
  jump = (c == 1) ? 340f : 200f;
  if (Norm(rms) > Norm(prevRms) + jump) { enter = 0; locked = 0; switchArmed = 1; return p; }
  if (belowMasked != 0) { enter = locked = switchArmed = 0; return 0; }
  return c;                                         // pose held regardless of the network

bool TapRejected(TapRing t)                         // newest first; Next() returns -1 when exhausted
  n = 0; v = 0;
  for (i = 0; i < 15; i++) { v = t.Next(); if (v > 0) n++; if (v < 0) break; }
  if (n != 0) return true;                          // a tap in the last 15 frames
  for (i = 0; i < 10; i++) { v = t.Next(); if (v != 0) break; }   // skip up to 10 empty frames
  if (v <= 0) return false;
  n = 1;
  for (i = 0; i < 25; i++) { v = t.Next(); if (v > 0) n++; if (v < 0) break; }
  return n >= 2 && n <= 4;
```
Notes:
- The 5-of-5 rule (not 4-of-5): `agree <= 4 -> 0` with exactly 5 entries examined [C 0x1744C].
- Comparisons on cur/p use the internal code table 0x6EF58 (0,1,100,101,304,42); because the
  codes are distinct, comparing class indices is equivalent.
- The "999" guard in Hold (0x1733C) can never trigger.
- Tap ring (core+0xB4A4, cap 50, values 0/1): one entry per frame pushed by the double-tap
  detector (0x17584) = 1 if at least one IMU tap callback arrived since the previous frame
  (0x1757A). Taps are fed only while synced (0x16514) [C/I]. Host: enable imu_mode 2/3 to get
  motion events (handle 31, type 0 tap) and set the flag for the next frame.
- Iterator 0x186A0/0x186A8: starts at the newest entry, walks backwards, returns the oldest
  then -1 forever after. An empty ring returns -1 immediately.

### 5.3 Frame-level sequence in state 2 (0x19CF0) [C]
```
syncState(rms, mask)                                // invalid flag, unsync check, maybe -> state 2
if just entered state 2: SetupSync()                // section 2.2
if holdCounter > 0:                                 // double-tap hold (core+0xB5AC)
    holdCounter--; pose = (reported == 5 && holdCounter != 0) ? 5 : 0
else:
    pose = BuiltinClassify(rms, mpf, invalid)       // 0xFFFF / 0..4 (section 3, 4, 5.1)
    if pose != 0xFFFF:
        pose = PostProcess(rms, pose, taps)
        if (reportedAtFrameStart != 0 && pose == 0) vote.FillZeros()
poseHistory11.Push(pose == 0xFFFF ? 65535f : pose)  // 0x19682
if pose == 0xFFFF: reported = cur  (unchanged)  else { reported = pose; cur = pose }
DoubleTap(rms): if detected { poseHistory11.Push(5); reported = 5;
                 ResetHistoryAndVotes(); ResetTapDetector(); holdCounter = 20; pp.Reset() }
emit arm_synced if newly synced; emit pose event if reported changed
```
- Pose events carry `reported` (0..5); unknown (0xFFFF) is never emitted as a pose here; it
  only appears via the unsync callback.
- During the 20-frame double-tap hold the classifier is not run and gets no frames (history,
  votes and pp were reset at detection, so the next 24 frames after the hold give rest).
- Double tap detector: 0x17B18 (see protocol report Part B 10.4).

---------------------------------------------------------------------------------------------

## 6. Invalid frames, unknown pose, unsync [C]

### 6.1 Invalid flag (computed in 0x197E8 every frame, core+0xA0)
```
invalid = ProfileTest(rms) || (popcount(mask[t] | mask[t-1] | mask[t-2]) > 2)
ProfileTest (mode 2, table 0x6EFD4, type 1 -> 0x1874C):
   max(rms) >= 1900 && mean(rms) >= 1500 && min(rms) >= 1100   // mean = sum/8 in order
   (plus "any rms < 0.0", never true)
```
The artifact ring holds the masks of the last 3 frames (zeros after reset), mask is the FV
byte 16 (0 during the first second after EMG start).
- Invalid frame in state 2: classifier returns 0xFFFF without touching the history, pushes 0 into
  the vote ring, post-processing skipped, reported pose unchanged, 65535 pushed into the
  11-frame pose history.
- Unsync (checked at the start of the next frame): pose history non-empty and its oldest and
  newest entries both 65535 -> state 0 (arm_unsynced event, lock, unknown pose callback,
  vibration). With a full history that means two invalid frames exactly 10 frames apart; with a
  partially filled history (right after a reset) a single invalid frame suffices.

### 6.2 Other 0xFFFF sources
Arm not 1/2 (cannot happen after a normal sync), descriptor errors.

---------------------------------------------------------------------------------------------

## 7. Other dependencies

### 7.1 Model selection [C]
One built-in network; no index semantics for type 0. Reloading the classifier (cmd 0x0C)
resets sync, history, votes and pp.

### 7.2 IMU
Not a network input. Used by: sync (orientation), tap rejection (5.2), double tap (5.3) [C].

### 7.3 Warm-up boost (0x1417C) [C]
Applied to the rms passed to the classifier (not to the transmitted FV) when user settings
classifier type == 1 AND the 1 s ring is full AND not warm AND synced:
`if mpf1s > 90 && rms <= 500: g = (mpf1s-90)/55.555 + 0.1; if rms > 400: g *= (500-rms)/100; rms *= 1+g`.
Normally irrelevant for the built-in model (type 0). Edge case: type 1 with a blank slot runs the
built-in model with boosted rms; the host cannot reproduce it (mpf1s is not transmitted).

### 7.4 Frame rate
Everything is counted in FV frames; with non-default window/shift (set_mode_ext) the time
constants scale accordingly and the network sees a different time base [C].

---------------------------------------------------------------------------------------------

## 8. C#-ready pseudocode

```csharp
sealed class BuiltinClassifier
{
  // tables from builtin_classifier_1.5.1970.json
  float[] Mean, Std;                 // 16
  float[][] W, B;                    // W[l] row-major [out*in], l = 0..3
  static readonly int[] In = { 80, 256, 128, 64 }, Out = { 256, 128, 64, 5 };

  // per-sync parameters
  public int Arm, Offset; public float MaxRmsScalar, EnterThr; public float[] Prof = new float[8];

  // state
  readonly float[][] slots = NewSlots(75, 16); int hCount, hHead, hTail;
  readonly int[] votes = new int[10]; readonly int[] vCnt = new int[5]; int vCount, vHead = -1, vTail = -1;
  // + PostProcess state (section 5.2), tap ring (cap 50), artifact masks (3), pose history (11)

  public float[] Features(float[] rms, float[] mpf)
  {
    var f = new float[16];
    for (int i = 0; i < 8; i++) { f[i] = rms[i] / MaxRmsScalar; f[8 + i] = mpf[i]; }
    Orient(f, 0); Orient(f, 8);
    for (int i = 0; i < 16; i++) { f[i] = (f[i] - Mean[i]) / Std[i]; }
    return f;
  }

  void Orient(float[] a, int s)
  {
    var c = new float[8]; Array.Copy(a, s, c, 0, 8);
    for (int i = 0; i < 8; i++)
    {
      a[s + i] = (Arm == 2) ? c[(6 - i + Offset) & 7] : c[(i + Offset) & 7];
    }
  }

  public float[] Network(float[] x)                       // x.Length == 80
  {
    float[] cur = x;
    for (int l = 0; l < 4; l++)
    {
      var o = new float[Out[l]];
      for (int r = 0; r < Out[l]; r++)
      {
        float acc = 0f;
        for (int c = 0; c < In[l]; c++) { float p = W[l][r * In[l] + c] * cur[c]; acc = acc + p; }
        o[r] = acc + B[l][r];
        if (l < 3 && o[r] <= 0f) { o[r] = 0f * o[r]; }  // PReLU with alpha 0
      }
      cur = o;
    }
    float m = cur[0];
    for (int i = 1; i < 5; i++) { if (m < cur[i]) { m = cur[i]; } }
    float s = 0f; var e = new float[5];
    for (int i = 0; i < 5; i++) { e[i] = MathF.Exp(cur[i] - m); s = s + e[i]; }
    for (int i = 0; i < 5; i++) { e[i] = e[i] / s; }
    return e;
  }

  // returns 0..4 or 0xFFFF
  public int Classify(float[] rms, float[] mpf, bool invalid)
  {
    if (invalid || (Arm != 1 && Arm != 2)) { VotePush(0); return 0xFFFF; }
    HistPush(Features(rms, mpf));
    if (hCount <= 24) { VotePush(0); return 0; }
    var p = Network(HistGather());
    float best = 0.5f; int d = 0;
    for (int i = 0; i < 5; i++) { if (p[i] > best) { best = p[i]; d = i; } }
    VotePush(d);
    return VoteMajority();                                 // tie -> 0
  }

  void HistPush(float[] z)
  {
    if (hCount > 0) { hHead = (hHead + 1) % 25; }
    Array.Copy(z, slots[hHead], 16);
    if (hCount < 25) { hCount++; } else { hTail = (hTail + 1) % 25; }
  }

  float[] HistGather()
  {
    var x = new float[80]; int idx = hHead;
    for (int k = 0; k < 5; k++)
    {
      if (idx < 0) { idx = 25 - idx; }                     // firmware bug, slots 25..74 stay zero
      Array.Copy(slots[idx], 0, x, 16 * k, 16);
      idx -= 5;
    }
    return x;
  }

  void VotePush(int v)                                     // 0x1857E, cap 10
  {
    if (vCount != 0) { vHead = (vHead + 1) % 10; } else { vTail = 0; vHead = 0; }
    if (vCount >= 10) { vCnt[votes[vTail]]--; vTail = (vTail + 1) % 10; } else { vCount++; }
    votes[vHead] = v; vCnt[v]++;
  }
  // Note the firmware order: head advances using the PREVIOUS count, then (if full) the tail
  // entry is evicted. When vCount==0 both indices are set to 0 first.

  int VoteMajority()
  {
    int best = 0; bool tie = false;
    for (int c = 1; c <= 4; c++)
    {
      if (vCnt[best] < vCnt[c]) { best = c; tie = false; }
      else if (vCnt[best] == vCnt[c]) { tie = true; }
    }
    return tie ? 0 : best;
  }
}
```
Vote ring caution: in 0x1857E the head is advanced before the eviction test and the eviction
uses the tail index; when full, tail == (head) after the advance, i.e. the evicted entry is the
one being overwritten. The code above reproduces this. Reset (0x18520) = count 0, head = tail =
-1, histogram zero; then 0x18606 pushes ten zeros.

PostProcess, Hold and TapRejected: section 5.2. Frame driver: section 5.3. Invalid flag: 6.1.

---------------------------------------------------------------------------------------------

## 9. Test vectors (computed by the C# reference implementation with the extracted tables)

Rotation check: firmware rotate emulation equals x[(i+k) mod 8] for k = -7..7.

TV1 Network(80 zeros) = [0.919333279, 0.066429235, 0.000167951628, 0.000967744563,
0.0131018031], decision 0.

TV2 Network(x), x[i] = 4*rnd-2, LCG s = 12345; per draw s = s*1664525 + 1013904223 (uint32),
rnd = (s >> 8) / 2^24; 80 consecutive draws per vector:
- #1 x[0..3] = -1.91838932, -1.9338088, 0.172623158, 0.539616108; x[79] = -0.512857199;
  p = [0.00120146549, 0.136459947, 0.817900062, 0.0444148816, 2.37117783E-05], decision 2
- #2 x[0..3] = 1.32953215, 0.768992424, 0.113106489, 1.58208466; x[79] = -1.50977969;
  p = [2.8862507E-05, 0.631321251, 0.0033451668, 0.000553374819, 0.36475125], decision 1
- #3 x[0..3] = -0.760653257, -1.26393104, 0.314855576, -1.99510527; x[79] = 0.296658993;
  p = [0.589950919, 0.166818202, 0.00837061554, 0.234590143, 0.000270140416], decision 0

TV3 Features(rms = 100,200,...,800; mpf = 60,70,...,130; maxRmsScalar 1000):
- arm 1 off 0: [-0.406864703, 0.322066575, 0.658550084, 1.039078, 1.65860271, 2.31789088,
  2.24345088, 1.87543476, -1.36431026, -0.628744125, -0.225042522, 0.148303106, 0.518998682,
  0.866679668, 1.09273016, 1.46081388]
- arm 1 off 3: [1.04168379, 1.92557681, 2.06744599, 2.37482166, 3.11222076, -0.254965812,
  0.00365269161, 0.0610063784, -0.093957983, 0.601859093, 0.95236212, 1.25757408, 1.60099971,
  -1.02140486, -0.94000572, -0.721760035]
- arm 2 off 0: [2.49023199, 2.46008039, 1.59781384, 1.039078, 0.689524353, 0.259605527,
  -0.44430694, 1.87543476, 1.17639422, 1.01206017, 0.559893906, 0.148303106, -0.202335313,
  -0.64378798, -1.34655297, 1.46081388]
- arm 2 off 3: [0.0759847835, -0.21243684, 3.00670981, 2.37482166, 2.14314222, 1.80331945,
  0.899571955, 0.0610063784, -0.940859437, -1.0389452, 1.73729849, 1.25757408, 0.879665732,
  0.489062756, -0.126911387, -0.721760035]

TV4 sanity probes (arm 1, off 0, scalar 1000, mpf 90 everywhere, 5 identical frames): all
channels 30/50 -> rest (p0 > 0.99999); all 300 -> fist (0.974); all 700 -> fist (0.99998);
activity centred on ch0 (700/400/80 profile) -> fingers_spread 0.992, ch1 -> fist 0.998,
ch2 -> wave_in 0.9987, ch3 -> wave_in 0.992, ch4 -> wave_in 0.622, ch5 -> fist 0.977,
ch6 -> fist 0.805, ch7 -> fingers_spread 0.774. (Plausible EMG semantics; confirms class order
and that MPF/rms scaling are right.)

TV5 history with the gather bug (arm 2, off 5, scalar 900); frame n = 0..34:
rms[ch] = n < 20 ? 80+40*ch+10*n : 80+40*((ch+3)%8)+5*n; mpf[ch] = 70+3*ch+0.5*n.
Network runs from n = 24 (count 25). x[16k] = first feature of gathered frame k:
```
n=24 head=24 x0=1.47088325 x16=1.20263362 x32=0.934383869 x48=0.666134179 x64=0.397884488 p1=0.989961386 d=1
n=25 head=0  x0=1.4977082  x16=0          x32=0           x48=1.36358345  x64=0.988033831 p1=0.987097025 d=1
n=26 head=1  x0=1.52453327 x16=0          x32=1.47088325  x48=1.20263362  x64=0.934383869 p1=0.999430239 d=1
n=30 head=5  x0=1.63183308 x16=1.4977082  x32=0           x48=0           x64=1.36358345  p1=0.932248175 d=1
n=34 head=9  x0=1.739133   x16=1.60500801 x32=0           x48=1.39040828  x64=1.04168379  p1=0.984318793 d=1
```
(full list in scratchpad `builtin/testvectors.txt`).

---------------------------------------------------------------------------------------------

## 10. Function / address index
| Address | Role |
|---|---|
| 0x1324C | per-FV-frame entry (timed lock check, then 0x19CF0) |
| 0x19CF0 | fw_cls_process_frame (frame driver, 5.3) |
| 0x197E8 | sync state machine, invalid flag (0x18CF0), unsync check (0x19778) |
| 0x19A6C | sync-success setup (enterThr, prof, arm/offset to built-in) |
| 0x17C1C / 0x19C36 | max of core+0x64 / divide rms by it |
| 0x16CD8 | fw_cls_builtin_classify (orientation, then 0x16C06) |
| 0x1886E, 0x18810, 0x1882A | orient, mirror, rotate |
| 0x16C06 | standardize, history, gather, network, argmax(0.5), vote |
| 0x17EAC | standardization |
| 0x17F4A, 0x18466 | ring push, ring gather (bug) |
| 0x16F92, 0x16F60 | network run, layer dispatch |
| 0x16DEE, 0x16E6E, 0x16EE4 | dense, PReLU, softmax |
| 0x16BCC | argmax with initial best 0.5 |
| 0x1857E, 0x18632, 0x18606, 0x18520 | vote push, majority, fill, reset |
| 0x17380 | post-processor; 0x17100 energy gate, 0x171A0 stability/mask, 0x17140 mask, 0x17270 hold, 0x1709C entry weights |
| 0x186A0, 0x186A8 | backward ring iterator (tap ring) |
| 0x17B18, 0x19C0A, 0x199C4 | double tap detect, inject pose 5, reset + 20-frame hold |
| 0x19682 | push into 11-frame pose history, set reported pose |
| 0x16D5A | model descriptor init (0x1FFF00F0, 0x1FFF013C) |
| 0x19B40, 0x16B70 | classifier init (history 25x16, vote 10, max class 4) |
| 0x6EF58 | u16 class codes 0,1,100,101,304,42 |
| 0x6EF64 + 56*mode | invalid/sync profile table (mode 2 at 0x6EFD4) |
| 0x6F07C | class -> pose id map (identity) |

## 11. Open points
- [I] The PReLU alphas are almost certainly a build bug (meant to be trained values); the
  firmware behaviour is ReLU, and no alpha values exist in the image.
- [I] Host reconstruction of maxRmsScalar / enterThr / prof requires seeing the whole sync
  phase; exact equality also needs float rms (only truncated values are transmitted).
- [I] The tap feed condition (0x13350: synced flag 0x200081D4 and 0x1A614 == 0) was not
  fully traced; treat taps as fed while synced.
- Not verified on hardware.
