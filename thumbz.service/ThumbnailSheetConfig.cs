
namespace thumbz.service
{


    public class ThumbnailsDimension
    {
        public int ThumbnailsHorizontal { get; set; } = 5;
        public int ThumbnailsVertical { get; set; } = 5;
    }


    public class ThumbnailSheetConfig
    {
        public string ffmpeg { get; set; } = "C:\\Program Files\\ffmpeg";
        public int ThumbnailPaddingPx { get; set; } = 2;
        public int SheetMarginPx { get; set; } = 7;
        public int FinalSheetWidthPx { get; set; } = 3500;
        public string BackgroundColorHex { get; set; } = "#2596be";
        public string TitleFontFamily { get; set; } = "Courier New";
        public int TitleFontSize { get; set; } = 45;
        public string TitleFontColorHex { get; set; } = "#000000";
        public string DetailFontFamily { get; set; } = "Courier New";
        public int DetailFontSize { get; set; } = 40;
        public string DetailFontColorHex { get; set; } = "#000000";
        public double VideoStartSkipInLengthPercentage { get; set; } = 0.02;
        public string SheetFileType { get; set; } = ".jpg";
        public List<string> VideoExtensions { get; set; } = [".mp4", ".avi", ".mkv", ".mov", ".wmv"];
        public ThumbnailsDimension HorizontalVideo { get; set; } = new();
        public ThumbnailsDimension VerticalVideo { get; set; } = new();

        public ThumbnailsDimension ThumbnailsDimension { get; set; } = new();
    }






}



