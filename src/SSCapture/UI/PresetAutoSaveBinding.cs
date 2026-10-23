namespace SSCapture.UI;

internal static class PresetAutoSaveBinding
{
    // 確定イベントの配線だけを共通化。検証・保存・編集中の抑止は編集画面が担う。
    internal static void Attach(Control root, Func<bool> canCommit, Action commit)
    {
        void Commit(object? sender, EventArgs e)
        {
            if (canCommit()) commit();
        }

        void CommitOnEnter(object? sender, KeyEventArgs e)
        {
            if (e.KeyCode != Keys.Enter) return;
            Commit(sender, EventArgs.Empty);
            e.Handled = e.SuppressKeyPress = true;
        }

        void AttachChildren(Control parent)
        {
            foreach (Control control in parent.Controls)
            {
                if (control is TextBox or NumericUpDown)
                {
                    control.Validated += Commit;
                    control.KeyDown += CommitOnEnter;
                }
                else if (control is CheckBox check)
                    check.CheckedChanged += Commit;
                else if (control is ComboBox combo)
                    combo.SelectedIndexChanged += Commit;

                AttachChildren(control);
            }
        }

        AttachChildren(root);
    }
}
