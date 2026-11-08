using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace MyoSharp.Firmware;

/// <summary>What one dumped region produced.</summary>
public sealed class MyoFlashDumpResult
{
  internal MyoFlashDumpResult(MyoFlashRegion region, string path, MyoMemoryBlock block)
  {
    Region = region;
    Path = path;
    Block = block;
  }

  public MyoFlashRegion Region { get; }

  /// <summary>File the dump was written to.</summary>
  public string Path { get; }

  public MyoMemoryBlock Block { get; }

  /// <summary>Undecided bytes taken from a reference image instead of the armband.</summary>
  public int ResolvedFromReference { get; internal set; }

  /// <summary>Bytes that differ from the reference image, where one was given.</summary>
  public int ReferenceDifferences { get; internal set; }

  /// <summary>Bytes still undecided (either 0x00 or 0xFF) after everything was tried.</summary>
  public int Undecided => Block.Unresolved.Count;

  /// <summary>
  /// For an application dump: whether the file passes the checks the bootloader makes before it starts
  /// an image, which is what makes it safe to flash back. Null for other regions.
  /// </summary>
  public bool? Flashable { get; internal set; }

  public string? FlashableReason { get; internal set; }

  public string Sha256 { get; internal set; } = string.Empty;

  public bool IsExact => Block.ChecksumVerified && Undecided == 0;

  public override string ToString()
  {
    var text = new StringBuilder();
    text.Append(CultureInfo.InvariantCulture, $"{Region.Name}: 0x{Region.Address:X}..0x{Region.End - 1:X}, {Block.Bytes.Length} bytes");
    text.Append(Block.ChecksumVerified ? ", checksums matched" : ", CHECKSUMS DID NOT MATCH");
    if (ResolvedFromReference > 0)
    {
      text.Append(CultureInfo.InvariantCulture, $", {ResolvedFromReference} bytes taken from the reference");
    }
    text.Append(Undecided == 0
      ? ", every byte settled"
      : string.Create(CultureInfo.InvariantCulture, $", {Undecided} bytes still undecided (0x00 or 0xFF)"));
    if (Flashable == true)
    {
      text.Append(", passes the bootloader's own checks");
    }
    else if (Flashable == false)
    {
      text.Append(CultureInfo.InvariantCulture, $", NOT flashable: {FlashableReason}");
    }
    return text.ToString();
  }
}

/// <summary>
/// Dumps the armband's flash to files through <see cref="MyoMemoryReader"/>: the factory calibration
/// and the user settings, any classifier slot, the bootloader, and the application firmware as an
/// S-record file that can be flashed back over USB or BLE.
/// <para>
/// The factory calibration is the one part of a Myo that exists nowhere else and cannot be restored
/// without a hardware debugger, so it is the dump worth keeping. It, the user settings, the
/// bootloader and classifier slot 0 all lie in the area an update mirrors into the spare bank, which
/// is what lets them be read exactly. The application and classifier slots 1 to 4 lie outside it, so
/// there bytes that are either 0x00 or 0xFF can only be settled from a reference image; for the
/// application the result is checked against the image's own checksum records, the same check the
/// bootloader makes, so a dump either proves itself flashable or says why it does not.
/// </para>
/// </summary>
public sealed class MyoFlashDumper : IAsyncDisposable
{
  private readonly MyoDevice _device;

  public MyoFlashDumper(MyoDevice device, MyoMemoryReadOptions? options = null)
  {
    _device = device;
    Reader = new MyoMemoryReader(device, options);
  }

  public MyoMemoryReader Reader { get; }

  /// <summary>Short name for the armband, used as a file name prefix.</summary>
  public string DeviceTag
  {
    get
    {
      string mac = _device.FirmwareInfo?.MacAddress.Replace(":", string.Empty) ?? "unknown";
      return $"myo-{mac}";
    }
  }

  public Task OpenAsync(IProgress<MyoMemoryReadProgress>? progress = null, CancellationToken cancellationToken = default)
  {
    return Reader.OpenAsync(progress, cancellationToken);
  }

  public Task CloseAsync()
  {
    return Reader.CloseAsync();
  }

  public async ValueTask DisposeAsync()
  {
    await Reader.DisposeAsync();
  }

  /// <summary>
  /// Reads one region and writes it to the directory: an S-record file for the application, raw bytes
  /// for everything else. A reference image is consulted only for bytes the armband cannot pin down.
  /// </summary>
  public async Task<MyoFlashDumpResult> DumpAsync(
    MyoFlashRegion region,
    string directory,
    MyoFirmwareImage? reference = null,
    IProgress<MyoMemoryReadProgress>? progress = null,
    CancellationToken cancellationToken = default)
  {
    Directory.CreateDirectory(directory);
    var block = await Reader.ReadAsync(region.Address, region.Length, progress, cancellationToken);
    string path = Path.Combine(directory, $"{DeviceTag}-{region.FileName}");
    var result = new MyoFlashDumpResult(region, path, block);

    if (reference != null)
    {
      ApplyReference(region, block, reference, result);
    }
    if (region.FileName.EndsWith(".hex", StringComparison.OrdinalIgnoreCase))
    {
      WriteSRecords(region, block, path);
      CheckFlashable(path, result);
    }
    else
    {
      File.WriteAllBytes(path, block.Bytes);
    }
    result.Sha256 = Convert.ToHexString(SHA256.HashData(block.Bytes));
    return result;
  }

  /// <summary>Writes a text report next to the dumps describing how each one came out.</summary>
  public string WriteReport(string directory, IEnumerable<MyoFlashDumpResult> results)
  {
    var text = new StringBuilder();
    var info = _device.FirmwareInfo;
    text.AppendLine("Myo flash dump");
    text.AppendLine(string.Create(CultureInfo.InvariantCulture, $"Taken    : {DateTime.Now:yyyy-MM-dd HH:mm:ss}"));
    text.AppendLine($"Armband  : {_device.Name}");
    text.AppendLine($"Address  : {info?.MacAddress}");
    text.AppendLine($"Firmware : {_device.FirmwareVersion}");
    text.AppendLine($"SKU      : {info?.Sku}");
    text.AppendLine();
    text.AppendLine("Read with the firmware update commands: a Fletcher16 over two bytes determines both of them");
    text.AppendLine("modulo 255, Scan proves 8-byte blocks erased, and below 0xF000 a compared write settles the");
    text.AppendLine("rest exactly. Modulo 255 the values 0x00 and 0xFF are the same, so outside that area bytes");
    text.AppendLine("that read as either are listed below unless a reference image settled them.");
    text.AppendLine();
    foreach (var result in results)
    {
      text.AppendLine(result.ToString());
      text.AppendLine($"  file   : {Path.GetFileName(result.Path)}");
      text.AppendLine($"  sha256 of the bytes read: {result.Sha256}");
      if (result.ReferenceDifferences > 0)
      {
        text.AppendLine($"  differs from the reference image in {result.ReferenceDifferences} bytes");
      }
      if (result.Undecided > 0)
      {
        text.AppendLine($"  undecided addresses ({result.Undecided}), first few: " +
          string.Join(" ", result.Block.Unresolved.Take(16).Select(a => $"0x{a:X}")));
      }
      if (result.Region.Address == MyoFlashLayout.UserSettingsAddress)
      {
        AppendUserSettings(text, result.Block);
      }
      if (result.Region.Address == MyoFlashLayout.FactorySettingsAddress)
      {
        AppendFactorySettings(text, result.Block);
      }
      text.AppendLine();
    }
    string path = Path.Combine(directory, $"{DeviceTag}-dump-report.txt");
    File.WriteAllText(path, text.ToString());
    return path;
  }

  private static void ApplyReference(MyoFlashRegion region, MyoMemoryBlock block, MyoFirmwareImage reference, MyoFlashDumpResult result)
  {
    var undecided = new HashSet<uint>(block.Unresolved);
    var left = new List<uint>();
    foreach (uint address in block.Unresolved)
    {
      // Only addresses the reference file actually defines can settle anything: elsewhere it holds
      // 0xFF simply because nothing was there, which would silently erase real content.
      if (address < MyoFirmwareImage.ApplicationStart || address >= MyoFirmwareImage.ApplicationEnd
        || !reference.IsDefined(address))
      {
        left.Add(address);
        continue;
      }
      byte candidate = reference[address];
      // Only 0x00 and 0xFF are consistent with what the armband reported; anything else means the
      // reference differs here and cannot settle the byte.
      if (candidate is 0x00 or 0xFF)
      {
        block.Bytes[address - region.Address] = candidate;
        result.ResolvedFromReference++;
      }
      else
      {
        left.Add(address);
      }
    }
    block.Unresolved = left;

    int differences = 0;
    for (uint address = region.Address; address < region.End; address++)
    {
      if (address >= MyoFirmwareImage.ApplicationStart && address < MyoFirmwareImage.ApplicationEnd
        && reference.IsDefined(address) && !undecided.Contains(address)
        && block.Bytes[address - region.Address] != reference[address])
      {
        differences++;
      }
    }
    result.ReferenceDifferences = differences;
  }

  private static void WriteSRecords(MyoFlashRegion region, MyoMemoryBlock block, string path)
  {
    uint? start = null;
    if (region.Address == MyoFlashLayout.ApplicationAddress && block.Bytes.Length >= 8)
    {
      start = BinaryPrimitives.ReadUInt32LittleEndian(block.Bytes.AsSpan(4));
    }
    SRecordWriter.Write(path, region.Address, block.Bytes, start, Path.GetFileName(path));
  }

  private static void CheckFlashable(string path, MyoFlashDumpResult result)
  {
    try
    {
      var image = MyoFirmwareImage.Load(path);
      result.Flashable = true;
      result.FlashableReason = $"loads as {image.Version}";
    }
    catch (Exception ex)
    {
      result.Flashable = false;
      result.FlashableReason = ex.Message;
    }
  }

  /// <summary>
  /// Decode of the factory record, so the dump can be eyeballed: biases that read as plausible small
  /// numbers are a good sign that the read came out right.
  /// </summary>
  private static void AppendFactorySettings(StringBuilder text, MyoMemoryBlock block)
  {
    if (block.Bytes.Length < MyoFlashLayout.FactoryRecordLength)
    {
      return;
    }
    text.AppendLine($"  gyro bias       : {ReadTriple(block.Bytes, MyoFlashLayout.FactoryGyroBiasOffset)}");
    text.AppendLine($"  accel bias      : {ReadTriple(block.Bytes, MyoFlashLayout.FactoryAccelBiasOffset)}");
    text.AppendLine("  led calibration : " +
      Convert.ToHexString(block.Bytes.AsSpan(MyoFlashLayout.FactoryLedCalibrationOffset, 9)));
    byte emg = block.Bytes[MyoFlashLayout.FactoryEmgCheckedOffset];
    text.AppendLine($"  emg self check  : 0x{emg:X2} ({(emg == 0xFF ? "never verified" : "verified at the factory")})");
  }

  private static string ReadTriple(byte[] bytes, int offset)
  {
    var parts = new string[3];
    for (int i = 0; i < 3; i++)
    {
      parts[i] = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset + i * 4)).ToString(CultureInfo.InvariantCulture);
    }
    return string.Join(" ", parts);
  }

  /// <summary>Best-effort decode of the documented user settings record, so the dump is readable.</summary>
  private static void AppendUserSettings(StringBuilder text, MyoMemoryBlock block)
  {
    const uint recordOffset = 8;
    if (block.Bytes.Length < recordOffset + 34)
    {
      return;
    }
    var name = new StringBuilder();
    for (int i = 0; i < 32; i++)
    {
      byte b = block.Bytes[recordOffset + i];
      if (b is 0x00 or 0xFF)
      {
        break;
      }
      name.Append((char)b);
    }
    uint counter = BinaryPrimitives.ReadUInt32LittleEndian(block.Bytes.AsSpan(0));
    byte type = block.Bytes[recordOffset + 32];
    byte index = block.Bytes[recordOffset + 33];
    text.AppendLine($"  write counter   : {counter}");
    text.AppendLine($"  stored name     : {(name.Length == 0 ? "(unset, the firmware then reports Myo)" : name.ToString())}");
    text.AppendLine($"  classifier type : 0x{type:X2} ({(type == 1 ? "custom" : "built-in")})");
    text.AppendLine($"  classifier slot : {index}");
  }
}
