using SSCapture.Services;

namespace SSCapture.Tests;

internal static partial class Program
{
    static partial void DeliveryWaitTests()
    {
        Run("出力待機: 完了済みの出力は期限0でも成功", () => RunOutputTask(async () =>
        {
            Check(await DeliveryWait.CompletedWithinAsync(Task.CompletedTask, TimeSpan.Zero), "完了を期限切れ扱いにした");
        }));
        Run("出力待機: 期限切れでも出力タスクをキャンセルしない", () => RunOutputTask(async () =>
        {
            var delivery = NewWaitSignal();
            Check(!await DeliveryWait.CompletedWithinAsync(delivery.Task, TimeSpan.Zero), "未完了なのに成功した");
            Check(!delivery.Task.IsCompleted, "元の出力を完了/キャンセルした");
            delivery.SetResult();
            Check(await DeliveryWait.CompletedWithinAsync(delivery.Task, TimeSpan.Zero), "遅れて完了した結果を受け取れない");
        }));
        Run("出力待機: OCR入力中には期限を適用せず入力後に適用", () => RunOutputTask(async () =>
        {
            var delivery = NewWaitSignal();
            var naming = NewWaitSignal();
            var waiting = DeliveryWait.CompletedWithinAsync(delivery.Task, TimeSpan.Zero, naming.Task);
            Check(!waiting.IsCompleted, "OCR入力待ちを期限切れ扱いにした");
            naming.SetResult();
            Check(!await waiting.WaitAsync(TimeSpan.FromSeconds(3)), "入力後の未完了出力に期限を適用しない");
            Check(!delivery.Task.IsCompleted, "期限切れで出力を中止した");
            delivery.SetResult();
        }));
        Run("出力待機: 入力待ち中の出力完了は入力シグナルなしでも返る", () => RunOutputTask(async () =>
        {
            var delivery = NewWaitSignal();
            var naming = NewWaitSignal();
            var waiting = DeliveryWait.CompletedWithinAsync(delivery.Task, TimeSpan.Zero, naming.Task);
            delivery.SetResult();
            Check(await waiting.WaitAsync(TimeSpan.FromSeconds(3)), "入力待ちから抜けられない");
            Check(!naming.Task.IsCompleted, "入力シグナルを書き換えた");
        }));
        Run("出力待機: 命名前の失敗はその元例外を返す", () => RunOutputTask(async () =>
        {
            var delivery = NewWaitSignal();
            var naming = NewWaitSignal();
            var error = new IOException("synthetic output failure");
            var waiting = DeliveryWait.CompletedWithinAsync(delivery.Task, TimeSpan.Zero, naming.Task);
            delivery.SetException(error);
            try { await waiting.WaitAsync(TimeSpan.FromSeconds(3)); throw new InvalidOperationException("出力失敗を隠した"); }
            catch (IOException actual) { Check(ReferenceEquals(error, actual), "元例外を置き換えた"); }
        }));
        Run("出力待機: 出力処理自身のTimeoutExceptionは期限切れに読み替えない", () => RunOutputTask(async () =>
        {
            var error = new TimeoutException("output operation timed out");
            try
            {
                await DeliveryWait.CompletedWithinAsync(Task.FromException(error), TimeSpan.Zero);
                throw new InvalidOperationException("処理失敗を背景処理中と判断した");
            }
            catch (TimeoutException actual) { Check(ReferenceEquals(error, actual), "元の処理例外を失った"); }
        }));
        Run("出力待機: 出力のキャンセルは入力待ち中でも保持", () => RunOutputTask(async () =>
        {
            var naming = NewWaitSignal();
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            try
            {
                await DeliveryWait.CompletedWithinAsync(Task.FromCanceled(cancellation.Token), TimeSpan.Zero, naming.Task);
                throw new InvalidOperationException("キャンセルを正常完了として扱った");
            }
            catch (OperationCanceledException actual) { Equal(cancellation.Token, actual.CancellationToken); }
        }));
    }

    private static TaskCompletionSource NewWaitSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
