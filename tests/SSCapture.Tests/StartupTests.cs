using SSCapture.Services;
using SSCapture.UI;

namespace SSCapture.Tests;

internal static partial class Program
{
    static void StartupTests()
    {
        Run("通常起動と二重起動は設定表示・自動起動とhiddenは無表示", () =>
        {
            Check(LaunchBehavior.ShowSettings([]), "無引数起動で設定を表示しない");
            Check(LaunchBehavior.ShowSettings(["--settings"]), "明示設定表示を失った");
            foreach (string[] args in new string[][] { ["--startup"], ["--hidden"], ["--settings", "--startup"], ["--settings", "--hidden"], ["--startup", "--launch-origin", "direct_task"] })
                Check(!LaunchBehavior.ShowSettings(args), "無表示指定より画面表示が優先された");
        });
        Run("自動起動の設定欄は初期OFFで読取・適用を明示する", () =>
        {
            var fake = new FakeStartup();
            using var editor = new StartupSettingsControl(fake);
            var check = (CheckBox)editor.Controls.Find("StartWithWindows", true).Single();
            var apply = (Button)editor.Controls.Find("ApplyStartup", true).Single();
            Check(!check.Checked && !check.Enabled && !apply.Enabled, "読取前に既定ONまたは編集可能");
            editor.RefreshAsync().GetAwaiter().GetResult();
            Check(!check.Checked && check.Enabled && !apply.Enabled && fake.Writes == 0, "読取だけで予約変更");
            check.Checked = true;
            Check(apply.Enabled && fake.Writes == 0, "選択だけで予約変更");
            editor.CreateControl(); apply.PerformClick();
            Equal(1, fake.Writes); Check(check.Checked && !apply.Enabled, "適用後の状態を保持していない");
            fake.State = new(true, false, "本体経由");
            editor.RefreshAsync().GetAwaiter().GetResult();
            Check(check.Checked && !check.Enabled && !apply.Enabled, "経由起動を重複登録できる");
        });
        Run("基本の自動起動欄は最小サイズでもチェック・適用・案内が重ならない", () =>
        {
            using var form = new SettingsForm(new AppSettings(), _ => null, () => { }, () => [], startupRegistration: new FakeStartup());
            form.Size = form.MinimumSize;
            _ = form.Handle; foreach (var control in Descendants(form)) _ = control.Handle;
            LayoutTree(form);
            var editor = (StartupSettingsControl)form.Controls.Find("StartupSettings", true).Single();
            editor.RefreshAsync().GetAwaiter().GetResult(); LayoutTree(form);
            var check = form.Controls.Find("StartWithWindows", true).Single();
            var apply = form.Controls.Find("ApplyStartup", true).Single();
            var message = form.Controls.Find("StartupState", true).Single();
            Check(editor.Parent!.ClientRectangle.Contains(editor.Bounds), "自動起動欄が親領域外");
            Check(check.Parent!.ClientRectangle.Contains(check.Bounds) && apply.Parent!.ClientRectangle.Contains(apply.Bounds), "自動起動ボタンが親領域外");
            Check(check.Right <= apply.Left, "チェックと適用が重なる");
            Check(message.Parent!.ClientRectangle.Contains(message.Bounds), "説明が親領域外");
            Check(apply.Height >= apply.Font.Height + 4, "適用ボタンがつぶれている");
        });
    }
    sealed class FakeStartup : IStartupRegistration
    {
        internal StartupRegistrationState State = new(false, true, "OFF");
        internal int Writes;
        public Task<StartupRegistrationState> ReadAsync() => Task.FromResult(State);
        public Task<string?> SetAsync(bool enabled) { Writes++; State = State with { Enabled = enabled, Detail = enabled ? "ON（模擬予約・画面を表示せず起動）" : "OFF（模擬予約なし）" }; return Task.FromResult<string?>(null); }
    }
}
