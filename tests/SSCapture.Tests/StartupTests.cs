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
        Run("管理者実行はOFFが既定で、ONの手動起動だけが昇格を要求する", () => WithServiceDirectory(directory =>
        {
            string file = Path.Combine(directory, "settings.json");
            var store = new SettingsStore(file);
            Check(!store.Load(out _).RunAsAdministrator, "新規設定がON");
            File.WriteAllText(file, """{"SchemaVersion":3}""");
            Check(!store.Load(out _).RunAsAdministrator, "旧設定がON");
            store.Save(store.Load(out _) with { RunAsAdministrator = true });
            Check(store.Load(out _).RunAsAdministrator, "保存値を読み込めない");

            Check(!LaunchBehavior.RequestsAdministrator([], false), "OFFで昇格を要求した");
            Check(LaunchBehavior.RequestsAdministrator([], true)
                && LaunchBehavior.RequestsAdministrator(["--settings"], true), "手動起動で昇格を要求しない");
            foreach (string[] args in new string[][] { ["--startup"], ["--hidden"], ["--settings", "--startup"],
                ["--startup", "--launch-origin", "direct_task"], ["--capture-select"], [LaunchBehavior.AdministratorHandoff] })
                Check(!LaunchBehavior.RequestsAdministrator(args, true), "自動起動または昇格引き継ぎでUACを再要求した");
            Check(LaunchBehavior.IsAdministratorHandoff([LaunchBehavior.AdministratorHandoff])
                && LaunchBehavior.ShowSettings([LaunchBehavior.AdministratorHandoff]), "昇格後の設定表示を失った");
        }));

        Run("管理者起動の要求はWindowsに委ね、取消と失敗を通常起動へ返す", () =>
        {
            const string exe = @"C:\Development path\SSCapture.exe";
            int calls = 0;
            Equal<string?>(null, LaunchBehavior.StartAsAdministrator(exe, request =>
            {
                calls++;
                Check(request.UseShellExecute, "ShellExecuteを使っていない");
                Equal("runas", request.Verb);
                Equal(exe, request.FileName);
                Equal(Path.GetDirectoryName(exe), request.WorkingDirectory);
                Equal(LaunchBehavior.AdministratorHandoff, request.ArgumentList.Single());
                return true;
            }));
            Equal(1, calls);
            Check(LaunchBehavior.StartAsAdministrator(exe, _ => throw new System.ComponentModel.Win32Exception(1223))
                ?.Contains("取り消されました") == true, "UAC取消を報告しない");
            Check(LaunchBehavior.StartAsAdministrator(exe, _ => throw new System.ComponentModel.Win32Exception(2))
                ?.Contains("起動できませんでした") == true, "起動失敗を報告しない");
            Check(LaunchBehavior.StartAsAdministrator(exe, _ => false) is not null, "起動結果がないのに成功した");
        });

        Run("基本で管理者実行を適用・戻す・初期化でき、自動起動の予約は変更しない", () =>
        {
            var startup = new FakeStartup();
            var saved = new List<bool>();
            using var form = new SettingsForm(new AppSettings(), value => { saved.Add(value.RunAsAdministrator); return null; },
                _ => { }, () => [], startupRegistration: startup);
            var check = (CheckBox)form.Controls.Find("RunAsAdministrator", true).Single();
            var apply = SettingsApplyButton(form, "基本");
            var revert = form.Controls.Find("RevertPageSettings", true).First(control => control.Parent == apply.Parent);
            Check(!check.Checked && !apply.Enabled, "初期状態でONまたは未確定");
            check.Checked = true;
            Check(apply.Enabled && revert.Enabled, "下書きを認識しない");
            RecordingClick(revert);
            Check(!check.Checked && !apply.Enabled && saved.Count == 0, "戻すで未確定値を保持または保存");
            check.Checked = true;
            ApplySettingsPage(form, "基本");
            Check(saved.SequenceEqual([true]) && !apply.Enabled && startup.Writes == 0, "適用値または予約の状態が不正");
            Check(form.Controls.Find("Status", true).Single().Text.Contains("手動起動"), "適用時に反映時期が分からない");
            form.ResetSelectedPage();
            Check(!check.Checked && apply.Enabled && saved.Count == 1, "初期化でOFFの下書きにならない");
            ApplySettingsPage(form, "基本");
            Check(saved.SequenceEqual([true, false]) && startup.Writes == 0, "OFFを保存できないか自動起動も変更された");
        });

        Run("適用済みの管理者実行だけを再起動でき、取消後も押し直せる", () =>
        {
            int restarts = 0;
            using var form = new SettingsForm(new AppSettings(), _ => null, _ => { }, () => [],
                startupRegistration: new FakeStartup(), restartAsAdministrator: () => { restarts++; return "模擬のUAC取消"; });
            var toggle = (CheckBox)form.Controls.Find("RunAsAdministrator", true).Single();
            var restart = form.Controls.Find("RestartAsAdministrator", true).Single();
            Check(!restart.Enabled, "OFFのときに再起動できる");
            toggle.Checked = true;
            Check(!restart.Enabled, "適用していない設定で再起動できる");
            ApplySettingsPage(form, "基本");
            Check(restart.Enabled && form.Controls.Find("Status", true).Single().Text.Contains("再起動"), "再起動を案内しない");
            RecordingClick(restart);
            Equal(1, restarts);
            Check(restart.Enabled && form.Controls.Find("Status", true).Single().Text.Contains("模擬のUAC取消"), "取消後に再試行できない");
            toggle.Checked = false;
            Check(!restart.Enabled, "OFFの下書きを無視して再起動できる");
        });

        Run("自動起動だけの変更は基本の適用で確定し、戻すと所有者の制限を守る", () =>
        {
            var fake = new FakeStartup();
            int saves = 0;
            using var form = new SettingsForm(new AppSettings(), _ => { saves++; return null; }, _ => { }, () => [], startupRegistration: fake);
            var editor = (StartupSettingsControl)form.Controls.Find("StartupSettings", true).Single();
            var check = (CheckBox)editor.Controls.Find("StartWithWindows", true).Single();
            var apply = SettingsApplyButton(form, "基本");
            var revert = form.Controls.Find("RevertPageSettings", true).First(control => control.Parent == apply.Parent);
            Check(!check.Checked && !check.Enabled && !apply.Enabled, "読取前に既定ONまたは編集可能");
            editor.RefreshAsync().GetAwaiter().GetResult();
            Check(!check.Checked && check.Enabled && !apply.Enabled && fake.Writes == 0, "読取だけで予約変更");

            check.Checked = true;
            Check(apply.Enabled && revert.Enabled && fake.Writes == 0, "自動起動だけの下書きを認識しない");
            RecordingClick(revert);
            Check(!check.Checked && !apply.Enabled && fake.Writes == 0, "戻す操作で登録したか下書きが残っている");

            check.Checked = true;
            ApplySettingsPage(form, "基本");
            Equal(1, fake.Writes);
            Equal(0, saves);
            Check(check.Checked && fake.State.Enabled && !apply.Enabled, "適用後の登録状態が一致しない");

            form.ResetSelectedPage();
            Check(!check.Checked && apply.Enabled && fake.State.Enabled, "初期化で登録を解除したか下書きを作れなかった");
            ApplySettingsPage(form, "基本");
            Equal(2, fake.Writes);
            Check(!fake.State.Enabled && !apply.Enabled, "解除を適用できない");

            fake.State = new(true, false, "本体経由");
            editor.RefreshAsync().GetAwaiter().GetResult();
            Check(check.Checked && !check.Enabled && !apply.Enabled, "経由起動を重複登録できる");
        });
        Run("基本のガイドと自動起動欄は最小サイズでも表示領域に収まる", () =>
        {
            var startup = new FakeStartup();
            int saves = 0;
            using var form = new SettingsForm(new AppSettings(), _ => { saves++; return null; }, _ => { }, () => [], startupRegistration: startup);
            form.Size = form.MinimumSize;
            _ = form.Handle; foreach (var control in Descendants(form)) _ = control.Handle;
            LayoutTree(form);
            var editor = (StartupSettingsControl)form.Controls.Find("StartupSettings", true).Single();
            editor.RefreshAsync().GetAwaiter().GetResult(); LayoutTree(form);
            var check = form.Controls.Find("StartWithWindows", true).Single();
            var apply = SettingsApplyButton(form, "基本");
            var message = form.Controls.Find("StartupState", true).Single();
            var screenGuide = form.Controls.Find("ShowSelectionGuide", true).Single();
            var pointerGuide = form.Controls.Find("ShowPointerGuide", true).Single();
            var magnifier = form.Controls.Find("ShowMagnifier", true).Single();
            var display = form.Controls.Find("SelectionGuideDisplay", true).Single();
            Check(new[] { screenGuide, pointerGuide, magnifier, display }.All(field => field.Parent!.ClientRectangle.Contains(field.Bounds))
                && screenGuide.Right <= display.Left && screenGuide.Bottom <= pointerGuide.Top && pointerGuide.Bottom <= magnifier.Top,
                "ガイドのチェック欄や表示先が切れるか重なる");
            Check(editor.Parent!.ClientRectangle.Contains(editor.Bounds), "自動起動欄が親領域外");
            Check(check.Parent!.ClientRectangle.Contains(check.Bounds) && apply.Parent!.ClientRectangle.Contains(apply.Bounds), "自動起動欄または適用ボタンが親領域外");
            Equal(RegionBoundsOnForm(screenGuide).Left, RegionBoundsOnForm(check).Left);
            var runAs = form.Controls.Find("RunAsAdministrator", true).Single();
            Check(runAs.Parent!.ClientRectangle.Contains(runAs.Bounds), "起動権限のチェック欄が収まらない");
            Equal(RegionBoundsOnForm(check).Left, RegionBoundsOnForm(runAs).Left);
            Check(form.Controls.Find("ApplyStartup", true).Length == 0 && apply.TabStop, "基本の適用に統合されていない");
            ((CheckBox)check).Checked = true;
            Check(apply.Enabled, "自動起動だけの変更で基本の適用が有効にならない");
            ((CheckBox)form.Controls.Find("DimCaptureBackground", true).Single()).Checked = false;
            ApplySettingsPage(form, "基本");
            Equal(1, saves);
            Equal(1, startup.Writes);
            Check(message.Parent!.ClientRectangle.Contains(message.Bounds), "状態メッセージが表示領域外");
            Check(RegionBoundsOnForm(message).Left >= RegionBoundsOnForm(check).Left + 12, "状態メッセージが字下げされていない");
            Check(apply.Height >= apply.Font.Height + 4, "適用ボタンがつぶれている");
        });
        Run("基本の適用は保存失敗時に自動起動を変えず、登録に失敗した場合は再適用できる", () =>
        {
            const string registrationError = "模擬登録エラー";
            const string writeError = "模擬保存エラー";
            var startup = new FakeStartup { Error = registrationError };
            string? saveError = writeError;
            int saves = 0;
            using var form = new SettingsForm(new AppSettings(), _ => { saves++; return saveError; }, _ => { }, () => [], startupRegistration: startup);
            var editor = (StartupSettingsControl)form.Controls.Find("StartupSettings", true).Single();
            editor.RefreshAsync().GetAwaiter().GetResult();
            var check = (CheckBox)form.Controls.Find("StartWithWindows", true).Single();
            var dim = (CheckBox)form.Controls.Find("DimCaptureBackground", true).Single();
            var status = form.Controls.Find("Status", true).Single();
            check.Checked = true;
            dim.Checked = !dim.Checked;

            ApplySettingsPage(form, "基本");
            Equal(1, saves);
            Equal(0, startup.Writes);
            Check(status.Text.Contains(writeError, StringComparison.Ordinal) && check.Checked, "保存エラーまたは自動起動の下書きが失われた");

            saveError = null;
            ApplySettingsPage(form, "基本");
            Equal(2, saves);
            Equal(1, startup.Writes);
            Check(status.Text.Contains("基本設定を保存しました", StringComparison.Ordinal)
                && status.Text.Contains(registrationError, StringComparison.Ordinal), "一部だけ適用されたことが分からない");
            Check(check.Checked && !startup.State.Enabled && SettingsApplyButton(form, "基本").Enabled, "失敗した登録をやり直せない");

            startup.Error = null;
            ApplySettingsPage(form, "基本");
            Equal(2, saves);
            Equal(2, startup.Writes);
            Check(startup.State.Enabled && !SettingsApplyButton(form, "基本").Enabled, "やり直しで登録状態が確定しない");
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
