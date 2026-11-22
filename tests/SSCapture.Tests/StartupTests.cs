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
            var neutral = apply.BackColor;
            check.Checked = true;
            Check(apply.Enabled && fake.Writes == 0, "選択だけで予約変更");
            if (!SystemInformation.HighContrast)
                Check(apply.BackColor.ToArgb() == Color.FromArgb(0x18, 0x5A, 0x31).ToArgb() && apply.ForeColor.ToArgb() == Color.White.ToArgb(), "変更を濃い緑と白文字で表示していない");
            check.Checked = false;
            Check(!apply.Enabled && apply.BackColor == neutral, "元に戻しても差分が残った");
            check.Checked = true;
            editor.CreateControl(); apply.PerformClick();
            Equal(1, fake.Writes); Check(check.Checked && !apply.Enabled, "適用後の状態を保持していない");
            fake.Error = "模擬失敗";
            check.Checked = false;
            apply.PerformClick();
            Equal(2, fake.Writes);
            var state = editor.Controls.Find("StartupState", true).Single();
            Check(check.Checked && !apply.Enabled && state.Text.Contains(fake.Error, StringComparison.Ordinal), "適用失敗の読戻し・エラー表示がない");
            fake.State = new(true, false, "本体経由");
            editor.RefreshAsync().GetAwaiter().GetResult();
            Check(check.Checked && !check.Enabled && !apply.Enabled, "経由起動を重複登録できる");
        });
        Run("基本のガイドと自動起動欄は最小サイズでも表示領域に収まる", () =>
        {
            var startup = new FakeStartup();
            int saves = 0;
            using var form = new SettingsForm(new AppSettings(), _ => { saves++; return null; }, () => { }, () => [], startupRegistration: startup);
            form.Size = form.MinimumSize;
            _ = form.Handle; foreach (var control in Descendants(form)) _ = control.Handle;
            LayoutTree(form);
            var editor = (StartupSettingsControl)form.Controls.Find("StartupSettings", true).Single();
            editor.RefreshAsync().GetAwaiter().GetResult(); LayoutTree(form);
            var check = form.Controls.Find("StartWithWindows", true).Single();
            var apply = form.Controls.Find("ApplyStartup", true).Single();
            var message = form.Controls.Find("StartupState", true).Single();
            var screenGuide = form.Controls.Find("ShowSelectionGuide", true).Single();
            var pointerGuide = form.Controls.Find("ShowPointerGuide", true).Single();
            var magnifier = form.Controls.Find("ShowMagnifier", true).Single();
            var display = form.Controls.Find("SelectionGuideDisplay", true).Single();
            Check(new[] { screenGuide, pointerGuide, magnifier, display }.All(field => field.Parent!.ClientRectangle.Contains(field.Bounds))
                && screenGuide.Right <= display.Left && screenGuide.Bottom <= pointerGuide.Top && pointerGuide.Bottom <= magnifier.Top,
                "ガイドのチェック欄や表示先が切れるか重なる");
            Check(editor.Parent!.ClientRectangle.Contains(editor.Bounds), "自動起動欄が親領域外");
            Check(check.Parent!.ClientRectangle.Contains(check.Bounds) && apply.Parent!.ClientRectangle.Contains(apply.Bounds), "自動起動ボタンが親領域外");
            var label = form.Controls.Find("StartupLabel", true).Single();
            Check(label.Right <= apply.Left && apply.Left - label.Right <= 12 && apply.Right <= check.Left && apply.TabStop,
                "見出しの隣にキーボードで選べる適用ボタンがない");
            ((CheckBox)check).Checked = true;
            RecordingClick(form.Controls.Find("SaveSettings", true).Single());
            Equal(1, saves);
            Equal(0, startup.Writes);
            Check(message.Parent!.ClientRectangle.Contains(message.Bounds), "状態メッセージが表示領域外");
            Check(message.Left >= label.Parent!.Left + label.Left + 12, "状態メッセージが字下げされていない");
            Check(apply.Height >= apply.Font.Height + 4, "適用ボタンがつぶれている");
        });
        Run("自動起動CLIは状態を読むだけでは書き込まず、明示した変更のみ適用する", () =>
        {
            var startup = new FakeStartup();
            var store = new SettingsStore("settings.json");
            var output = new StringWriter();
            SettingsCommandLine.Run(["--startup-status"], store, output, startup);
            Check(output.ToString().Contains("\"enabled\":false", StringComparison.Ordinal) && startup.Writes == 0, "状態取得で書き込んだ");
            SettingsCommandLine.Run(["--startup-enable"], store, output, startup);
            Check(startup.State.Enabled && startup.Writes == 1, "有効化が1回でない");
            SettingsCommandLine.Run(["--startup-disable"], store, output, startup);
            Check(!startup.State.Enabled && startup.Writes == 2, "無効化が1回でない");
        });
    }
    sealed class FakeStartup : IStartupRegistration
    {
        internal StartupRegistrationState State = new(false, true, "OFF");
        internal int Writes;
        internal string? Error;
        public Task<StartupRegistrationState> ReadAsync() => Task.FromResult(State);
        public Task<string?> SetAsync(bool enabled) { Writes++; if (Error is not null) return Task.FromResult<string?>(Error); State = State with { Enabled = enabled, Detail = enabled ? "ON（模擬予約・画面を表示せず起動）" : "OFF（模擬予約なし）" }; return Task.FromResult<string?>(null); }
    }
}
