using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using Microsoft.Win32;
using MyoSharp.Firmware;

namespace MyoSharp.Demo;

/// <summary>Reading the armband's flash out over BLE.</summary>
public partial class MainWindow
{
  /// <summary>Rough rate used only to warn about how long a selection will take.</summary>
  private const double DumpBytesPerSecond = 100.0;

  private static readonly string DefaultDumpFolder = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "MyoSharp", "dumps");

  private MyoFirmwareImage? _dumpReference;
  private CancellationTokenSource? _dumpCancellation;
  private bool _dumping;

  private void InitializeDump()
  {
    DumpFolderBox.Text = DefaultDumpFolder;
  }

  private void OnDumpBrowseClick(object sender, RoutedEventArgs e)
  {
    var dialog = new OpenFolderDialog { Title = "Where to put the dumps", InitialDirectory = DumpFolder() };
    if (dialog.ShowDialog(this) == true)
    {
      DumpFolderBox.Text = dialog.FolderName;
    }
  }

  private void OnDumpOpenFolderClick(object sender, RoutedEventArgs e)
  {
    string folder = DumpFolder();
    Directory.CreateDirectory(folder);
    Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
  }

  private void OnDumpReferenceClick(object sender, RoutedEventArgs e)
  {
    var dialog = new OpenFileDialog
    {
      Filter = "Myo firmware image (*.hex)|*.hex|All files|*.*",
      Title = "Reference image, used only for bytes the armband cannot pin down",
    };
    if (dialog.ShowDialog(this) != true)
    {
      return;
    }
    try
    {
      _dumpReference = MyoFirmwareImage.Load(dialog.FileName);
      DumpReferenceText.Text = $"{Path.GetFileName(dialog.FileName)} ({_dumpReference.Version})";
      Log($"Dump reference image loaded: {_dumpReference}");
    }
    catch (Exception ex)
    {
      _dumpReference = null;
      DumpReferenceText.Text = $"refused: {ex.Message}";
      Log($"Dump reference image refused: {ex.Message}");
    }
  }

  private void OnDumpReferenceClearClick(object sender, RoutedEventArgs e)
  {
    _dumpReference = null;
    DumpReferenceText.Text = "none loaded";
  }

  private void OnDumpCancelClick(object sender, RoutedEventArgs e)
  {
    _dumpCancellation?.Cancel();
    DumpStatusText.Text = "Stopping after the current region...";
  }

  private async void OnDumpStartClick(object sender, RoutedEventArgs e)
  {
    var myo = _myo;
    if (myo == null)
    {
      Log("Connect first.");
      return;
    }
    if (_dumping || _updating)
    {
      Log(_updating ? "A firmware update is running." : "A dump is already running.");
      return;
    }
    var regions = SelectedDumpRegions();
    if (regions.Count == 0)
    {
      DumpStatusText.Text = "Nothing selected.";
      return;
    }
    long total = regions.Sum(region => (long)region.Length);
    var estimate = TimeSpan.FromSeconds(total / DumpBytesPerSecond);
    var answer = MessageBox.Show(
      this,
      $"Read {total / 1024} KiB out of the armband ({string.Join(", ", regions.Select(r => r.Name))})?\n\n" +
      $"At about {DumpBytesPerSecond:F0} bytes per second that is roughly {FormatDuration(estimate)}. " +
      "You can cancel at any point and keep what has been written.\n\n" +
      "This opens a firmware update session, which erases the spare flash bank: scratch space the armband does " +
      "not run from. Nothing is committed and the firmware on the armband is left exactly as it is. Streaming is " +
      "stopped meanwhile.",
      "Read flash over BLE",
      MessageBoxButton.YesNo,
      MessageBoxImage.Question);
    if (answer != MessageBoxResult.Yes)
    {
      return;
    }

    string folder = DumpFolder();
    _dumping = true;
    DumpStartButton.IsEnabled = false;
    DumpCancelButton.IsEnabled = true;
    DumpProgressBar.Value = 0;
    var text = new StringBuilder();
    DumpResultText.Text = string.Empty;
    _dumpCancellation = new CancellationTokenSource();
    var results = new List<MyoFlashDumpResult>();
    var dumper = new MyoFlashDumper(myo);
    long doneBytes = 0;
    var stopwatch = Stopwatch.StartNew();
    try
    {
      var progress = new Progress<MyoMemoryReadProgress>(update =>
      {
        DumpProgressBar.Value = total == 0 ? 0 : Math.Min(1.0, (doneBytes + update.BytesDone) / (double)total);
        DumpStatusText.Text = $"{update.Message} ({results.Count + 1} of {regions.Count}: " +
          $"{regions[Math.Min(results.Count, regions.Count - 1)].Name}, {update.Requests} requests)";
      });
      DumpStatusText.Text = "Opening a read session...";
      await dumper.OpenAsync(progress, _dumpCancellation.Token);
      if (!dumper.Reader.CanReadExactly)
      {
        Log("Dump: the spare bank is not a fresh mirror, so bytes that are either 0x00 or 0xFF cannot be settled exactly.");
      }
      foreach (var region in regions)
      {
        var result = await dumper.DumpAsync(region, folder, _dumpReference, progress, _dumpCancellation.Token);
        results.Add(result);
        doneBytes += region.Length;
        text.AppendLine(result.ToString());
        text.AppendLine($"  {Path.GetFileName(result.Path)}");
        DumpResultText.Text = text.ToString().TrimEnd();
        Log($"Dumped {result}");
      }
      DumpStatusText.Text = $"Finished in {FormatDuration(stopwatch.Elapsed)}.";
    }
    catch (OperationCanceledException)
    {
      DumpStatusText.Text = $"Cancelled after {FormatDuration(stopwatch.Elapsed)}; {results.Count} of {regions.Count} regions written.";
      Log(DumpStatusText.Text);
    }
    catch (Exception ex)
    {
      DumpStatusText.Text = $"Reading failed: {ex.Message}";
      Log(DumpStatusText.Text);
    }
    finally
    {
      try
      {
        if (results.Count > 0)
        {
          string report = dumper.WriteReport(folder, results);
          text.AppendLine($"Report: {Path.GetFileName(report)}");
          DumpResultText.Text = text.ToString().TrimEnd();
        }
      }
      catch (Exception ex)
      {
        Log($"Writing the dump report failed: {ex.Message}");
      }
      await dumper.DisposeAsync();
      _dumpCancellation?.Dispose();
      _dumpCancellation = null;
      _dumping = false;
      DumpStartButton.IsEnabled = true;
      DumpCancelButton.IsEnabled = false;
      UpdateStatus();
    }
  }

  private List<MyoFlashRegion> SelectedDumpRegions()
  {
    var regions = new List<MyoFlashRegion>();
    if (DumpFactoryCheck.IsChecked == true)
    {
      regions.Add(MyoFlashLayout.FactorySettings);
    }
    if (DumpSettingsCheck.IsChecked == true)
    {
      regions.Add(MyoFlashLayout.UserSettings);
    }
    var slots = new[] { DumpSlot0Check, DumpSlot1Check, DumpSlot2Check, DumpSlot3Check, DumpSlot4Check };
    for (int slot = 0; slot < slots.Length; slot++)
    {
      if (slots[slot].IsChecked == true)
      {
        regions.Add(MyoFlashLayout.ClassifierSlot(slot));
      }
    }
    if (DumpBootloaderCheck.IsChecked == true)
    {
      regions.Add(MyoFlashLayout.Bootloader);
    }
    if (DumpApplicationCheck.IsChecked == true)
    {
      regions.Add(MyoFlashLayout.Application);
    }
    return regions;
  }

  private string DumpFolder()
  {
    string folder = DumpFolderBox.Text.Trim();
    return folder.Length == 0 ? DefaultDumpFolder : folder;
  }

  private static string FormatDuration(TimeSpan span)
  {
    if (span.TotalMinutes < 1)
    {
      return $"{span.TotalSeconds:F0} s";
    }
    return span.TotalHours < 1
      ? $"{span.TotalMinutes:F0} min"
      : $"{(int)span.TotalHours} h {span.Minutes} min";
  }
}
