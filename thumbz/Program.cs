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
        .OrderBy(_ => Random.Shared.Next()) // Randomize
        .ToList();

    int alreadyExist = allVideoFiles.Count - filesToProcess.Count;
    int total = filesToProcess.Count;

    Console.WriteLine($"Found {allVideoFiles.Count} videos: {alreadyExist} already have sheets, {total} to process.\n");

    if (total == 0)
    {
        Console.WriteLine("Nothing to do.\n");
        return;
    }

    // Decide whether to parallelize
    if (total < 10)
    {
        Console.WriteLine("Processing in single thread...\n");
        await ProcessFileList(filesToProcess, 1, 1);
    }
    else
    {
        Console.WriteLine($"Spawning 4 parallel processes...\n");
        SpawnParallelProcesses(filesToProcess);
        Console.WriteLine("All processes spawned. Monitor the new terminal windows for progress.\n");
    }
}

void SpawnParallelProcesses(List<string> files)
{
    int processCount = 4;
    int filesPerProcess = (int)Math.Ceiling((double)files.Count / processCount);

    string exePath = Process.GetCurrentProcess().MainModule!.FileName;

    for (int i = 0; i < processCount; i++)
    {
        var batch = files.Skip(i * filesPerProcess).Take(filesPerProcess).ToList();
        if (!batch.Any()) continue;

        // Escape and quote file paths for command line
        string fileArgs = string.Join(" ", batch.Select(f => $"\"{f}\""));

        var startInfo = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/k title \"Thumbz Process {i + 1}/{processCount}\" && \"{exePath}\" {fileArgs}",
            UseShellExecute = true,
            CreateNoWindow = false
        };

        Process.Start(startInfo);
        Console.WriteLine($"Process {i + 1}/{processCount}: {batch.Count} files");
    }
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

async Task ProcessFileList(List<string> filesToProcess, int batchNumber, int totalBatches)
{
    int processed = 0, skipped = 0, errors = 0;
    int total = filesToProcess.Count;
    int current = 0;

    string batchInfo = totalBatches > 1 ? $" [Batch {batchNumber}/{totalBatches}]" : "";

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
                Console.WriteLine($"[{current}/{total} - {current * 100 / total}%]{batchInfo} {name}");
                Console.WriteLine("  Skipped (already exists)\n");
                skipped++;
                continue;
            }

            Console.WriteLine($"[{current}/{total} - {current * 100 / total}%]{batchInfo} {name}");
            sw.Restart();

            using (var sheet = thumbz.CreateThumbnailSheet(videoPath))
            {
                sw.Stop();
                if (sheet != null)
                {
                    // Before saving (race condition guard)
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

    Console.WriteLine($"--- Summary{batchInfo} ---");
    Console.WriteLine($"Created: {processed} | Skipped: {skipped} | Errors: {errors}");
    Console.WriteLine("---------------------------------\n");

    if (totalBatches > 1)
    {
        Console.WriteLine("Press any key to close this window...");
        Console.ReadKey();
    }
}