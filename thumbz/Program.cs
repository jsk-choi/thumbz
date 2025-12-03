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
        await ProcessDirectory(path);
    }
    else
    {
        Console.WriteLine($"Directory not found: {path}");
    }
}

async Task ProcessDirectory(string path)
{
    Console.WriteLine($"Scanning: {path}...");

    // Get video files and RANDOMIZE order
    var videoFiles = Directory.EnumerateFiles(path, "*.*", SearchOption.AllDirectories)
        .Where(file => cnf.VideoExtensions.Contains(Path.GetExtension(file).ToLower()))
        .OrderBy(_ => Random.Shared.Next())
        .ToList();

    int processed = 0, skipped = 0, errors = 0;

    foreach (var videoPath in videoFiles)
    {
        try
        {
            string? dir = Path.GetDirectoryName(videoPath);
            string name = Path.GetFileNameWithoutExtension(videoPath);
            string expectedSheetPath = Path.Combine(dir!, $"{name}{cnf.SheetFileType}");

            // CHECK 1: Before processing
            if (File.Exists(expectedSheetPath))
            {
                skipped++;
                continue;
            }

            Console.Write($"[{DateTime.Now:HH:mm:ss}] {name}... ");
            sw.Restart();

            using (var sheet = thumbz.CreateThumbnailSheet(videoPath))
            {
                sw.Stop();

                if (sheet != null)
                {
                    // CHECK 2: Before saving
                    if (File.Exists(expectedSheetPath))
                    {
                        Console.WriteLine($"Skipped (already created)");
                        skipped++;
                        continue;
                    }

                    await sheet.SaveAsync(expectedSheetPath);
                    Console.WriteLine($"Done ({sw.Elapsed.TotalSeconds:F2}s)");
                    processed++;
                }
                else
                {
                    Console.WriteLine("Failed (Null Output)");
                    errors++;
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error: {ex.Message}");
            errors++;
        }
    }

    Console.WriteLine($"\n--- Summary for {path} ---");
    Console.WriteLine($"Created: {processed} | Skipped: {skipped} | Errors: {errors}");
    Console.WriteLine("---------------------------------\n");
}