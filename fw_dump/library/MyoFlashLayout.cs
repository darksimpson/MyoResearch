namespace MyoSharp.Firmware;

/// <summary>A named area of the armband's flash, as used by the dumper.</summary>
public readonly record struct MyoFlashRegion(string Name, uint Address, uint Length, string FileName)
{
  public uint End => Address + Length;

  /// <summary>
  /// True when an OTA Prepare copies this area into the spare bank, which makes an exact read
  /// possible (see MyoMemoryReader).
  /// </summary>
  public bool IsMirrored => End <= MyoFlashLayout.MirrorEnd;

  public override string ToString()
  {
    return $"{Name} (0x{Address:X}..0x{End - 1:X})";
  }
}

/// <summary>
/// Where things live in the Kinetis flash of a Myo running firmware 1.5. The lower 512 KB bank is the
/// one the armband executes from and the one these addresses refer to; the upper bank is the staging
/// area an OTA update writes, and the banks are exchanged by the flash swap on commit.
/// </summary>
public static class MyoFlashLayout
{
  public const uint FlashSize = 0x100000;

  /// <summary>Size of one flash bank; the staging bank starts here.</summary>
  public const uint BankSize = 0x80000;

  public const uint SectorSize = 0x1000;

  /// <summary>
  /// An OTA Prepare copies 0x0..0xEFFF into the staging bank, so everything below this address exists
  /// twice while a read session is open.
  /// </summary>
  public const uint MirrorEnd = 0xF000;

  public const uint BootloaderAddress = 0x0;

  /// <summary>Flash reserved for the resident bootloader (the image itself is 22652 bytes).</summary>
  public const uint BootloaderLength = 0xA000;

  public const uint UserSettingsAddress = 0xC000;

  public const uint FactorySettingsAddress = 0xE000;

  /// <summary>
  /// The factory record the firmware keeps at the start of that sector (RAM copy 0x1FFFE3CC, written
  /// by fw_factory_settings_save 0x13FA4): three int32 gyroscope biases, three int32 accelerometer
  /// biases, nine bytes of LED calibration, the EMG self-check flag, and the byte the factory command
  /// 0x12 sets. The biases are the part that exists on no other Myo and that nothing can write back.
  /// </summary>
  public const int FactoryRecordLength = 40;

  public const int FactoryGyroBiasOffset = 0;

  public const int FactoryAccelBiasOffset = 12;

  public const int FactoryLedCalibrationOffset = 24;

  public const int FactoryEmgCheckedOffset = 33;

  /// <summary>Sector that records the state of a pending flash swap; never erased by a Prepare.</summary>
  public const uint SwapIndicatorAddress = 0xF000;

  public const uint ApplicationAddress = 0x10000;

  /// <summary>First address after the application area: the four upper classifier slots follow.</summary>
  public const uint ApplicationEnd = 0x76000;

  public const uint ClassifierSlotLength = 0x2000;

  /// <summary>Bytes of a classifier slot a model actually uses; the rest of the slot stays erased.</summary>
  public const int ClassifierBlobLength = 7696;

  public const int ClassifierSlotCount = 5;

  /// <summary>Slot 0 sits below the application, slots 1 to 4 at the top of the bank.</summary>
  public static uint ClassifierSlotAddress(int slot)
  {
    return slot switch
    {
      0 => 0xA000,
      >= 1 and <= 4 => 0x7E000 - (uint)slot * ClassifierSlotLength,
      _ => throw new ArgumentOutOfRangeException(nameof(slot), slot, "Classifier slots are 0 to 4."),
    };
  }

  public static MyoFlashRegion FactorySettings =>
    new("Factory calibration", FactorySettingsAddress, SectorSize, "factory-calibration.bin");

  public static MyoFlashRegion UserSettings =>
    new("User settings", UserSettingsAddress, SectorSize, "user-settings.bin");

  public static MyoFlashRegion Bootloader =>
    new("Bootloader", BootloaderAddress, BootloaderLength, "bootloader.bin");

  public static MyoFlashRegion Application =>
    new("Application firmware", ApplicationAddress, ApplicationEnd - ApplicationAddress, "application.hex");

  public static MyoFlashRegion ClassifierSlot(int slot)
  {
    return new MyoFlashRegion(
      $"Classifier slot {slot}",
      ClassifierSlotAddress(slot),
      ClassifierSlotLength,
      $"classifier-slot{slot}.bin");
  }
}
