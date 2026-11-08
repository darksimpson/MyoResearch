using System.Text;

namespace MyoSharp.Firmware;

/// <summary>
/// Writes Motorola S-record files of the shape Thalmic shipped as ".hex": an S0 header, S2 data
/// records with 24-bit addresses, an S5 count and an S8 start address. Runs of 0xFF are left out,
/// exactly as the original files leave out the gaps in the image, because both ways of flashing erase
/// the application area first and so leave those addresses at 0xFF.
/// </summary>
public static class SRecordWriter
{
  public const int DefaultBytesPerRecord = 32;

  /// <summary>Shortest run of 0xFF that is left out of the file rather than written.</summary>
  public const int DefaultMinimumGap = 16;

  /// <summary>Writes the bytes and returns how many data records were produced.</summary>
  public static int Write(
    string path,
    uint baseAddress,
    ReadOnlySpan<byte> bytes,
    uint? startAddress = null,
    string header = "",
    int bytesPerRecord = DefaultBytesPerRecord,
    int minimumGap = DefaultMinimumGap)
  {
    var text = new StringBuilder();
    AppendRecord(text, '0', 0, Encoding.ASCII.GetBytes(header), 2);
    int records = 0;
    int position = 0;
    while (position < bytes.Length)
    {
      int blank = BlankRun(bytes, position);
      if (blank >= minimumGap)
      {
        position += blank;
        continue;
      }
      int end = position;
      while (end < bytes.Length)
      {
        int gap = BlankRun(bytes, end);
        if (gap >= minimumGap)
        {
          break;
        }
        end += gap > 0 ? gap : 1;
      }
      for (int offset = position; offset < end; offset += bytesPerRecord)
      {
        int length = Math.Min(bytesPerRecord, end - offset);
        AppendRecord(text, '2', baseAddress + (uint)offset, bytes.Slice(offset, length), 3);
        records++;
      }
      position = end;
    }
    AppendRecord(text, '5', (uint)records, ReadOnlySpan<byte>.Empty, 2);
    if (startAddress.HasValue)
    {
      AppendRecord(text, '8', startAddress.Value, ReadOnlySpan<byte>.Empty, 3);
    }
    File.WriteAllText(path, text.ToString(), Encoding.ASCII);
    return records;
  }

  private static int BlankRun(ReadOnlySpan<byte> bytes, int position)
  {
    int run = 0;
    while (position + run < bytes.Length && bytes[position + run] == 0xFF)
    {
      run++;
    }
    return run;
  }

  private static void AppendRecord(StringBuilder text, char type, uint address, ReadOnlySpan<byte> data, int addressBytes)
  {
    var raw = new List<byte> { (byte)(addressBytes + data.Length + 1) };
    for (int i = addressBytes - 1; i >= 0; i--)
    {
      raw.Add((byte)(address >> (8 * i)));
    }
    foreach (byte b in data)
    {
      raw.Add(b);
    }
    int sum = 0;
    foreach (byte b in raw)
    {
      sum += b;
    }
    raw.Add((byte)~sum);
    text.Append('S').Append(type).Append(Convert.ToHexString(raw.ToArray())).Append("\r\n");
  }
}
