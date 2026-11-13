using Microsoft.Extensions.Configuration;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using System.Diagnostics;
using thumbz.service;

const string Usage = "Usage: thumbz [--clean] <path1> <path2> ...\n" +
                     "  --clean  Before processing a folder, delete sheets whose video no longer exists\n" +
                     "           (sent to the Recycle Bin). Off by default.";

// 1. Validation
bool cleanOrphans = args.Contains("--clean", StringComparer.OrdinalIgnoreCase);
bool legacySkip = args.Contains("--skip", StringComparer.OrdinalIgnoreCase);
var paths = args
    .Where(a => !a.Equals("--clean", StringComparison.OrdinalIgnoreCase)
             && !a.Equals("--skip", StringComparison.OrdinalIgnoreCase))
    .ToArray();

if (!paths.Any())
{
    Console.WriteLine("No paths supplied. " + Usage);
    return;
}

if (legacySkip)
{
    Console.WriteLine("Note: --skip is no longer needed; orphan cleanup is now off unless you pass --clean.\n");
}

// 2. Load Config
var configuration = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
    .Build();

var cnf = configuration
    .GetSection("AppSettings:ThumbnailSheetConfig")
    .Get<ThumbnailSheetConfig>()!;

// 3. Init Service
var thumbz = new Thumbz(cnf);
var sw = new Stopwatch();

// 4. Run
foreach (string path in paths)
{
    if (Directory.Exists(path))
    {
        if (cleanOrphans)
            CleanOrphanedThumbnails(path);

        await ProcessDirectoryWithParallelization(path);
    }
    else if (File.Exists(path))
    {
        // Process single video file
        await ProcessSingleFile(path);
    }
    else
    {
        Console.WriteLine($"Path not found: {path}");
    }
}

string SheetPathFor(string videoPath) =>
    Path.Combine(Path.GetDirectoryName(videoPath)!, Path.GetFileNameWithoutExtension(videoPath) + cnf.SheetFileType);

// Writes the sheet to a temp file and renames it into place, so an interrupted run never leaves
// a half-written sheet that later runs would mistake for a finished one.
// Returns false if the sheet already exists (another worker or process got there first).
async Task<bool> SaveSheetAtomicAsync(Image<Rgba32> sheet, string sheetPath, CancellationToken ct = default)
{
    var format = sheet.Configuration.ImageFormatsManager.TryFindFormatByFileExtension(cnf.SheetFileType, out var f)
        ? f
        : throw new InvalidOperationException($"Unsupported SheetFileType: {cnf.SheetFileType}");
    var encoder = sheet.Configuration.ImageFormatsManager.GetEncoder(format);

    // Not ending in the sheet extension, so a leftover from a crash is never taken for a sheet
    string tempPath = sheetPath + $".{Guid.NewGuid():N}.thumbz-tmp";
    try
    {
        await using (var stream = File.Create(tempPath))
        {
            await sheet.SaveAsync(stream, encoder, ct);
        }

        try
        {
            File.Move(tempPath, sheetPath, overwrite: false);
            return true;
        }
        catch (IOException) when (File.Exists(sheetPath))
        {
            return false;
        }
    }
    finally
    {
        try { File.Delete(tempPath); } catch { }
    }
}

void CleanOrphanedThumbnails(string path)
{
    Console.WriteLine($"Checking for orphaned thumbnails in: {path}...");

    var allFiles = Directory.EnumerateFiles(path, "*.*", SearchOption.AllDirectories).ToList();

    // Build a set of video base names (without extension) for fast lookup
    var videoBaseNames = allFiles
        .Where(f => cnf.VideoExtensions.Contains(Path.GetExtension(f).ToLower()))
        .Select(f => Path.Combine(Path.GetDirectoryName(f)!, Path.GetFileNameWithoutExtension(f)).ToLower())
        .ToHashSet();

    // Find thumbnail sheets that don't have a corresponding video
    var thumbnails = allFiles
        .Where(f => Path.GetExtension(f).Equals(cnf.SheetFileType, StringComparison.OrdinalIgnoreCase));

    int deleted = 0;
    foreach (var thumb in thumbnails)
    {
        string thumbBasePath = Path.Combine(Path.GetDirectoryName(thumb)!, Path.GetFileNameWithoutExtension(thumb)).ToLower();

        if (!videoBaseNames.Contains(thumbBasePath))
        {
            try
            {
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(
                    thumb,
                    Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                    Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                Console.WriteLine($"Deleted orphan: {Path.GetFileName(thumb)}");
                deleted++;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Could not delete {Path.GetFileName(thumb)}: {ex.Message}");
            }
        }
    }

    Console.WriteLine($"Orphaned thumbnails moved to the Recycle Bin: {deleted}\n");
}

async Task ProcessDirectoryWithParallelization(string path)
{
    Console.WriteLine($"Scanning: {path}...");

    // Get all video files
    var allVideoFiles = Directory.EnumerateFiles(path, "*.*", SearchOption.AllDirectories)
        .Where(file => cnf.VideoExtensions.Contains(Path.GetExtension(file).ToLower()))
        .ToList();

    // Filter to only files that need processing (no existing sheet)
    var filesToProcess = allVideoFiles
        .Where(videoPath => !File.Exists(SheetPathFor(videoPath)))
        .ToList();

    int alreadyExist = allVideoFiles.Count - filesToProcess.Count;
    int total = filesToProcess.Count;

    Console.WriteLine($"Found {allVideoFiles.Count} videos: {alreadyExist} already have sheets, {total} to process.\n");

    if (total == 0)
    {
        Console.WriteLine("Nothing to do.\n");
        return;
    }

    await ProcessFilesInParallel(filesToProcess);
}

async Task ProcessSingleFile(string videoPath)
{
    try
    {
        string name = Path.GetFileNameWithoutExtension(videoPath);
        string expectedSheetPath = SheetPathFor(videoPath);

        // Check if already exists
        if (File.Exists(expectedSheetPath))
        {
            Console.WriteLine($"Skipped (already exists): {name}\n");
            return;
        }

        Console.WriteLine($"Processing: {name}");
        sw.Restart();

        using (var sheet = thumbz.CreateThumbnailSheet(videoPath))
        {
            sw.Stop();
            if (sheet != null)
            {
                if (await SaveSheetAtomicAsync(sheet, expectedSheetPath))
                    Console.WriteLine($"  Saved ({sw.Elapsed.TotalSeconds:F2}s)\n");
                else
                    Console.WriteLine("  Skipped (created by another process)\n");
            }
            else
            {
                Console.WriteLine("  Failed (Null Output)\n");
            }
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  Error: {ex.Message}\n");
    }
}

async Task ProcessFilesInParallel(List<string> filesToProcess)
{
    int total = filesToProcess.Count;
    int processed = 0, skipped = 0, errors = 0, done = 0;
    var consoleLock = new object();

    // No point in more workers than there are videos
    int workers = Math.Max(1, Math.Min(cnf.MaxParallelism, total));
    Console.WriteLine($"Processing {total} videos with up to {workers} parallel workers...\n");

    var options = new ParallelOptions { MaxDegreeOfParallelism = workers };
    var overallSw = Stopwatch.StartNew();

    await Parallel.ForEachAsync(filesToProcess, options, async (videoPath, ct) =>
    {
        int n = Interlocked.Increment(ref done);
        string name = Path.GetFileNameWithoutExtension(videoPath);
        try
        {
            string expectedSheetPath = SheetPathFor(videoPath);

            // Another worker (or run) may already have produced it.
            if (File.Exists(expectedSheetPath))
            {
                Interlocked.Increment(ref skipped);
                lock (consoleLock) Console.WriteLine($"[{n}/{total}] {name}\n  Skipped (already exists)\n");
                return;
            }

            var localSw = Stopwatch.StartNew();
            using var sheet = thumbz.CreateThumbnailSheet(videoPath);
            localSw.Stop();

            if (sheet != null)
            {
                if (await SaveSheetAtomicAsync(sheet, expectedSheetPath, ct))
                {
                    Interlocked.Increment(ref processed);
                    lock (consoleLock) Console.WriteLine($"[{n}/{total}] {name}\n  Saved ({localSw.Elapsed.TotalSeconds:F2}s)\n");
                }
                else
                {
                    Interlocked.Increment(ref skipped);
                    lock (consoleLock) Console.WriteLine($"[{n}/{total}] {name}\n  Skipped (created by another worker)\n");
                }
            }
            else
            {
                Interlocked.Increment(ref errors);
                lock (consoleLock) Console.WriteLine($"[{n}/{total}] {name}\n  Failed (null output)\n");
            }
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref errors);
            lock (consoleLock) Console.WriteLine($"[{n}/{total}] {name}\n  Error: {ex.Message}\n");
        }
    });

    overallSw.Stop();
    double totalSecs = overallSw.Elapsed.TotalSeconds;
    string totalStr = totalSecs >= 60 ? $"{overallSw.Elapsed:mm\\:ss} ({totalSecs:F0}s)" : $"{totalSecs:F1}s";

    Console.WriteLine("--- Summary ---");
    Console.WriteLine($"Created: {processed} | Skipped: {skipped} | Errors: {errors}");
    Console.WriteLine($"Total time: {totalStr}");
    if (processed > 0)
        Console.WriteLine($"Avg per sheet: {totalSecs / processed:F2}s  ({processed} sheets / {totalSecs:F1}s)");
    Console.WriteLine("---------------------------------\n");
}
