using System.Diagnostics;
using DumpResearch;

// Reproduces every measurement quoted in ..\README.md. Run with no arguments, or pass a path to the
// raw application image to use a different one.
//
//   dotnet run
//   dotnet run -c Release                 (experiment 4 is much faster this way)
//   dotnet run -- <path to a flash image at 0x10000>

string imagePath = args.Length > 0
  ? args[0]
  : FindDefaultImage();
if (!File.Exists(imagePath))
{
  Console.WriteLine($"Image not found: {imagePath}");
  return 1;
}
var image = File.ReadAllBytes(imagePath);
const uint ImageBase = 0x10000;
Console.WriteLine($"Image: {imagePath}");
Console.WriteLine($"       {image.Length} bytes at 0x{ImageBase:X}");
Console.WriteLine();

PairDecoding();
AmbiguityStatistics(image);
ScanReach(image);
FoldIsIdentityModulo255(image);
SRecordRoundTrip(image, ImageBase);
return 0;

static string FindDefaultImage()
{
  var directory = new DirectoryInfo(AppContext.BaseDirectory);
  while (directory != null)
  {
    string candidate = Path.Combine(directory.FullName, "Firmware", "research", "binaries", "kinetis_1.5.1970_at_0x10000.bin");
    if (File.Exists(candidate))
    {
      return candidate;
    }
    directory = directory.Parent;
  }
  throw new FileNotFoundException("Pass the path to a decoded Kinetis application binary loaded at 0x10000.");
}

// Experiment 1. A Fletcher16 over two bytes determines both of them modulo 255.
static void PairDecoding()
{
  Console.WriteLine("1. Pair decoding");
  int wrong = 0;
  int withUndecided = 0;
  for (int first = 0; first <= 255; first++)
  {
    for (int second = 0; second <= 255; second++)
    {
      ushort checksum = Firmware.Fletcher16(new[] { (byte)first, (byte)second });
      var (a, b) = Firmware.DecodeBytePair(checksum);
      if (a != first % 255 || b != second % 255)
      {
        wrong++;
      }
      if (a == 0 || b == 0)
      {
        withUndecided++;
      }
    }
  }
  Console.WriteLine($"   pairs decoded wrong        : {wrong} of 65536");
  Console.WriteLine($"   pairs with an undecided byte: {withUndecided} (every pair holding a 0x00 or 0xFF)");

  var single = new Dictionary<ushort, List<int>>();
  for (int value = 0; value <= 255; value++)
  {
    ushort checksum = Firmware.Fletcher16(new[] { (byte)value });
    if (!single.TryGetValue(checksum, out var list))
    {
      single[checksum] = list = new List<int>();
    }
    list.Add(value);
  }
  Console.WriteLine($"   single bytes: {single.Count} distinct checksums for 256 values");
  foreach (var entry in single.Where(e => e.Value.Count > 1))
  {
    Console.WriteLine($"     collision 0x{entry.Key:X4}: {string.Join(", ", entry.Value.Select(v => $"0x{v:X2}"))}");
  }
  Console.WriteLine();
}

// Experiment 2. How often the blind spot actually bites, and the cost of guessing 0x00.
static void AmbiguityStatistics(byte[] image)
{
  Console.WriteLine("2. Ambiguity in the real image");
  int zero = image.Count(b => b == 0x00);
  int ff = image.Count(b => b == 0xFF);
  int erasedBlocks = 0;
  int insideNonBlank = 0;
  int ffInsideNonBlank = 0;
  for (int block = 0; block + 8 <= image.Length; block += 8)
  {
    bool erased = true;
    for (int i = 0; i < 8 && erased; i++)
    {
      erased = image[block + i] == 0xFF;
    }
    if (erased)
    {
      erasedBlocks++;
      continue;
    }
    for (int i = 0; i < 8; i++)
    {
      if (image[block + i] == 0x00)
      {
        insideNonBlank++;
      }
      else if (image[block + i] == 0xFF)
      {
        insideNonBlank++;
        ffInsideNonBlank++;
      }
    }
  }
  Console.WriteLine($"   bytes that are 0x00: {zero}, that are 0xFF: {ff}");
  Console.WriteLine($"   aligned 8-byte blocks that are entirely 0xFF: {erasedBlocks} ({erasedBlocks * 8} bytes)");
  Console.WriteLine($"   undecided inside non-erased blocks: {insideNonBlank} " +
    $"({100.0 * insideNonBlank / image.Length:F1}% of the image)");
  Console.WriteLine($"   of those, really 0xFF, so wrong if guessed as 0x00: {ffInsideNonBlank}");

  // The probe that spots a chunk holding nothing but 0x00 and 0xFF, and its false positive rate.
  var rng = new Random(7);
  var blank = new byte[512];
  Array.Fill(blank, (byte)0xFF);
  ushort blankChecksum = Firmware.Fletcher16(blank);
  int falsePositives = 0;
  for (int trial = 0; trial < 20000; trial++)
  {
    var buffer = new byte[512];
    rng.NextBytes(buffer);
    if (Firmware.Fletcher16(buffer) == blankChecksum)
    {
      falsePositives++;
    }
  }
  int missed = 0;
  for (int trial = 0; trial < 2000; trial++)
  {
    var buffer = new byte[512];
    for (int i = 0; i < buffer.Length; i++)
    {
      buffer[i] = rng.Next(2) == 0 ? (byte)0x00 : (byte)0xFF;
    }
    if (Firmware.Fletcher16(buffer) != blankChecksum)
    {
      missed++;
    }
  }
  Console.WriteLine($"   512-byte probe: {falsePositives} of 20000 random chunks matched by chance, " +
    $"{missed} of 2000 genuine 0x00/0xFF mixes missed");
  Console.WriteLine();
}

// Experiment 3. What the Scan command can settle, since its 8-byte window may start anywhere.
static void ScanReach(byte[] image)
{
  Console.WriteLine("3. Reach of the Scan command");
  int n = image.Length;
  var undecided = new bool[n];
  int total = 0;
  for (int i = 0; i < n; i++)
  {
    undecided[i] = image[i] == 0x00 || image[i] == 0xFF;
    if (undecided[i])
    {
      total++;
    }
  }

  // What the shipped implementation did: aligned blocks whose eight bytes are all undecided.
  var aligned = (bool[])undecided.Clone();
  for (int block = 0; block + 8 <= n; block += 8)
  {
    bool all = true;
    for (int i = 0; i < 8 && all; i++)
    {
      all = undecided[block + i] && image[block + i] == 0xFF;
    }
    if (all)
    {
      for (int i = 0; i < 8; i++)
      {
        aligned[block + i] = false;
      }
    }
  }

  // The most any window placement can do: seed from windows that could be all 0xFF, then settle the
  // bytes next to seven confirmed 0xFF ones, whichever way the answer goes.
  var left = (bool[])undecided.Clone();
  var knownFf = new bool[n];
  long questions = 0;
  for (int start = 0; start + 8 <= n; start++)
  {
    bool plausible = true;
    for (int i = 0; i < 8 && plausible; i++)
    {
      plausible = left[start + i] || knownFf[start + i];
    }
    if (!plausible)
    {
      continue;
    }
    questions++;
    bool allFf = true;
    for (int i = 0; i < 8 && allFf; i++)
    {
      allFf = image[start + i] == 0xFF;
    }
    if (allFf)
    {
      for (int i = 0; i < 8; i++)
      {
        knownFf[start + i] = true;
        left[start + i] = false;
      }
    }
  }
  bool changed = true;
  while (changed)
  {
    changed = false;
    for (int p = 0; p < n; p++)
    {
      if (!left[p])
      {
        continue;
      }
      for (int start = Math.Max(0, p - 7); start + 8 <= n && start <= p; start++)
      {
        int confirmed = 0;
        for (int i = 0; i < 8; i++)
        {
          if (start + i != p && knownFf[start + i])
          {
            confirmed++;
          }
        }
        if (confirmed != 7)
        {
          continue;
        }
        questions++;
        left[p] = false;
        if (image[p] == 0xFF)
        {
          knownFf[p] = true;
        }
        changed = true;
        break;
      }
    }
  }
  Console.WriteLine($"   undecided to begin with                   : {total}");
  Console.WriteLine($"   left by aligned blocks (what was shipped) : {aligned.Count(x => x)}");
  Console.WriteLine($"   left by any placement plus edge growth    : {left.Count(x => x)} ({questions} questions)");
  Console.WriteLine($"   of those, really 0xFF: {Enumerable.Range(0, n).Count(i => left[i] && image[i] == 0xFF)}");
  Console.WriteLine();
}

// Experiment 4. Why a long, folding range does not help: the fold is the identity modulo 255.
static void FoldIsIdentityModulo255(byte[] image)
{
  Console.WriteLine("4. The overflow fold");
  var rng = new Random(5);
  int foldWrong = 0;
  for (int i = 0; i < 2000000; i++)
  {
    uint value = (uint)rng.NextInt64(0, uint.MaxValue);
    if (Firmware.Fold(value) % 255 != value % 255)
    {
      foldWrong++;
    }
  }
  Console.WriteLine($"   Fold(v) != v (mod 255) for {foldWrong} of 2000000 random values");

  int changed16 = 0;
  int changed32 = 0;
  long folds = 0;
  for (int trial = 0; trial < 200; trial++)
  {
    var buffer = new byte[20000];
    rng.NextBytes(buffer);
    uint sum1 = 0xFF;
    uint sum2 = 0xFF;
    foreach (byte b in buffer)
    {
      if (sum2 > 0x80000000)
      {
        sum2 = Firmware.Fold(sum2);
        sum1 = Firmware.Fold(sum1);
        folds++;
      }
      sum1 += b;
      sum2 += sum1;
    }
    int position = rng.Next(buffer.Length);
    buffer[position] = 0x00;
    ushort before16 = Firmware.Fletcher16(buffer);
    uint before32 = Firmware.Fletcher32(buffer);
    buffer[position] = 0xFF;
    if (Firmware.Fletcher16(buffer) != before16)
    {
      changed16++;
    }
    if (Firmware.Fletcher32(buffer) != before32)
    {
      changed32++;
    }
  }
  Console.WriteLine($"   200 ranges of 20000 bytes, {folds / 200} folds each on average");
  Console.WriteLine($"     Fletcher16 changed by a 0x00 to 0xFF flip: {changed16} of 200");
  Console.WriteLine($"     Fletcher32 changed by the same flip      : {changed32} of 200");

  // And on real positions: no range length separates the two hypotheses, although folds do occur.
  const int MaxRange = 65536;
  var candidates = new List<int>();
  for (int i = 0; i < image.Length; i++)
  {
    if ((image[i] == 0x00 || image[i] == 0xFF) && i + MaxRange <= image.Length)
    {
      candidates.Add(i);
    }
  }
  var sample = candidates.OrderBy(_ => rng.Next()).Take(3000).ToList();
  int distinguishable = 0;
  var firstFolds = new List<int>();
  var stopwatch = Stopwatch.StartNew();
  foreach (int position in sample)
  {
    var (differs, fold) = Probe(image.AsSpan(position, MaxRange));
    firstFolds.Add(fold);
    if (differs)
    {
      distinguishable++;
    }
  }
  Console.WriteLine($"   {sample.Count} real positions, ranges up to {MaxRange} bytes ({stopwatch.Elapsed.TotalSeconds:F0} s)");
  Console.WriteLine($"     first fold after a median of {firstFolds.Order().ElementAt(firstFolds.Count / 2)} bytes");
  Console.WriteLine($"     positions where some range length distinguishes 0x00 from 0xFF: {distinguishable}");
  Console.WriteLine();
}

/// <summary>Walks one range byte by byte, holding the first byte at 0x00 and at 0xFF in parallel.</summary>
static (bool Differs, int FirstFold) Probe(ReadOnlySpan<byte> data)
{
  uint s1a = 0xFF, s2a = 0xFF, s1b = 0xFF, s2b = 0xFF;
  int firstFold = 0;
  for (int i = 0; i < data.Length; i++)
  {
    if (s2a > 0x80000000)
    {
      s2a = Firmware.Fold(s2a);
      s1a = Firmware.Fold(s1a);
      firstFold = firstFold == 0 ? i + 1 : firstFold;
    }
    if (s2b > 0x80000000)
    {
      s2b = Firmware.Fold(s2b);
      s1b = Firmware.Fold(s1b);
      firstFold = firstFold == 0 ? i + 1 : firstFold;
    }
    s1a += i == 0 ? (byte)0x00 : data[i];
    s2a += s1a;
    s1b += i == 0 ? (byte)0xFF : data[i];
    s2b += s1b;
    if (Firmware.Reduce(s1a, s2a) != Firmware.Reduce(s1b, s2b))
    {
      return (true, firstFold);
    }
  }
  return (false, firstFold);
}

// Experiment 5. A dump written as S-records reproduces the image byte for byte.
static void SRecordRoundTrip(byte[] image, uint baseAddress)
{
  Console.WriteLine("5. S-record round trip");
  string path = Path.Combine(Path.GetTempPath(), "myo-dump-research-roundtrip.hex");
  uint start = BitConverter.ToUInt32(image, 4);
  int records = SRecord.Write(path, baseAddress, image, start, "round trip");
  var reloaded = SRecord.Read(path, (int)(baseAddress + image.Length));
  int differences = 0;
  for (int i = 0; i < image.Length; i++)
  {
    if (reloaded[baseAddress + i] != image[i])
    {
      differences++;
    }
  }
  Console.WriteLine($"   {records} data records, start address 0x{start:X8}");
  Console.WriteLine($"   bytes differing after reading it back: {differences}");
  Console.WriteLine($"   file: {path}");
  Console.WriteLine();
}
