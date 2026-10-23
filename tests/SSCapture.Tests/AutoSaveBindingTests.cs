using System.Reflection;
using SSCapture.Services;
using SSCapture.UI;

namespace SSCapture.Tests;

internal static partial class Program
{
    private static void AutoSaveBindingTests()
    {
        Run("共通の自動保存配線は入れ子の確定・選択変更だけを保存する", () =>
        {
            using var root = new Panel();
            var nested = new Panel(); root.Controls.Add(nested);
            var text = new TextBox(); var number = new NumericUpDown(); var check = new CheckBox(); var combo = new ComboBox(); var button = new Button();
            combo.Items.AddRange(["A", "B"]);
            nested.Controls.AddRange([text, number, check, combo, button]);
            int commits = 0;
            PresetAutoSaveBinding.Attach(root, () => true, () => commits++);
            text.Text = "編集中"; number.Value = 12; Equal(0, commits);
            check.Checked = true; Equal(1, commits);
            combo.SelectedIndex = 1; Equal(2, commits);
            CommitPresetField(text); Equal(3, commits);
            CommitPresetField(number); Equal(4, commits);
            var letter = RaiseAutoSaveKey(text, Keys.A); Equal(4, commits);
            Check(!letter.Handled && !letter.SuppressKeyPress, "通常の文字入力を抑止");
            var enter = RaiseAutoSaveKey(number, Keys.Enter); Equal(5, commits);
            Check(enter.Handled && enter.SuppressKeyPress, "Enterが全体保存へ漏れる");
            typeof(Control).GetMethod("OnClick", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(button, [EventArgs.Empty]);
            Equal(5, commits);
        });

        Run("共通の自動保存配線は毎回ガードを評価し抑止中もEnterを消費する", () =>
        {
            using var root = new Panel(); var text = new TextBox(); var check = new CheckBox();
            var combo = new ComboBox(); combo.Items.AddRange(["A", "B"]); root.Controls.AddRange([text, check, combo]);
            int commits = 0, guardCalls = 0; bool enabled = false;
            PresetAutoSaveBinding.Attach(root, () => { guardCalls++; return enabled; }, () => commits++);
            CommitPresetField(text); var blocked = RaiseAutoSaveKey(text, Keys.Enter); check.Checked = true; combo.SelectedIndex = 1;
            Equal(0, commits); Check(guardCalls >= 4 && blocked.SuppressKeyPress, "保存ガードを保持しない");
            enabled = true;
            CommitPresetField(text); check.Checked = false; combo.SelectedIndex = 0; Equal(3, commits);
            enabled = false; RaiseAutoSaveKey(text, Keys.Control | Keys.Enter); Equal(3, commits);
        });

        Run("共通の自動保存配線は編集側の保存中ガードに従い再入を抑止する", () =>
        {
            using var root = new Panel(); var check = new CheckBox(); root.Controls.Add(check);
            bool storing = false; int commits = 0;
            PresetAutoSaveBinding.Attach(root, () => !storing, () =>
            {
                storing = true;
                try { commits++; check.Checked = !check.Checked; }
                finally { storing = false; }
            });
            check.Checked = true; Equal(1, commits);
        });

        Run("共通の自動保存配線は保存側の予期しない例外を握りつぶさない", () =>
        {
            using var root = new Panel(); var text = new TextBox(); root.Controls.Add(text);
            PresetAutoSaveBinding.Attach(root, () => true, () => throw new InvalidOperationException("保存側の例外"));
            bool propagated = false;
            try { CommitPresetField(text); }
            catch (TargetInvocationException ex) when (ex.InnerException is InvalidOperationException inner && inner.Message == "保存側の例外") { propagated = true; }
            Check(propagated, "保存側の例外を成功として扱う");
        });

        Run("入出力プリセットのEnter確定は別々に保存し不正な下書きは保持する", () =>
        {
            var writes = new List<AppSettings>();
            using var form = new SettingsForm(new AppSettings(), settings => { writes.Add(settings); return null; }, () => { }, () => []);
            var input = form.Controls.Find("RegionName", true).Single();
            var output = form.Controls.Find("CapturePresetName", true).Single();
            input.Text = "Enterで入力確定"; Equal(0, writes.Count);
            Check(RaiseAutoSaveKey(input, Keys.Enter).SuppressKeyPress, "入力Enterを消費しない");
            Equal(1, writes.Count); Equal(input.Text, writes[^1].SavedRegions.Single(p => p.IsDefault).Name);
            output.Text = "Enterで出力確定";
            Check(RaiseAutoSaveKey(output, Keys.Control | Keys.Enter).SuppressKeyPress, "出力Enterを消費しない");
            Equal(2, writes.Count); Equal(output.Text, writes[^1].CapturePresets.Single(p => p.IsDefault).Name);
            input.Text = ""; RaiseAutoSaveKey(input, Keys.Enter);
            output.Text = ""; RaiseAutoSaveKey(output, Keys.Enter);
            Equal(2, writes.Count); Equal("", input.Text); Equal("", output.Text);
        });
    }

    private static KeyEventArgs RaiseAutoSaveKey(Control control, Keys key)
    {
        var args = new KeyEventArgs(key);
        typeof(Control).GetMethod("OnKeyDown", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(control, [args]);
        return args;
    }
}
