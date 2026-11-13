using Microsoft.Extensions.Configuration;
using SixLabors.ImageSharp;
using System.Diagnostics;
using thumbz.service;

// 1. Validation
if (!args.Any())
{
    Console.WriteLine("No paths supplied. Usage: thumbz [--skip] <path1> <path2>");
    return;
}

// Check for --skip flag
bool skipOrphanCleanup = args.Contains("--skip", StringComparer.OrdinalIgnoreCase);
var paths = args.Where(a => !a.Equals("--skip", StringComparison.OrdinalIgnoreCase)).ToArray();

if (!paths.Any())
{
    Console.WriteLine("No paths supplied. Usage: thumbz [--skip] <path1> <path2>");
    return;
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
        if (!skipOrphanCleanup)
            CleanOrphanedThumbnails(path);
        else
            Console.WriteLine("Skipping orphaned thumbnail cleanup (--skip).\n");

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
            File.Delete(thumb);
            Console.WriteLine($"Deleted orphan: {Path.GetFileName(thumb)}");
            deleted++;
        }
    }

    Console.WriteLine($"Orphaned thumbnails removed: {deleted}\n");
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
        .Where(videoPath =>
        {
            string? dir = Path.GetDirectoryName(videoPath);
            string name = Path.GetFileNameWithoutExtension(videoPath);
            string expectedSheetPath = Path.Combine(dir!, $"{name}{cnf.SheetFileType}");
            return !File.Exists(expectedSheetPath);
        })
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
        string? dir = Path.GetDirectoryName(videoPath);
        string name = Path.GetFileNameWithoutExtension(videoPath);
        string expectedSheetPath = Path.Combine(dir!, $"{name}{cnf.SheetFileType}");

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
                // Race condition guard
                if (File.Exists(expectedSheetPath))
                {
                    Console.WriteLine("  Skipped (created by another process)\n");
                    return;
                }
                await sheet.SaveAsync(expectedSheetPath);
                Console.WriteLine($"  Saved ({sw.Elapsed.TotalSeconds:F2}s)\n");
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

    int workers = Math.Max(1, cnf.MaxParallelism);
    Console.WriteLine($"Processing {total} videos with up to {workers} parallel workers...\n");

    var options = new ParallelOptions { MaxDegreeOfParallelism = workers };

    await Parallel.ForEachAsync(filesToProcess, options, async (videoPath, ct) =>
    {
        int n = Interlocked.Increment(ref done);
        string name = Path.GetFileNameWithoutExtension(videoPath);
        try
        {
            string? dir = Path.GetDirectoryName(videoPath);
            string expectedSheetPath = Path.Combine(dir!, $"{name}{cnf.SheetFileType}");

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
                // Race guard: another worker may have finished the same file first.
                if (File.Exists(expectedSheetPath))
                {
                    Interlocked.Increment(ref skipped);
                    lock (consoleLock) Console.WriteLine($"[{n}/{total}] {name}\n  Skipped (created by another worker)\n");
                    return;
                }
                await sheet.SaveAsync(expectedSheetPath, ct);
                Interlocked.Increment(ref processed);
                lock (consoleLock) Console.WriteLine($"[{n}/{total}] {name}\n  Saved ({localSw.Elapsed.TotalSeconds:F2}s)\n");
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

    Console.WriteLine("--- Summary ---");
    Console.WriteLine($"Created: {processed} | Skipped: {skipped} | Errors: {errors}");
    Console.WriteLine("---------------------------------\n");
}