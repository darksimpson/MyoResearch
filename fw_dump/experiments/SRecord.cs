using System.Text;

namespace DumpResearch;

/// <summary>
/// Motorola S-record writing and reading, of the shape Thalmic shipped as ".hex". Copied from the
/// library's SRecordWriter and reduced to what experiment 5 needs.
/// </summary>
public static class SRecord
{
  public static int Write(
    string path,
    uint baseAddress,
    ReadOnlySpan<byte> bytes,
    uint? startAddress = null,
    string header = "",
    int bytesPerRecord = 32,
    int minimumGap = 16)
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

  /// <summary>Reads data records into a 0xFF-filled image of the given size.</summary>
  public static byte[] Read(string path, int size)
  {
    var image = new byte[size];
    Array.Fill(image, (byte)0xFF);
    foreach (string line in File.ReadLines(path))
    {
      string text = line.Trim();
      if (text.Length < 4 || text[0] != 'S' || text[1] is not ('1' or '2' or '3'))
      {
        continue;
      }
      int addressBytes = text[1] - '0' + 1;
      byte[] raw = Convert.FromHexString(text.AsSpan(2));
      if (raw[0] != raw.Length - 1)
      {
        throw new InvalidDataException($"Bad record length: {line}");
      }
      int sum = 0;
      for (int i = 0; i < raw.Length - 1; i++)
      {
        sum += raw[i];
      }
      if ((byte)~sum != raw[^1])
      {
        throw new InvalidDataException($"Bad record checksum: {line}");
      }
      uint address = 0;
      for (int i = 0; i < addressBytes; i++)
      {
        address = (address << 8) | raw[1 + i];
      }
      int length = raw.Length - addressBytes - 2;
      raw.AsSpan(1 + addressBytes, length).CopyTo(image.AsSpan((int)address, length));
    }
    return image;
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
