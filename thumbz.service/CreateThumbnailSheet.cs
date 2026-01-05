using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.Fonts;
using FFMpegCore;
using System.Diagnostics;
using System.Text;

namespace thumbz.service
{
    public class Thumbz
    {
        public ThumbnailSheetConfig _cnf { get; set; }

        public Thumbz(ThumbnailSheetConfig cnf)
        {
            _cnf = cnf;
            GlobalFFOptions.Configure(options =>
            {
                options.BinaryFolder = _cnf.ffmpeg;
                options.LogLevel = FFMpegCore.Enums.FFMpegLogLevel.Error;
            });
        }

        public Image<Rgba32> CreateThumbnailSheet(string videoPath)
        {
            if (!File.Exists(videoPath)) return null!;

            FileInfo videoFile = new(videoPath);
            IMediaAnalysis mediaInfo;

            try
            {
                mediaInfo = FFProbe.Analyse(videoPath, new FFOptions { BinaryFolder = _cnf.ffmpeg });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  [Probe Failed] {ex.Message}");
                return null!;
            }

            // --- Layout Calculations ---
            double vidWidth = mediaInfo.PrimaryVideoStream?.Width ?? 1920;
            double vidHeight = mediaInfo.PrimaryVideoStream?.Height ?? 1080;
            _cnf.ThumbnailsDimension = vidHeight > (vidWidth * 1.1) ? _cnf.VerticalVideo : _cnf.HorizontalVideo;

            int cols = _cnf.ThumbnailsDimension.ThumbnailsHorizontal;
            int rows = _cnf.ThumbnailsDimension.ThumbnailsVertical;
            int totalFrames = cols * rows;
            int framesToExtract = totalFrames + 2; // Extract 2 extra to skip first and last

            int sheetWidth = _cnf.FinalSheetWidthPx;
            int margin = _cnf.SheetMarginPx;
            int padding = _cnf.ThumbnailPaddingPx;

            int availableWidth = sheetWidth - (2 * margin) - ((cols - 1) * padding);
            int thumbWidth = availableWidth / cols;
            double aspectRatio = vidHeight / vidWidth;
            int thumbHeight = (int)(thumbWidth * aspectRatio);

            // Fonts & Colors
            var titleFont = SystemFonts.CreateFont(_cnf.TitleFontFamily, _cnf.TitleFontSize);
            var detailFont = SystemFonts.CreateFont(_cnf.DetailFontFamily, _cnf.DetailFontSize);
            var timestampFont = SystemFonts.CreateFont(_cnf.DetailFontFamily, (int)(thumbHeight * 0.1), FontStyle.Bold);

            int headerHeight = _cnf.TitleFontSize + _cnf.DetailFontSize + 10;
            int sheetHeight = (2 * margin) + headerHeight + (rows * thumbHeight) + ((rows - 1) * padding);

            // --- FAST EXTRACTION ---
            string tempDirPrefx = "__aa__";
            CleanupTempDirectories(tempDirPrefx);


            string tempDir = Path.Combine(Path.GetTempPath(), tempDirPrefx + Guid.NewGuid().ToString());
            Directory.CreateDirectory(tempDir);

            try
            {
                // Calculate FPS to extract the desired number of frames
                double fps = framesToExtract / mediaInfo.Duration.TotalSeconds;

                var psi = new ProcessStartInfo
                {
                    FileName = Path.Combine(_cnf.ffmpeg, "ffmpeg.exe"),
                    Arguments = $"-i \"{videoPath}\" -vf \"fps={fps:F6},scale={thumbWidth}:{thumbHeight},format=yuvj420p\" -fps_mode vfr -q:v 8 \"{Path.Combine(tempDir, "frame_%04d.jpg")}\" -y",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true
                };

                Console.WriteLine("  Extracting frames...");

                using (var proc = new Process { StartInfo = psi })
                {
                    proc.Start();
                    string errorLog = proc.StandardError.ReadToEnd();
                    proc.WaitForExit();

                    if (proc.ExitCode != 0)
                    {
                        Console.WriteLine($"  [FFmpeg Error] ExitCode: {proc.ExitCode}");
                        Console.WriteLine($"  {errorLog}");
                        return null!;
                    }
                }

                Console.WriteLine("  Compositing sheet...");

                // Get extracted frames (skip first and last)
                var frameFiles = Directory.GetFiles(tempDir, "frame_*.jpg")
                    .OrderBy(f => f)
                    .Skip(1) // Skip first
                    .Take(totalFrames) // Take only what we need
                    .ToList();

                if (frameFiles.Count < totalFrames)
                {
                    Console.WriteLine($"  [Warning] Only {frameFiles.Count} frames extracted, expected {totalFrames}");
                }

                // Calculate time interval for timestamps
                double interval = mediaInfo.Duration.TotalSeconds / (framesToExtract - 1);

                // --- Composite ---
                var sheet = new Image<Rgba32>(sheetWidth, sheetHeight);
                sheet.Mutate(ctx =>
                {
                    ctx.BackgroundColor(Color.ParseHex(_cnf.BackgroundColorHex));

                    // Header
                    ctx.DrawText(videoFile.Name, titleFont, Color.ParseHex(_cnf.TitleFontColorHex), new PointF(margin, margin));

                    double fileSizeMB = videoFile.Length / 1024.0 / 1024.0;
                    string fileSizeStr = fileSizeMB >= 1024
                        ? $"{fileSizeMB / 1024:F1}GB"
                        : $"{fileSizeMB:F0}MB";
                    string details = $"{fileSizeStr} | {vidWidth}x{vidHeight} | {mediaInfo.Duration:hh\\:mm\\:ss} | {mediaInfo.PrimaryVideoStream?.CodecName?.ToUpper()}";

                    ctx.DrawText(details, detailFont, Color.ParseHex(_cnf.DetailFontColorHex), new PointF(margin, margin + _cnf.TitleFontSize + 5));

                    // Grid
                    for (int i = 0; i < frameFiles.Count; i++)
                    {
                        int row = i / cols;
                        int col = i % cols;
                        int x = margin + (col * (thumbWidth + padding));
                        int y = margin + headerHeight + (row * (thumbHeight + padding));

                        using (var img = Image.Load(frameFiles[i]))
                        {
                            ctx.DrawImage(img, new Point(x, y), 1f);
                        }

                        // Calculate timestamp for this frame (accounting for skipped first frame)
                        var ts = TimeSpan.FromSeconds(interval * (i + 1));
                        DrawTimestamp(ctx, ts, timestampFont, x, y, thumbWidth, thumbHeight);
                    }
                });

                return sheet;
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        private void DrawTimestamp(IImageProcessingContext ctx, TimeSpan ts, Font font, int x, int y, int w, int h)
        {
            string text = ts.TotalHours >= 1 ? ts.ToString(@"hh\:mm\:ss") : ts.ToString(@"mm\:ss");
            var textSize = TextMeasurer.MeasureSize(text, new TextOptions(font));
            int p = (int)(font.Size * 0.3);
            int bx = x + w - (int)textSize.Width - (p * 2);
            int by = y + h - (int)textSize.Height - (p * 2);

            ctx.Fill(Color.ParseHex("#000000").WithAlpha(0.7f), new RectangleF(bx, by, textSize.Width + p * 2, textSize.Height + p * 2));
            ctx.DrawText(text, font, Color.White, new PointF(bx + p - 4, by + p - 4));
        }


        // In your cleanup routine or before starting
        public static void CleanupTempDirectories(string tempDirPrefix)
        {
            string tempPath = Path.GetTempPath();

            var dirsToDelete = Directory.GetDirectories(tempPath, $"{tempDirPrefix}*");

            foreach (var dir in dirsToDelete)
            {
                try
                {
                    Directory.Delete(dir, true);
                }
                catch
                {
                    // Ignore failures - might be in use
                }
            }
        }





    }
}