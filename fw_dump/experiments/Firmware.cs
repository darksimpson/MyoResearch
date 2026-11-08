namespace DumpResearch;

/// <summary>
/// The two checksums of Myo firmware 1.5.1970, copied here so these experiments stand on their own:
/// Fletcher16 (fw_fletcher16 0x12D28) backs the OTA Checksum and Scan commands, Fletcher32
/// (fw_fletcher32 0x12D8A) backs the image verification records. Both keep 32-bit running sums and
/// fold them only when they are about to overflow, so they are reproduced step by step rather than
/// written in the usual modulo form.
/// </summary>
public static class Firmware
{
  public const ushort Fletcher16Seed = 0xFFFF;

  public static ushort Fletcher16(ReadOnlySpan<byte> data)
  {
    uint sum1 = Fletcher16Seed & 0xFF;
    uint sum2 = Fletcher16Seed >> 8;
    foreach (byte b in data)
    {
      if (sum2 > 0x80000000)
      {
        sum2 = Fold(sum2);
        sum1 = Fold(sum1);
      }
      sum1 += b;
      sum2 += sum1;
    }
    return Reduce(sum1, sum2);
  }

  /// <summary>
  /// The overflow fold. It splits the value into bytes and adds them, less 255 for each carry, so
  /// Fold(v) equals v modulo 255: the fold changes the magnitude of the running sums but never the
  /// result. Experiment 4 leans on this.
  /// </summary>
  public static uint Fold(uint value)
  {
    uint wide = ((value >> 16) + (value & 0xFFFF)) >> 8;
    uint narrow = (((value >> 16) & 0xFF) + (value & 0xFF)) & 0xFF;
    return wide + narrow;
  }

  /// <summary>Final reduction into 1..255 per sum, where 255 stands for zero.</summary>
  public static ushort Reduce(uint sum1, uint sum2)
  {
    while (sum2 > 0xFF)
    {
      sum2 = (sum2 >> 8) + (sum2 & 0xFF);
    }
    while (sum1 > 0xFF)
    {
      sum1 = (sum1 >> 8) + (sum1 & 0xFF);
    }
    return (ushort)(sum1 | (sum2 << 8));
  }

  public static uint Fletcher32(ReadOnlySpan<byte> data)
  {
    uint sum1 = 0xFFFF;
    uint sum2 = 0xFFFF;
    foreach (byte b in data)
    {
      if (sum2 >= 0x80000000)
      {
        sum2 = (sum2 & 0xFFFF) + (sum2 >> 16);
        sum1 = (sum1 & 0xFFFF) + (sum1 >> 16);
      }
      sum1 += b;
      sum2 += sum1;
    }
    while (sum2 > 0xFFFF)
    {
      sum2 = (sum2 & 0xFFFF) + (sum2 >> 16);
    }
    while (sum1 > 0xFFFF)
    {
      sum1 = (sum1 & 0xFFFF) + (sum1 >> 16);
    }
    return sum1 | (sum2 << 16);
  }

  /// <summary>
  /// Splits a Fletcher16 over two bytes back into those bytes, modulo 255. A result of 0 means the
  /// byte is either 0x00 or 0xFF, which this checksum cannot distinguish.
  /// </summary>
  public static (int First, int Second) DecodeBytePair(ushort checksum)
  {
    int sum1 = (checksum & 0xFF) % 255;
    int sum2 = (checksum >> 8) % 255;
    int first = ((sum2 - sum1) % 255 + 255) % 255;
    int second = ((2 * sum1 - sum2) % 255 + 255) % 255;
    return (first, second);
  }
}
