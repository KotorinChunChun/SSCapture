using SSCapture.Capture;

namespace SSCapture.Tests;

internal static partial class Program
{
    private static void PresentationAreaTests()
    {
        Run("録画用の表示範囲は x,y,幅,高さ の整数だけを受け付ける", () =>
        {
            Equal(new Rectangle(0, 112, 1280, 532), PresentationArea.Parse("0,112,1280,532"));
            Equal(new Rectangle(-1920, 10, 640, 480), PresentationArea.Parse(" -1920 , 10 , 640 , 480 "));
            foreach (string? invalid in new[] { null, "", "  ", "0,0,1280", "0,0,1280,720,1", "0,0,0,720", "0,0,1280,-1", "a,0,1280,720", "0.5,0,1280,720" })
                Equal((Rectangle?)null, PresentationArea.Parse(invalid));
        });

        Run("録画用の表示範囲はモニターのクライアント座標に直して切り詰める", () =>
        {
            Rectangle client = new(0, 0, 1920, 1080);
            Equal(client, PresentationArea.Within(client, new Rectangle(0, 0, 1920, 1080), null));
            Equal(new Rectangle(0, 112, 1280, 532), PresentationArea.Within(client, new Rectangle(0, 0, 1920, 1080), new Rectangle(0, 112, 1280, 532)));
            // 右のモニター（左上1920,0）では、画面座標をモニター内の座標に直す。
            Equal(new Rectangle(100, 0, 500, 300), PresentationArea.Within(client, new Rectangle(1920, 0, 1920, 1080), new Rectangle(2020, -50, 500, 350)));
            // 重ならないモニターでは、案内が消えないようモニター全体を使う。
            Equal(client, PresentationArea.Within(client, new Rectangle(1920, 0, 1920, 1080), new Rectangle(0, 0, 1280, 720)));
        });
    }
}
