using Microsoft.Extensions.Configuration;
using SixLabors.ImageSharp;
using System.Diagnostics;
using thumbz.service;

// 1. Validation
if (!args.Any())
{
    Console.WriteLine("No paths supplied. Usage: thumbz <path1> <path2>");
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
foreach (string path in args)
{
    if (Directory.Exists(path))
    {
        CleanOrphanedThumbnails(path);
        await ProcessDirectory(path);
    }
    else
    {
        Console.WriteLine($"Directory not found: {path}");
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


async Task ProcessDirectory(string path)
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
        .OrderBy(_ => Random.Shared.Next()) // Randomize
        .ToList();

    int alreadyExist = allVideoFiles.Count - filesToProcess.Count;
    int processed = 0, skipped = 0, errors = 0;
    int total = filesToProcess.Count;

    Console.WriteLine($"Found {allVideoFiles.Count} videos: {alreadyExist} already have sheets, {total} to process.\n");

    if (total == 0)
    {
        Console.WriteLine("Nothing to do.\n");
        return;
    }

    int current = 0;
    foreach (var videoPath in filesToProcess)
    {
        current++;
        try
        {
            string? dir = Path.GetDirectoryName(videoPath);
            string name = Path.GetFileNameWithoutExtension(videoPath);
            string expectedSheetPath = Path.Combine(dir!, $"{name}{cnf.SheetFileType}");

            // Double-check in case another process created it
            if (File.Exists(expectedSheetPath))
            {
                Console.WriteLine($"[{current}/{total} - {current * 100 / total}%] {name}");
                Console.WriteLine("  Skipped (already exists)\n");
                skipped++;
                continue;
            }

            Console.WriteLine($"[{current}/{total} - {current * 100 / total}%] {name}");
            sw.Restart();

            using (var sheet = thumbz.CreateThumbnailSheet(videoPath))
            {
                sw.Stop();
                if (sheet != null)
                {
                    // CHECK 2: Before saving (race condition guard)
                    if (File.Exists(expectedSheetPath))
                    {
                        Console.WriteLine("  Skipped (created by another process)\n");
                        skipped++;
                        continue;
                    }
                    await sheet.SaveAsync(expectedSheetPath);
                    Console.WriteLine($"  Saved ({sw.Elapsed.TotalSeconds:F2}s)\n");
                    processed++;
                }
                else
                {
                    Console.WriteLine("  Failed (Null Output)\n");
                    errors++;
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  Error: {ex.Message}\n");
            errors++;
        }
    }

    Console.WriteLine($"--- Summary for {path} ---");
    Console.WriteLine($"Created: {processed} | Skipped: {skipped + alreadyExist} | Errors: {errors}");
    Console.WriteLine("---------------------------------\n");
}