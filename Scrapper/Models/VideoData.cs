public class VideoData
{
    public string Title { get; set; }
    public string Url { get; set; }
    public string VideoSourceUrl { get; set; }
    public string PostId { get; set; }
    public bool Downloaded { get; set; }
    public string DownloadPath { get; set; }
    public DateTime? DownloadDate { get; set; }
}