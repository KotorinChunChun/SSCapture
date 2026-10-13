namespace SSCapture.Services;

// 副作用のない操作可否。録画中の終了拒否と通知の保留条件は別の方針として保持する。
internal readonly record struct TrayOperationPolicy(
    bool Busy, bool Recording, bool RecordingTransition, bool Naming, bool PreparingRecording)
{
    public bool MustWaitToStop => Busy || Recording || RecordingTransition;
    public bool MustDeferFeedback => Busy || Naming || PreparingRecording || RecordingTransition;
}
