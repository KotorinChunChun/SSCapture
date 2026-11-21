namespace SSCapture;

internal enum CaptureAnchor { TopLeft, TopRight, BottomRight, BottomLeft, Center }

internal static class CaptureAnchorExtensions
{
    internal static string Label(this CaptureAnchor anchor) => anchor switch
    {
        CaptureAnchor.TopRight => "右上",
        CaptureAnchor.BottomRight => "右下",
        CaptureAnchor.BottomLeft => "左下",
        CaptureAnchor.Center => "中心",
        _ => "左上"
    };
}
