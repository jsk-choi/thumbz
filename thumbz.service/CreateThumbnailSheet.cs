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
                Console.WriteLine($"[Probe Failed] {ex.Message}");
                return null!;
            }

            // --- Layout Calculations ---
            double vidWidth = mediaInfo.PrimaryVideoStream?.Width ?? 1920;
            double vidHeight = mediaInfo.PrimaryVideoStream?.Height ?? 1080;
            _cnf.ThumbnailsDimension = vidHeight > (vidWidth * 1.1) ? _cnf.VerticalVideo : _cnf.HorizontalVideo;

            int cols = _cnf.ThumbnailsDimension.ThumbnailsHorizontal;
            int rows = _cnf.ThumbnailsDimension.ThumbnailsVertical;
            int totalFrames = cols * rows;

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

            // --- Timestamps ---
            var skipSeconds = mediaInfo.Duration.TotalSeconds * _cnf.VideoStartSkipInLengthPercentage;
            var effectiveDuration = mediaInfo.Duration.TotalSeconds - (2 * skipSeconds);
            var interval = effectiveDuration / (totalFrames + 1);

            // --- FAST EXTRACTION ---
            string tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
            Directory.CreateDirectory(tempDir);

            try
            {
                var cmdBuilder = new StringBuilder();

                // 1. INPUTS: Add the file N times with different seek points
                for (int i = 0; i < totalFrames; i++)
                {
                    var ts = TimeSpan.FromSeconds(skipSeconds + (interval * (i + 1)));
                    cmdBuilder.Append($"-ss {ts.TotalSeconds:F3} -i \"{videoPath}\" ");
                }

                // 2. OUTPUTS: Map specific inputs to specific files
                for (int i = 0; i < totalFrames; i++)
                {
                    string outFile = Path.Combine(tempDir, $"{i}.jpg");
                    cmdBuilder.Append($"-map {i}:v:0 -frames:v 1 -vf scale={thumbWidth}:{thumbHeight} \"{outFile}\" ");
                }

                var psi = new ProcessStartInfo
                {
                    FileName = Path.Combine(_cnf.ffmpeg, "ffmpeg.exe"),
                    Arguments = cmdBuilder.ToString() + " -y",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true
                };

                // Show extracting status
                Console.Write("Extracting... ");

                using (var proc = new Process { StartInfo = psi })
                {
                    proc.Start();
                    string errorLog = proc.StandardError.ReadToEnd();
                    proc.WaitForExit();

                    if (proc.ExitCode != 0)
                    {
                        Console.WriteLine($"\n[FFmpeg Error] ExitCode: {proc.ExitCode}");
                        Console.WriteLine(errorLog);
                        return null!;
                    }
                }

                Console.WriteLine("OK");

                // --- Composite ---
                var sheet = new Image<Rgba32>(sheetWidth, sheetHeight);
                sheet.Mutate(ctx =>
                {
                    ctx.BackgroundColor(Color.ParseHex(_cnf.BackgroundColorHex));

                    // Header
                    ctx.DrawText(videoFile.Name, titleFont, Color.ParseHex(_cnf.TitleFontColorHex), new PointF(margin, margin));
                    string details = $"{vidWidth}x{vidHeight} | {mediaInfo.Duration:hh\\:mm\\:ss} | {mediaInfo.PrimaryVideoStream?.CodecName?.ToUpper()}";
                    ctx.DrawText(details, detailFont, Color.ParseHex(_cnf.DetailFontColorHex), new PointF(margin, margin + _cnf.TitleFontSize + 5));

                    // Grid with progress
                    int drawnCount = 0;
                    for (int i = 0; i < totalFrames; i++)
                    {
                        string framePath = Path.Combine(tempDir, $"{i}.jpg");

                        if (!File.Exists(framePath))
                        {
                            continue;
                        }

                        int row = i / cols;
                        int col = i % cols;
                        int x = margin + (col * (thumbWidth + padding));
                        int y = margin + headerHeight + (row * (thumbHeight + padding));

                        using (var img = Image.Load(framePath))
                        {
                            ctx.DrawImage(img, new Point(x, y), 1f);
                        }

                        var ts = TimeSpan.FromSeconds(skipSeconds + (interval * (i + 1)));
                        DrawTimestamp(ctx, ts, timestampFont, x, y, thumbWidth, thumbHeight);
                        drawnCount++;

                        // Update progress bar
                        DrawProgressBar(i + 1, totalFrames);
                    }

                    // Clear progress line and move to next line
                    Console.Write("\r" + new string(' ', 50) + "\r");

                    if (drawnCount == 0) Console.WriteLine("[Warning] FFmpeg ran, but no images were drawn.");
                });

                return sheet;
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        private void DrawProgressBar(int current, int total, int barWidth = 30)
        {
            //double percent = (double)current / total;
            //int filled = (int)(barWidth * percent);
            //int empty = barWidth - filled;

            //string bar = new string('█', filled) + new string('░', empty);
            //string text = $"\rCompositing: [{bar}] {percent * 100:F0}% ({current}/{total})";

            //Console.Write(text);
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
    }
}