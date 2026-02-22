using System.Runtime.InteropServices;
using camera_archiver;
using camera_archiver.Native;

Console.OutputEncoding = System.Text.Encoding.UTF8;
Console.WriteLine("Camera Archiver - MTP Device Browser");
Console.WriteLine("=====================================\n");

// ── Parse command-line arguments ──────────────────────────────────────────
//
//   Copy mode:  camera-archiver --from <device-path> --since <date> --to <output-dir>
//                               [--device <index>]
//
//   Browser:    camera-archiver                (no arguments)

string? argFrom = null, argTo = null, argSince = null;
int argDevice = 0;

for (int i = 0; i < args.Length; i++)
{
    switch (args[i].ToLowerInvariant())
    {
        case "--from":   if (i + 1 < args.Length) argFrom   = args[++i]; break;
        case "--to":     if (i + 1 < args.Length) argTo     = args[++i]; break;
        case "--since":  if (i + 1 < args.Length) argSince  = args[++i]; break;
        case "--device": if (i + 1 < args.Length) int.TryParse(args[++i], out argDevice); break;
        case "--help": case "-h":
            Console.WriteLine("Usage:");
            Console.WriteLine("  Browser mode  : camera-archiver");
            Console.WriteLine("  Copy mode     : camera-archiver --to <dir> [--from <path>] [--since <date>] [--device <n>]");
            Console.WriteLine();
            Console.WriteLine("Options:");
            Console.WriteLine("  --from   <path>  Source folder on device  (default: /)");
            Console.WriteLine("  --to     <dir>   Local output directory   (required for copy mode)");
            Console.WriteLine("  --since  <date>  Only copy files newer than this date (e.g. 2024-11-01)");
            Console.WriteLine("  --device <n>     Device index when multiple are connected (default: 0)");
            return;
    }
}

bool copyMode = argTo is not null;

// ── Init + detect devices ─────────────────────────────────────────────────

MtpClient.Initialize();

Console.Write("Scanning for MTP devices... ");
var (rawDevices, arrayPtr) = MtpClient.DetectDevices();

if (rawDevices.Count == 0)
{
    Console.WriteLine("none found.");
    Console.WriteLine("Make sure your device is connected and set to MTP/PTP mode.");
    return;
}

Console.WriteLine($"{rawDevices.Count} found.\n");

// ── Select device ─────────────────────────────────────────────────────────

RawDevice selectedRaw;
if (copyMode || rawDevices.Count == 1)
{
    selectedRaw = rawDevices[Math.Clamp(argDevice, 0, rawDevices.Count - 1)];
    if (rawDevices.Count > 1)
        Console.WriteLine($"Using device [{argDevice}]: {selectedRaw.DisplayName}\n");
}
else
{
    for (int i = 0; i < rawDevices.Count; i++)
    {
        var d = rawDevices[i];
        Console.WriteLine($"  [{i}] {d.DisplayName}  ({d.Vendor ?? "?"} · Bus {d.BusLocation} Dev {d.DeviceNumber})");
    }
    Console.Write("\nSelect device [0]: ");
    var raw = Console.ReadLine()?.Trim();
    int idx = string.IsNullOrEmpty(raw) ? 0
        : int.TryParse(raw, out var v) ? Math.Clamp(v, 0, rawDevices.Count - 1) : 0;
    selectedRaw = rawDevices[idx];
}

Console.Write($"Opening {selectedRaw.DisplayName}... ");

OpenDevice device;
try
{
    device = MtpClient.OpenDevice(selectedRaw);
}
catch (Exception ex)
{
    Console.WriteLine($"Failed: {ex.Message}");
    if (arrayPtr != IntPtr.Zero) Marshal.FreeHGlobal(arrayPtr);
    return;
}

if (arrayPtr != IntPtr.Zero) Marshal.FreeHGlobal(arrayPtr);
Console.WriteLine("Connected.\n");

using (device)
{
    if (copyMode)
    {
        await RunCopyMode(device, argFrom ?? "/", argTo!, argSince);
    }
    else
    {
        RunBrowserMode(device);
    }
}

Console.WriteLine("\nDisconnected.");

// ── Copy mode ─────────────────────────────────────────────────────────────

async Task RunCopyMode(OpenDevice dev, string sourcePath, string outputDir, string? sinceArg)
{
    // Parse --since date
    DateTime? since = null;
    if (sinceArg is not null)
    {
        if (!DateTime.TryParse(sinceArg, out var d))
        {
            Console.WriteLine($"Invalid date '{sinceArg}'. Use format: yyyy-MM-dd");
            return;
        }
        since = d.Date; // start of day
    }

    // Get storage
    var storages = dev.GetStorages();
    if (storages.Count == 0) { Console.WriteLine("No storage found on device."); return; }
    var storage = storages[0];

    // Resolve source folder
    Console.Write($"Resolving {sourcePath}... ");
    var folderId = dev.ResolvePath(storage.Id, sourcePath);
    if (folderId is null)
    {
        Console.WriteLine("not found.");
        return;
    }
    Console.WriteLine("OK");

    // Collect matching files
    Console.Write("Collecting files... ");
    var files = dev.GetFilesRecursive(storage.Id, folderId.Value, since);
    if (files.Count == 0)
    {
        var msg = since is not null ? $" newer than {since:yyyy-MM-dd}" : "";
        Console.WriteLine($"no files{msg} found in {sourcePath}.");
        return;
    }

    var sinceLabel = since is not null ? $" newer than {since:yyyy-MM-dd}" : "";
    Console.WriteLine($"{files.Count} file(s){sinceLabel}.");

    // Ensure output directory exists
    Directory.CreateDirectory(outputDir);
    Console.WriteLine($"Output : {Path.GetFullPath(outputDir)}\n");

    int copied = 0, skipped = 0, failed = 0;
    ulong totalBytes = (ulong)files.Sum(f => (long)f.Size);
    ulong doneBytes  = 0;

    for (int i = 0; i < files.Count; i++)
    {
        var file     = files[i];
        var destPath = Path.Combine(outputDir, file.Name);

        if (File.Exists(destPath))
        {
            PrintFileLine(i + 1, files.Count, file.Name, "skipped (exists)");
            skipped++;
            doneBytes += file.Size;
            continue;
        }

        // Draw initial bar at 0%
        DrawProgress(i + 1, files.Count, file.Name, 0, file.Size);

        int result = dev.DownloadFile(file.ItemId, destPath,
            (sent, total) => DrawProgress(i + 1, files.Count, file.Name, sent, total));

        doneBytes += file.Size;

        if (result == 0)
        {
            PrintFileLine(i + 1, files.Count, file.Name,
                $"{FormatSize(file.Size),9}  {file.ModifiedAt?.ToString("yyyy-MM-dd HH:mm") ?? ""}");
            copied++;
        }
        else
        {
            // Remove partial file on failure
            try { if (File.Exists(destPath)) File.Delete(destPath); } catch { }
            PrintFileLine(i + 1, files.Count, file.Name, "FAILED");
            failed++;
        }

        // Give the UI a moment to flush on fast transfers
        await Task.Yield();
    }

    Console.WriteLine($"\nDone: {copied} copied, {skipped} skipped, {failed} failed." +
                      $"  ({FormatSize(totalBytes)} total)");
}

// ── Browser mode ──────────────────────────────────────────────────────────

void RunBrowserMode(OpenDevice dev)
{
    Console.WriteLine($"  Friendly Name : {dev.FriendlyName ?? "(not set)"}");
    Console.WriteLine($"  Manufacturer  : {dev.ManufacturerName ?? "(unknown)"}");
    Console.WriteLine($"  Model         : {dev.ModelName ?? "(unknown)"}");
    Console.WriteLine($"  Serial        : {dev.SerialNumber ?? "(unknown)"}");
    Console.WriteLine();

    var storages = dev.GetStorages();
    if (storages.Count == 0)
    {
        Console.WriteLine("No accessible storage found on this device.");
        return;
    }

    Console.WriteLine($"Storage ({storages.Count} unit(s)):");
    for (int i = 0; i < storages.Count; i++)
    {
        var s    = storages[i];
        ulong used = s.MaxCapacity > 0 ? s.MaxCapacity - s.FreeSpaceInBytes : 0;
        int   pct  = s.MaxCapacity > 0 ? (int)(used * 100 / s.MaxCapacity)  : 0;
        Console.WriteLine(
            $"  [{i}] {s.Description ?? "Storage",-32}  " +
            $"{FormatSize(s.FreeSpaceInBytes),9} free / {FormatSize(s.MaxCapacity),9} total  ({pct}% used)");
    }

    StorageInfo storage = storages[0];
    if (storages.Count > 1)
    {
        Console.Write("\nSelect storage [0]: ");
        var raw = Console.ReadLine()?.Trim();
        int idx = string.IsNullOrEmpty(raw) ? 0
            : int.TryParse(raw, out var v) ? Math.Clamp(v, 0, storages.Count - 1) : 0;
        storage = storages[idx];
    }

    Console.WriteLine($"\nBrowsing: {storage.Description ?? "Storage"}\n");
    Console.WriteLine("Commands: <folder>  enter folder  |  ..  go up  |  ls  list  |  q  quit\n");

    var stack = new Stack<(uint StorageId, uint FolderId, string Name)>();
    stack.Push((storage.Id, LibMtp.Root, ""));

    var currentEntries = RefreshAndPrint(dev, stack.Peek());

    while (true)
    {
        Console.Write($"\n/{BuildPath(stack)}> ");
        var cmd = Console.ReadLine()?.Trim();

        if (string.IsNullOrEmpty(cmd) || cmd is "q" or "quit" or "exit")
            break;

        if (cmd is "..")
        {
            if (stack.Count <= 1) { Console.WriteLine("Already at root."); continue; }
            stack.Pop();
            currentEntries = RefreshAndPrint(dev, stack.Peek());
            continue;
        }

        if (cmd is "ls" or "dir") { PrintEntries(currentEntries); continue; }

        if (cmd is "help" or "?")
        {
            Console.WriteLine("  <folder>  navigate into folder");
            Console.WriteLine("  ..        go up one level");
            Console.WriteLine("  ls        list current folder");
            Console.WriteLine("  q         quit");
            continue;
        }

        var match = currentEntries.FirstOrDefault(e =>
            e.IsFolder && string.Equals(e.Name, cmd, StringComparison.OrdinalIgnoreCase));

        if (match is null)
        {
            var asFile = currentEntries.FirstOrDefault(e =>
                string.Equals(e.Name, cmd, StringComparison.OrdinalIgnoreCase));
            Console.WriteLine(asFile is not null
                ? $"  '{cmd}' is a file, not a folder."
                : $"  '{cmd}': not found. Type 'ls' to list or '..' to go up.");
            continue;
        }

        var (curStorId, _, _) = stack.Peek();
        stack.Push((curStorId, match.ItemId, match.Name));
        currentEntries = RefreshAndPrint(dev, stack.Peek());
    }
}

// ── Shared helpers ────────────────────────────────────────────────────────

static List<FileEntry> RefreshAndPrint(OpenDevice dev, (uint StorageId, uint FolderId, string Name) folder)
{
    var entries = dev.GetFilesAndFolders(folder.StorageId, folder.FolderId);
    PrintEntries(entries);
    return entries;
}

static void PrintEntries(List<FileEntry> entries)
{
    if (entries.Count == 0) { Console.WriteLine("  (empty)"); return; }

    var dirs  = entries.Where(e =>  e.IsFolder).OrderBy(e => e.Name).ToList();
    var files = entries.Where(e => !e.IsFolder).OrderBy(e => e.Name).ToList();

    foreach (var d in dirs)
        Console.WriteLine($"  {d.Name + "/",-46}{"<DIR>",9}  {d.ModifiedAt?.ToString("yyyy-MM-dd HH:mm") ?? ""}");

    foreach (var f in files)
        Console.WriteLine($"  {f.Name,-46}{FormatSize(f.Size),9}  {f.ModifiedAt?.ToString("yyyy-MM-dd HH:mm") ?? ""}");

    Console.WriteLine($"\n  {dirs.Count} dir(s), {files.Count} file(s)");
}

static string BuildPath(Stack<(uint, uint, string Name)> stack) =>
    string.Join("/", stack.Reverse().Select(x => x.Name).Where(n => n.Length > 0));

// Progress bar: "  [ 3/23] filename.ext          [████████░░░░░░░░░░░░]  38%  9.1 MB / 24.0 MB"
static void DrawProgress(int idx, int total, string name, ulong sent, ulong totalBytes)
{
    const int barWidth = 20;
    int filled = totalBytes > 0 ? (int)Math.Min((long)sent * barWidth / (long)totalBytes, barWidth) : 0;
    int pct    = totalBytes > 0 ? (int)Math.Min((long)sent * 100    / (long)totalBytes, 100)      : 0;
    var bar    = new string('█', filled) + new string('░', barWidth - filled);
    Console.Write(
        $"\r  [{idx,3}/{total}] {Truncate(name, 26),-26} [{bar}] {pct,3}%  {FormatSize(sent)} / {FormatSize(totalBytes)}   ");
}

// Print a completed / skipped / failed line for a file (clears the progress bar).
static void PrintFileLine(int idx, int total, string name, string status)
{
    Console.Write($"\r  [{idx,3}/{total}] {Truncate(name, 26),-26}  {status,-46}");
    Console.WriteLine();
}

static string Truncate(string s, int max) =>
    s.Length <= max ? s : s[..(max - 1)] + "…";

static string FormatSize(ulong bytes) => bytes switch
{
    < 1_024               => $"{bytes} B",
    < 1_048_576           => $"{bytes / 1_024.0:0.0} KB",
    < 1_073_741_824       => $"{bytes / 1_048_576.0:0.0} MB",
    < 1_099_511_627_776UL => $"{bytes / 1_073_741_824.0:0.00} GB",
    _                     => $"{bytes / 1_099_511_627_776.0:0.00} TB",
};
