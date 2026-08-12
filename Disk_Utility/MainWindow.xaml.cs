using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Win32;
using DiskUtility.Models;
using DiskUtility.Services;

namespace DiskUtility;

public partial class MainWindow : Window
{
    private CancellationTokenSource? _cts;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += async (_, _) => await LoadDrivesAsync();
    }

    // ---- Drive enumeration ----

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
        => await LoadDrivesAsync();

    private async Task LoadDrivesAsync()
    {
        RefreshButton.IsEnabled = false;
        StatusText.Text = "Scanning drives\u2026";
        try
        {
            var disks = await Task.Run(() => DriveEnumerator.Enumerate());
            DiskGrid.ItemsSource = disks;
            StatusText.Text = $"Found {disks.Count} physical drive(s). "
                            + "Run as Administrator for complete details.";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Error: {ex.Message}";
        }
        finally
        {
            RefreshButton.IsEnabled = true;
        }
    }

    // ---- Imaging ----

    private async void CreateImageButton_Click(object sender, RoutedEventArgs e)
    {
        if (DiskGrid.SelectedItem is not PhysicalDisk disk)
        {
            StatusText.Text = "Select a drive to image first.";
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "Save disk image as",
            Filter = "Raw disk image (*.img)|*.img|All files (*.*)|*.*",
            FileName = $"drive{disk.Index}.img",
        };
        if (dialog.ShowDialog() != true) return;

        string outputPath = dialog.FileName;
        var (progress, token) = BeginOperation();
        LogLine($"Imaging {disk.DeviceId}  ({disk.Model}, {disk.SizeDisplay})");
        LogLine($"  -> {outputPath}");

        ImagingResult result = await Task.Run(() =>
            ImagingEngine.CreateImage(disk.DeviceId, outputPath, HashSelection.All, progress, token));

        EndOperation();

        if (result.Cancelled)
            LogLine($"Cancelled after {PhysicalDisk.FormatBytes(result.BytesWritten)} "
                  + $"({result.Elapsed:hh\\:mm\\:ss}). Partial image left on disk.");
        else if (result.Error is not null)
            LogLine($"ERROR: {result.Error}");
        else
        {
            LogLine($"Done. Wrote {PhysicalDisk.FormatBytes(result.BytesWritten)} in {result.Elapsed:hh\\:mm\\:ss}.");
            if (result.BadRegionCount > 0)
                LogLine($"  WARNING: {result.BadRegionCount} unreadable region(s) "
                      + $"({PhysicalDisk.FormatBytes(result.BadBytes)}) were zero-filled.");
            if (result.Md5 is not null) LogLine($"  MD5    {result.Md5}");
            if (result.Sha1 is not null) LogLine($"  SHA1   {result.Sha1}");
            if (result.Sha256 is not null) LogLine($"  SHA256 {result.Sha256}");
        }
    }

    // ---- Verify: source device vs image file ----

    private async void VerifyImageButton_Click(object sender, RoutedEventArgs e)
    {
        if (DiskGrid.SelectedItem is not PhysicalDisk disk)
        {
            StatusText.Text = "Select the source drive first.";
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = "Select the image to verify against the selected drive",
            Filter = "Raw disk image (*.img)|*.img|All files (*.*)|*.*",
        };
        if (dialog.ShowDialog() != true) return;

        string imagePath = dialog.FileName;
        var (progress, token) = BeginOperation();

        LogLine($"Verifying {disk.DeviceId} against {imagePath}");
        LogLine("  Hashing source drive\u2026");
        HashResult source = await Task.Run(() =>
            VerifyEngine.HashDevice(disk.DeviceId, HashSelection.All, progress, token));

        if (!Finished(source, "source")) { EndOperation(); return; }

        LogLine("  Hashing image file\u2026");
        HashResult image = await Task.Run(() =>
            VerifyEngine.HashFile(imagePath, HashSelection.All, progress, token));

        EndOperation();
        if (!Finished(image, "image")) return;

        LogLine($"  Source SHA256 {source.Sha256}");
        LogLine($"  Image  SHA256 {image.Sha256}");
        if (source.BadRegionCount > 0)
            LogLine($"  Note: {source.BadRegionCount} unreadable region(s) on the source were zero-filled.");
        LogLine(VerifyEngine.Match(source, image)
            ? "  RESULT: MATCH \u2713  source and image are identical."
            : "  RESULT: MISMATCH \u2717  image does NOT match source.");
    }

    // ---- Hash a single file ----

    private async void HashFileButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Select a file to hash", Filter = "All files (*.*)|*.*" };
        if (dialog.ShowDialog() != true) return;

        var (progress, token) = BeginOperation();
        LogLine($"Hashing {dialog.FileName}");

        HashResult r = await Task.Run(() =>
            VerifyEngine.HashFile(dialog.FileName, HashSelection.All, progress, token));

        EndOperation();
        if (r.Cancelled) { LogLine("Cancelled."); return; }
        if (r.Error is not null) { LogLine($"ERROR: {r.Error}"); return; }

        LogLine($"Done. {PhysicalDisk.FormatBytes(r.BytesHashed)} in {r.Elapsed:hh\\:mm\\:ss}.");
        if (r.Md5 is not null) LogLine($"  MD5    {r.Md5}");
        if (r.Sha1 is not null) LogLine($"  SHA1   {r.Sha1}");
        if (r.Sha256 is not null) LogLine($"  SHA256 {r.Sha256}");
    }

    // ---- Restore: write an image back to a disk ----

    private async void RestoreButton_Click(object sender, RoutedEventArgs e)
    {
        if (DiskGrid.SelectedItem is not PhysicalDisk disk)
        {
            StatusText.Text = "Select the target drive first.";
            return;
        }
        if (disk.IsSystemDisk)
        {
            StatusText.Text = "The system disk cannot be a write target.";
            return;
        }

        var open = new OpenFileDialog
        {
            Title = "Select the image to write to the drive",
            Filter = "Raw disk image (*.img)|*.img|All files (*.*)|*.*",
        };
        if (open.ShowDialog() != true) return;
        string imagePath = open.FileName;

        var confirm = new ConfirmDialog(
            "Restore image — DESTRUCTIVE",
            $"This will OVERWRITE all data on:\n\n"
            + $"    {disk.DeviceId}  —  {disk.Model}  ({disk.SizeDisplay})\n\n"
            + $"with the contents of:\n\n    {imagePath}\n\n"
            + "Every volume on the target will be dismounted. This cannot be undone.",
            confirmPhrase: $"RESTORE {disk.Index}",
            verifyLabel: "Verify after writing")
        {
            Owner = this,
        };
        if (confirm.ShowDialog() != true) return;

        bool verify = confirm.VerifyChecked;
        var (progress, token) = BeginOperation();
        LogLine($"Restoring {imagePath} -> {disk.DeviceId} ({disk.Model})");

        RestoreResult result = await Task.Run(() =>
            RestoreEngine.Restore(disk, imagePath, verify, progress, token));

        EndOperation();

        if (result.Cancelled)
            LogLine($"Cancelled after {PhysicalDisk.FormatBytes(result.BytesWritten)} ({result.Elapsed:hh\\:mm\\:ss}). "
                  + "Target is now in a partial state.");
        else if (result.Error is not null)
            LogLine($"ERROR: {result.Error}");
        else
        {
            LogLine($"Done. Wrote {PhysicalDisk.FormatBytes(result.BytesWritten)} in {result.Elapsed:hh\\:mm\\:ss}.");
            if (result.Truncated)
                LogLine("  WARNING: image was larger than the target disk; it was truncated to fit.");
            if (result.VerifyRequested)
                LogLine(result.VerifyMatched
                    ? "  VERIFY: MATCH \u2713  the disk now matches the image."
                    : "  VERIFY: MISMATCH \u2717" + (result.Truncated ? " (expected — image was truncated)." : "."));
        }
    }

    // ---- Wipe: overwrite a disk ----

    private async void WipeButton_Click(object sender, RoutedEventArgs e)
    {
        if (DiskGrid.SelectedItem is not PhysicalDisk disk)
        {
            StatusText.Text = "Select the drive to wipe first.";
            return;
        }
        if (disk.IsSystemDisk)
        {
            StatusText.Text = "The system disk cannot be wiped.";
            return;
        }

        string ssdNote = disk.Media == MediaKind.SSD
            ? "\n\nNOTE: This is an SSD. Overwrite passes do NOT reliably erase solid-state media "
              + "because of wear-levelling. For guaranteed erasure use ATA Secure Erase / NVMe Sanitize "
              + "(not yet implemented here). Proceed only if you understand this limitation."
            : string.Empty;

        var confirm = new ConfirmDialog(
            "Wipe drive — DESTRUCTIVE",
            $"This will PERMANENTLY ERASE all data on:\n\n"
            + $"    {disk.DeviceId}  —  {disk.Model}  ({disk.SizeDisplay})\n\n"
            + "Every volume on the drive will be dismounted. This cannot be undone." + ssdNote,
            confirmPhrase: $"WIPE {disk.Index}",
            options: new[] { "Zeros (1 pass)", "Random (1 pass)", "DoD 5220.22-M (3 passes)" },
            verifyLabel: "Verify (zeros method only)",
            // On an SSD the warning above is not enough on its own — make the user state that
            // they've read it, and pass that answer to the engine instead of assuming it.
            acknowledgeText: disk.Media == MediaKind.SSD
                ? "I understand this is an SSD, and that overwrite passes do not guarantee erasure."
                : null,
            verifyOnlyForOptionIndex: 0)   // index of "Zeros (1 pass)"
        {
            Owner = this,
        };
        if (confirm.ShowDialog() != true) return;

        WipeMethod method = confirm.SelectedOptionIndex switch
        {
            1 => WipeMethod.Random,
            2 => WipeMethod.DoD5220,
            _ => WipeMethod.Zeros,
        };
        bool verify = confirm.VerifyChecked;

        var (progress, token) = BeginOperation();
        LogLine($"Wiping {disk.DeviceId} ({disk.Model}, {disk.SizeDisplay}) — method: {method}");

        bool ssdAcknowledged = confirm.Acknowledged;

        WipeResult result = await Task.Run(() =>
            WipeEngine.Wipe(disk, method, allowSsdOverwrite: ssdAcknowledged, verifyZeros: verify, progress, token));

        EndOperation();

        if (result.Cancelled)
            LogLine($"Cancelled after {result.Elapsed:hh\\:mm\\:ss}. Drive is partially wiped.");
        else if (result.Error is not null)
            LogLine($"ERROR: {result.Error}");
        else
        {
            LogLine($"Done. {result.PassesRun} pass(es) over "
                  + $"{PhysicalDisk.FormatBytes(result.BytesPerPass)} in {result.Elapsed:hh\\:mm\\:ss}.");
            if (result.VerifiedZero)
                LogLine("  VERIFY: PASS \u2713  the entire drive reads back as zero.");
            else if (result.FirstNonZeroOffset is long off)
                LogLine($"  VERIFY: FAIL \u2717  first non-zero byte at offset {off:N0}.");
            else if (result.UnverifiedRegionCount > 0)
                LogLine($"  VERIFY: INCONCLUSIVE  no non-zero data was found, but "
                      + $"{result.UnverifiedRegionCount} region(s) "
                      + $"({PhysicalDisk.FormatBytes(result.UnverifiedBytes)}) could not be read "
                      + "back and cannot be confirmed erased.");
            else if (result.VerifyRequested)
                LogLine($"  VERIFY: SKIPPED  read-back verification only applies to the Zeros "
                      + $"method; this run used {method}.");
        }
    }

    // ---- Shared operation plumbing ----

    private (IProgress<DiskProgress> progress, CancellationToken token) BeginOperation()
    {
        _cts = new CancellationTokenSource();
        SetBusyUi(true);

        var progress = new Progress<DiskProgress>(p =>
        {
            ProgressBarCtl.Value = p.PercentComplete;
            ProgressText.Text =
                $"{p.PercentComplete:0.0}%  \u2022  {PhysicalDisk.FormatBytes((long)p.BytesPerSecond)}/s"
                + $"  \u2022  ETA {p.Eta:hh\\:mm\\:ss}";
        });

        return (progress, _cts.Token);
    }

    private void EndOperation() => SetBusyUi(false);

    private bool Finished(HashResult r, string label)
    {
        if (r.Cancelled) { LogLine($"Cancelled while hashing {label}."); return false; }
        if (r.Error is not null) { LogLine($"ERROR hashing {label}: {r.Error}"); return false; }
        return true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        StatusText.Text = "Cancelling\u2026";
    }

    private void SetBusyUi(bool running)
    {
        CreateImageButton.IsEnabled = !running;
        VerifyImageButton.IsEnabled = !running;
        HashFileButton.IsEnabled = !running;
        RestoreButton.IsEnabled = !running;
        WipeButton.IsEnabled = !running;
        RefreshButton.IsEnabled = !running;
        DiskGrid.IsEnabled = !running;
        CancelButton.IsEnabled = running;
        StatusText.Text = running ? "Working\u2026" : "Ready.";
        if (!running)
        {
            ProgressBarCtl.Value = 0;
            ProgressText.Text = string.Empty;
        }
    }

    private void LogLine(string text)
    {
        LogBox.AppendText($"{DateTime.Now:HH:mm:ss}  {text}{Environment.NewLine}");
        LogBox.ScrollToEnd();
    }
}
