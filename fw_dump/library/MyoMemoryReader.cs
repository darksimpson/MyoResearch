using System.Diagnostics;
using MyoSharp.Protocol;

namespace MyoSharp.Firmware;

public sealed class MyoMemoryReadOptions
{
  /// <summary>Also allow addresses in the Kinetis SRAM window (0x1FFF0000..0x2000FFFF).</summary>
  public bool AllowRam { get; init; }

  /// <summary>
  /// Settle bytes that read back as "0x00 or 0xFF" in the mirrored area by offering the armband a
  /// write of the candidate value, which it compares instead of programming. Needs a session opened
  /// with a fresh Prepare.
  /// </summary>
  public bool AllowWriteCompare { get; init; } = true;

  /// <summary>Bytes per "is everything in here 0x00 or 0xFF" probe; one request each.</summary>
  public int ProbeLength { get; init; } = 512;

  /// <summary>Bytes per verification checksum. Must stay within the fold-free length.</summary>
  public int VerifyLength { get; init; } = FirmwareChecksums.Fletcher16FoldFreeLength;

  /// <summary>Most candidate values tried per 16-byte block when settling bytes by write-compare.</summary>
  public int MaxWriteCompareCandidates { get; init; } = 16;

  /// <summary>
  /// Join an update session that is already open instead of erasing the spare bank again. Faster, but
  /// the mirrored copy may be older than the live flash, so exact reads are not attempted.
  /// </summary>
  public bool ResumeSession { get; init; }
}

public readonly record struct MyoMemoryReadProgress(uint Address, long BytesDone, long BytesTotal, long Requests, string Message);

/// <summary>
/// Bytes read out of the armband, with the addresses the armband could not pin down exactly.
/// </summary>
public sealed class MyoMemoryBlock
{
  internal MyoMemoryBlock(uint address, byte[] bytes, IReadOnlyList<uint> unresolved, bool checksumVerified, long requests)
  {
    Address = address;
    Bytes = bytes;
    Unresolved = unresolved;
    ChecksumVerified = checksumVerified;
    Requests = requests;
  }

  public uint Address { get; }

  public byte[] Bytes { get; }

  /// <summary>
  /// Addresses whose byte is either 0x00 or 0xFF and could not be settled; the value in Bytes is a
  /// guess (0x00). Everything else is exact.
  /// </summary>
  public IReadOnlyList<uint> Unresolved { get; internal set; }

  /// <summary>The armband's checksums over the result matched after the read.</summary>
  public bool ChecksumVerified { get; }

  public long Requests { get; }

  public bool IsExact => Unresolved.Count == 0 && ChecksumVerified;
}

/// <summary>
/// Reads the armband's flash (and optionally its RAM) over BLE, using the firmware update commands as
/// a read channel: Checksum (0x17) returns a Fletcher16 over any address and length, with no bounds
/// check at all, and Scan (0x18) reports whether 8-byte blocks are erased.
/// <para>
/// A Fletcher16 over two bytes determines both of them modulo 255, so two bytes come out of every
/// request. Modulo 255 the values 0x00 and 0xFF are the same, and that is the one thing the checksum
/// cannot tell apart: the firmware's overflow fold is itself congruent to the identity modulo 255, so
/// no range length or placement helps, however non-linear it looks (see FirmwareChecksums.Fold).
/// Such bytes are settled in three further ways, in order: Scan proves an 8-byte
/// block to be erased (all 0xFF); inside the area an OTA Prepare mirrors into the spare bank
/// (0x0..0xEFFF, so the bootloader, classifier slot 0, the user settings and the factory
/// calibration), a Write of a candidate value is compared rather than programmed, which settles it
/// exactly; and whatever is left is reported in <see cref="MyoMemoryBlock.Unresolved"/> for the caller
/// to resolve, for instance against a reference image.
/// </para>
/// <para>
/// Reading needs an update session, so opening one erases the spare flash bank, which costs one erase
/// cycle and about half a minute. Nothing the armband runs is touched: the staging bank is scratch
/// space, the session is abandoned again on close, and no command in this class ever programs flash
/// outside it. Expect roughly 100 bytes per second, so a full application dump takes hours.
/// </para>
/// </summary>
public sealed class MyoMemoryReader : IAsyncDisposable
{
  private const int BlockSize = 8;
  private const int WriteBlockSize = MyoCommands.OtaWriteSize;

  private readonly MyoDevice _device;
  private readonly MyoMemoryReadOptions _options;
  private readonly Dictionary<int, ushort> _blankChecksums = new();
  private StreamOptions? _previousStreams;
  private bool _mirrorIsFresh;

  public MyoMemoryReader(MyoDevice device, MyoMemoryReadOptions? options = null)
  {
    _device = device;
    _options = options ?? new MyoMemoryReadOptions();
  }

  public bool IsOpen { get; private set; }

  /// <summary>Commands sent since construction, for progress reporting.</summary>
  public long Requests { get; private set; }

  /// <summary>True when exact reads of the mirrored area are possible in this session.</summary>
  public bool CanReadExactly => _mirrorIsFresh && _options.AllowWriteCompare;

  /// <summary>
  /// Opens an update session so the read commands are accepted: streams are stopped, the spare bank is
  /// erased and the area below 0xF000 is copied into it. Nothing is committed, so the armband keeps
  /// running the firmware it has.
  /// </summary>
  public async Task OpenAsync(IProgress<MyoMemoryReadProgress>? progress = null, CancellationToken cancellationToken = default)
  {
    if (IsOpen)
    {
      return;
    }
    if (!_device.IsConnected)
    {
      throw new MyoMemoryReadException("The armband is not connected.");
    }
    if (!_device.FirmwareVersion.IsAtLeast(1, 5))
    {
      throw new MyoMemoryReadException(
        $"The armband runs firmware {_device.FirmwareVersion}; reading flash this way needs firmware 1.5.");
    }
    var info = await _device.RefreshFirmwareInfoAsync();
    if (info.OtaState == OtaState.Committed)
    {
      throw new MyoMemoryReadException(
        "A firmware update is staged on the armband: let it reset (leave it still for about 30 seconds) before reading.");
    }

    _previousStreams = _device.Streams;
    await _device.ConfigureStreamsAsync(StreamOptions.None);
    IsOpen = true;
    try
    {
      if (_options.ResumeSession && info.OtaState is OtaState.Ready or OtaState.Scanned or OtaState.Writing)
      {
        _mirrorIsFresh = false;
        Report(progress, 0, 0, 0, "Joined the open update session.");
        return;
      }
      if (info.OtaState != OtaState.Inactive)
      {
        await SendAsync(MyoCommands.OtaAbort());
        info = await ReadInfoAsync();
      }
      Report(progress, 0, 0, 0, "Erasing the spare flash bank so the read commands are accepted...");
      await SendAsync(_options.ResumeSession ? MyoCommands.OtaContinue() : MyoCommands.OtaPrepare());
      var stopwatch = Stopwatch.StartNew();
      info = await ReadInfoAsync();
      while (info.OtaState == OtaState.Preparing)
      {
        if (stopwatch.Elapsed > TimeSpan.FromMinutes(2))
        {
          throw new MyoMemoryReadException("The armband did not finish preparing the spare bank.");
        }
        cancellationToken.ThrowIfCancellationRequested();
        Report(progress, 0, 0, 0, $"Preparing the spare flash bank ({stopwatch.Elapsed.TotalSeconds:F0} s)...");
        await Task.Delay(250, cancellationToken);
        info = await ReadInfoAsync();
      }
      if (info.OtaState is not (OtaState.Ready or OtaState.Scanned or OtaState.Writing))
      {
        throw new MyoMemoryReadException($"The armband refused to open a read session (state {info.OtaState}).");
      }
      _mirrorIsFresh = !_options.ResumeSession;
      Report(progress, 0, 0, 0, "Read session open.");
    }
    catch
    {
      await CloseAsync();
      throw;
    }
  }

  /// <summary>Abandons the session and restores the streams that were running.</summary>
  public async Task CloseAsync()
  {
    if (!IsOpen)
    {
      return;
    }
    IsOpen = false;
    _mirrorIsFresh = false;
    try
    {
      if (_device.IsConnected)
      {
        await SendAsync(MyoCommands.OtaAbort());
        await _device.RefreshFirmwareInfoAsync();
      }
    }
    catch (Exception)
    {
      // Closing is best effort; nothing was committed either way.
    }
    var streams = _previousStreams;
    _previousStreams = null;
    if (streams != null && _device.IsConnected)
    {
      try
      {
        await _device.ConfigureStreamsAsync(streams);
      }
      catch (Exception)
      {
      }
    }
  }

  public async ValueTask DisposeAsync()
  {
    await CloseAsync();
  }

  /// <summary>Fletcher16 the armband computes over any address and length.</summary>
  public async Task<ushort> ChecksumAsync(uint address, uint length)
  {
    EnsureOpen();
    uint offset = ToImageOffset(address);
    await SendAsync(MyoCommands.OtaChecksum(offset, length));
    var info = await ReadInfoAsync();
    if (info.OtaState == OtaState.Failed)
    {
      throw new MyoMemoryReadException(
        $"The armband refused a checksum of 0x{address:X} ({info.OtaError}).");
    }
    if (info.OtaLength != (ushort)(offset >> 3))
    {
      throw new MyoMemoryReadException(
        $"The armband answered a checksum for a different address than 0x{address:X}; commands are being lost.");
    }
    return info.OtaStatus;
  }

  /// <summary>
  /// Number of 8-byte blocks from the address up to the first erased one, at most maxLength bytes
  /// worth. The firmware compares byte by byte here, so this is exact.
  /// </summary>
  public async Task<uint> ScanAsync(uint address, uint maxLength)
  {
    EnsureOpen();
    await SendAsync(MyoCommands.OtaScan(ToImageOffset(address), maxLength));
    var info = await ReadInfoAsync();
    if (info.OtaState == OtaState.Failed)
    {
      throw new MyoMemoryReadException($"The armband refused a scan of 0x{address:X} ({info.OtaError}).");
    }
    return info.OtaLength;
  }

  /// <summary>Whether the 8 bytes at the address are all 0xFF.</summary>
  public async Task<bool> IsErasedBlockAsync(uint address)
  {
    return await ScanAsync(address, BlockSize) == 0;
  }

  /// <summary>
  /// Reads a range. Address and length should be multiples of 8 for the erased-block shortcut to
  /// apply to every byte.
  /// </summary>
  public async Task<MyoMemoryBlock> ReadAsync(
    uint address,
    uint length,
    IProgress<MyoMemoryReadProgress>? progress = null,
    CancellationToken cancellationToken = default)
  {
    EnsureOpen();
    CheckRange(address, length);
    long startRequests = Requests;
    var bytes = new byte[length];
    var ambiguous = new SortedSet<uint>();
    var notErased = new HashSet<uint>();
    var stopwatch = Stopwatch.StartNew();
    uint end = address + length;

    for (uint position = address; position < end;)
    {
      cancellationToken.ThrowIfCancellationRequested();
      uint chunk = Math.Min((uint)Math.Max(64, _options.ProbeLength), end - position);
      // One request tells whether every byte in the chunk is 0x00 or 0xFF, because the firmware's
      // Fletcher16 is computed modulo 255 and those two values are congruent there.
      bool allCongruentToZero = chunk >= 64 && await ChecksumAsync(position, chunk) == BlankChecksum((int)chunk);
      if (allCongruentToZero)
      {
        await ReadErasedCandidateAsync(position, chunk, address, bytes, ambiguous, notErased, cancellationToken);
      }
      else
      {
        await ReadPairsAsync(position, chunk, address, bytes, ambiguous, cancellationToken);
      }
      position += chunk;
      Report(progress, position, position - address, length, Describe(position - address, length, stopwatch));
    }

    await ResolveErasedBlocksAsync(address, length, bytes, ambiguous, notErased, progress, stopwatch, cancellationToken);
    await ResolveByWriteCompareAsync(address, length, bytes, ambiguous, progress, stopwatch, cancellationToken);
    bool verified = await VerifyAsync(address, length, bytes, progress, stopwatch, cancellationToken);
    return new MyoMemoryBlock(address, bytes, ambiguous.ToArray(), verified, Requests - startRequests);
  }

  /// <summary>
  /// Splits the firmware's Fletcher16 over two bytes back into those bytes, modulo 255. A result of 0
  /// means the byte is either 0x00 or 0xFF, which this checksum cannot distinguish.
  /// </summary>
  public static (int First, int Second) DecodeBytePair(ushort checksum)
  {
    // Seeded with 0xFFFF, two bytes give sum1 = 255 + b0 + b1 and sum2 = 765 + 2 * b0 + b1, and the
    // firmware folds both into 1..255 where 255 stands for 0.
    int sum1 = (checksum & 0xFF) % 255;
    int sum2 = (checksum >> 8) % 255;
    int first = ((sum2 - sum1) % 255 + 255) % 255;
    int second = ((2 * sum1 - sum2) % 255 + 255) % 255;
    return (first, second);
  }

  /// <summary>
  /// A chunk that looks like nothing but 0x00 and 0xFF: only Scan can tell those apart, block by
  /// block. Blocks that turn out not to be erased are still read byte by byte, both because the
  /// chunk probe can match real content by chance (one checksum in 65536) and because such a block
  /// may hold 0x00 bytes next to 0xFF ones.
  /// </summary>
  private async Task ReadErasedCandidateAsync(
    uint position,
    uint chunk,
    uint origin,
    byte[] bytes,
    SortedSet<uint> ambiguous,
    HashSet<uint> notErased,
    CancellationToken cancellationToken)
  {
    for (uint block = position; block < position + chunk; block += BlockSize)
    {
      cancellationToken.ThrowIfCancellationRequested();
      uint blockLength = Math.Min((uint)BlockSize, position + chunk - block);
      if (blockLength == BlockSize && await IsErasedBlockAsync(block))
      {
        for (uint a = block; a < block + BlockSize; a++)
        {
          bytes[a - origin] = 0xFF;
        }
        continue;
      }
      if (blockLength == BlockSize)
      {
        notErased.Add(block);
      }
      await ReadPairsAsync(block, blockLength, origin, bytes, ambiguous, cancellationToken);
    }
  }

  private async Task ReadPairsAsync(
    uint position,
    uint chunk,
    uint origin,
    byte[] bytes,
    SortedSet<uint> ambiguous,
    CancellationToken cancellationToken)
  {
    uint end = position + chunk;
    for (uint a = position; a < end; a += 2)
    {
      cancellationToken.ThrowIfCancellationRequested();
      if (a + 1 < end)
      {
        var (first, second) = DecodeBytePair(await ChecksumAsync(a, 2));
        Store(a, first, origin, bytes, ambiguous);
        Store(a + 1, second, origin, bytes, ambiguous);
      }
      else
      {
        ushort checksum = await ChecksumAsync(a, 1);
        Store(a, (checksum & 0xFF) % 255, origin, bytes, ambiguous);
      }
    }
  }

  private static void Store(uint address, int valueModulo255, uint origin, byte[] bytes, SortedSet<uint> ambiguous)
  {
    if (valueModulo255 == 0)
    {
      bytes[address - origin] = 0x00;
      ambiguous.Add(address);
    }
    else
    {
      bytes[address - origin] = (byte)valueModulo255;
    }
  }

  /// <summary>Proves whole 8-byte blocks of undecided bytes to be erased, which settles them as 0xFF.</summary>
  private async Task ResolveErasedBlocksAsync(
    uint address,
    uint length,
    byte[] bytes,
    SortedSet<uint> ambiguous,
    HashSet<uint> notErased,
    IProgress<MyoMemoryReadProgress>? progress,
    Stopwatch stopwatch,
    CancellationToken cancellationToken)
  {
    if (ambiguous.Count == 0)
    {
      return;
    }
    var blocks = new SortedSet<uint>();
    foreach (uint a in ambiguous)
    {
      blocks.Add(a / BlockSize * BlockSize);
    }
    foreach (uint block in blocks)
    {
      cancellationToken.ThrowIfCancellationRequested();
      if (block < address || block + BlockSize > address + length || notErased.Contains(block))
      {
        continue;
      }
      bool allUndecided = true;
      for (uint a = block; a < block + BlockSize; a++)
      {
        if (!ambiguous.Contains(a))
        {
          allUndecided = false;
          break;
        }
      }
      // A block holding a byte that is known to be neither 0x00 nor 0xFF cannot be erased, so asking
      // would tell us nothing.
      if (!allUndecided || !await IsErasedBlockAsync(block))
      {
        continue;
      }
      for (uint a = block; a < block + BlockSize; a++)
      {
        bytes[a - address] = 0xFF;
        ambiguous.Remove(a);
      }
      Report(progress, block, length, length, $"Settling undecided bytes, {ambiguous.Count} left{Elapsed(stopwatch)}");
    }
  }

  /// <summary>
  /// Settles the remaining bytes in the mirrored area exactly. A Write into a block of the spare bank
  /// that is not erased is compared against what is there instead of being programmed, and the armband
  /// reports a mismatch as a failure, so offering a candidate value answers "is this the byte".
  /// </summary>
  private async Task ResolveByWriteCompareAsync(
    uint address,
    uint length,
    byte[] bytes,
    SortedSet<uint> ambiguous,
    IProgress<MyoMemoryReadProgress>? progress,
    Stopwatch stopwatch,
    CancellationToken cancellationToken)
  {
    if (ambiguous.Count == 0 || !CanReadExactly || address + length > MyoFlashLayout.MirrorEnd)
    {
      return;
    }
    if (!await MirrorMatchesAsync(address, length))
    {
      return;
    }
    var blocks = new SortedSet<uint>();
    foreach (uint a in ambiguous)
    {
      blocks.Add(a / WriteBlockSize * WriteBlockSize);
    }
    foreach (uint block in blocks)
    {
      cancellationToken.ThrowIfCancellationRequested();
      if (block < address || block + WriteBlockSize > address + length)
      {
        continue;
      }
      var undecided = new List<uint>();
      bool knownNotErased = false;
      for (uint a = block; a < block + WriteBlockSize; a++)
      {
        if (ambiguous.Contains(a))
        {
          undecided.Add(a);
        }
        else if (bytes[a - address] != 0xFF)
        {
          knownNotErased = true;
        }
      }
      // Without a byte that is certainly not 0xFF the spare bank's block may be erased, and then the
      // armband would program the candidate instead of comparing it, which proves nothing.
      if (!knownNotErased || undecided.Count == 0 || undecided.Count > 4)
      {
        continue;
      }
      int candidates = 1 << undecided.Count;
      if (candidates > _options.MaxWriteCompareCandidates)
      {
        continue;
      }
      var probe = new byte[WriteBlockSize];
      Array.Copy(bytes, block - address, probe, 0, WriteBlockSize);
      for (int candidate = 0; candidate < candidates; candidate++)
      {
        for (int bit = 0; bit < undecided.Count; bit++)
        {
          probe[undecided[bit] - block] = (candidate & (1 << bit)) == 0 ? (byte)0x00 : (byte)0xFF;
        }
        if (!await WriteMatchesAsync(block, probe))
        {
          continue;
        }
        for (int bit = 0; bit < undecided.Count; bit++)
        {
          uint a = undecided[bit];
          bytes[a - address] = probe[a - block];
          ambiguous.Remove(a);
        }
        break;
      }
      Report(progress, block, length, length, $"Settling undecided bytes exactly, {ambiguous.Count} left{Elapsed(stopwatch)}");
    }
  }

  /// <summary>Offers the armband 16 bytes for a block of the spare bank and reports whether they matched.</summary>
  private async Task<bool> WriteMatchesAsync(uint mirroredAddress, byte[] data)
  {
    // In the spare bank the mirrored copy of flash address a sits at 0x80000 + a, so the image offset
    // an OTA write takes is the address itself.
    await SendAsync(MyoCommands.OtaWrite(mirroredAddress, data));
    var info = await ReadInfoAsync();
    if (info.OtaState != OtaState.Failed)
    {
      return true;
    }
    if (info.OtaError != OtaError.WriteTwice)
    {
      throw new MyoMemoryReadException(
        $"The armband failed on a comparison at 0x{mirroredAddress:X} ({info.OtaError}).");
    }
    // A mismatch leaves the session failed; the staged copy is untouched, so it can be reopened.
    await SendAsync(MyoCommands.OtaAbort());
    await ReadInfoAsync();
    await SendAsync(MyoCommands.OtaContinue());
    info = await ReadInfoAsync();
    if (info.OtaState is not (OtaState.Ready or OtaState.Scanned or OtaState.Writing))
    {
      throw new MyoMemoryReadException(
        $"The read session could not be reopened after a comparison (state {info.OtaState}).");
    }
    return false;
  }

  /// <summary>Checks that the spare bank still mirrors the live flash over the range being read.</summary>
  private async Task<bool> MirrorMatchesAsync(uint address, uint length)
  {
    for (uint position = address; position < address + length;)
    {
      uint window = Math.Min((uint)_options.VerifyLength, address + length - position);
      ushort live = await ChecksumAsync(position, window);
      ushort staged = await ChecksumAsync(MyoFlashLayout.BankSize + position, window);
      if (live != staged)
      {
        return false;
      }
      position += window;
    }
    return true;
  }

  private async Task<bool> VerifyAsync(
    uint address,
    uint length,
    byte[] bytes,
    IProgress<MyoMemoryReadProgress>? progress,
    Stopwatch stopwatch,
    CancellationToken cancellationToken)
  {
    for (uint position = address; position < address + length;)
    {
      cancellationToken.ThrowIfCancellationRequested();
      uint window = Math.Min((uint)_options.VerifyLength, address + length - position);
      ushort expected = FirmwareChecksums.Fletcher16(
        FirmwareChecksums.Fletcher16Seed,
        bytes.AsSpan((int)(position - address), (int)window));
      if (await ChecksumAsync(position, window) != expected)
      {
        return false;
      }
      position += window;
      Report(progress, position, length, length, $"Verifying what was read{Elapsed(stopwatch)}");
    }
    return true;
  }

  private ushort BlankChecksum(int length)
  {
    if (!_blankChecksums.TryGetValue(length, out ushort checksum))
    {
      var blank = new byte[length];
      Array.Fill(blank, (byte)0xFF);
      checksum = FirmwareChecksums.Fletcher16(FirmwareChecksums.Fletcher16Seed, blank);
      _blankChecksums[length] = checksum;
    }
    return checksum;
  }

  private async Task SendAsync(byte[] command)
  {
    Requests++;
    await _device.SendCommandAsync(command);
  }

  private async Task<FirmwareInfo> ReadInfoAsync()
  {
    return await _device.RefreshFirmwareInfoAsync();
  }

  private void EnsureOpen()
  {
    if (!IsOpen)
    {
      throw new MyoMemoryReadException("Open the read session first.");
    }
  }

  private void CheckRange(uint address, uint length)
  {
    if (length == 0)
    {
      throw new ArgumentOutOfRangeException(nameof(length), "Nothing to read.");
    }
    if (address + (long)length > uint.MaxValue)
    {
      throw new ArgumentOutOfRangeException(nameof(length), "The range wraps around the address space.");
    }
    bool flash = address + length <= MyoFlashLayout.FlashSize;
    bool ram = address >= 0x1FFF0000 && address + length <= 0x20010000;
    if (flash || (ram && _options.AllowRam))
    {
      return;
    }
    throw new MyoMemoryReadException(ram
      ? $"0x{address:X}..0x{address + length - 1:X} is RAM; set AllowRam to read it."
      : $"0x{address:X}..0x{address + length - 1:X} is neither flash nor RAM. Reading peripheral registers " +
        "can clear flags and empty FIFOs in the running firmware, so it is refused.");
  }

  /// <summary>
  /// Address to the offset the OTA commands take, which they add to the base of the spare bank. The
  /// addition wraps, so every address in the 32-bit space is reachable.
  /// </summary>
  private static uint ToImageOffset(uint address)
  {
    return unchecked(address - MyoFlashLayout.BankSize);
  }

  private static string Describe(long done, long total, Stopwatch stopwatch)
  {
    double fraction = total == 0 ? 1 : (double)done / total;
    string left = string.Empty;
    if (fraction > 0.02 && fraction < 1)
    {
      var remaining = TimeSpan.FromSeconds(stopwatch.Elapsed.TotalSeconds / fraction * (1 - fraction));
      left = $", about {remaining.TotalMinutes:F0} min {remaining.Seconds} s left";
    }
    return $"Read {done} of {total} bytes{left}";
  }

  private static string Elapsed(Stopwatch stopwatch)
  {
    return $" ({stopwatch.Elapsed.TotalMinutes:F0} min {stopwatch.Elapsed.Seconds} s)";
  }

  private void Report(IProgress<MyoMemoryReadProgress>? progress, uint address, long done, long total, string message)
  {
    progress?.Report(new MyoMemoryReadProgress(address, done, total, Requests, message));
  }
}

/// <summary>A read that could not be carried out. The armband keeps running its firmware either way.</summary>
public sealed class MyoMemoryReadException : Exception
{
  public MyoMemoryReadException(string message)
    : base(message)
  {
  }
}
