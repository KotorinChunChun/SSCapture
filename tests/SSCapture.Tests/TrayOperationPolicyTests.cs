using SSCapture.Services;

namespace SSCapture.Tests;

internal static partial class Program
{
    static void TrayOperationPolicyTests()
    {
        for (int mask = 0; mask < 32; mask++)
        {
            var busy = (mask & 1) != 0;
            var recording = (mask & 2) != 0;
            var transition = (mask & 4) != 0;
            var naming = (mask & 8) != 0;
            var preparing = (mask & 16) != 0;
            Run("終了拒否と通知保留の既存条件を保持: 状態" + mask, () =>
            {
                var policy = new TrayOperationPolicy(busy, recording, transition, naming, preparing);
                Equal(busy || recording || transition, policy.MustWaitToStop);
                Equal(busy || naming || preparing || transition, policy.MustDeferFeedback);
            });
        }
    }
}
