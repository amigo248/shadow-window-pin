using System.Text.RegularExpressions;
using Shadow.Core;
using Shadow.Persistence;

namespace Shadow.App;

/// <summary>
/// 规则编辑器：按「进程名 / 窗口标题」的正则自动置顶或排除某类窗口。
///
/// 界面取向是「能当场验证」：编辑区下方永远显示这条正则当前命中几个窗口，
/// 因为正则写错（少个转义、写成 *.exe 这种通配符）在纯列表界面里是看不出来的，
/// 用户只会觉得「规则不生效」。测试按钮用的就是引擎同一套匹配代码。
/// </summary>
internal sealed class RuleForm : Form
{
    private readonly ListView _list = new();
    private readonly TextBox _name = new();
    private readonly TextBox _pattern = new();
    private readonly ComboBox _target = new();
    private readonly ComboBox _action = new();
    private readonly NumericUpDown _rank = new();
    private readonly NumericUpDown _opacity = new();
    private readonly CheckBox _clickThrough = new();
    private readonly CheckBox _enabled = new();
    private readonly Label _status = new();
    private readonly Label _hint = new();

    /// <summary>正在编辑的规则原名（用于重命名时删掉旧条目）；null 表示在编辑一条新规则。</summary>
    private string? _editingOriginalName;

    private bool _loading;

    internal RuleForm()
    {
        Text = "Shadow · 自动规则";
        Icon = SystemIcons.Application;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        ClientSize = new Size(760, 470);
        MinimumSize = new Size(700, 440);
        ShowInTaskbar = false;

        BuildList();
        BuildEditorHost();

        Controls.Add(_list);
        Controls.Add(BuildEditorHost());
        Controls.Add(BuildStatusBar());

        Load += (_, _) => Reload();
        Shown += (_, _) => _pattern.Focus();
    }

    // ------------------------------------------------------------ 上半部分：规则列表

    private void BuildList()
    {
        _list.Dock = DockStyle.Fill;
        _list.View = View.Details;
        _list.FullRowSelect = true;
        _list.MultiSelect = false;
        _list.HideSelection = false;
        _list.GridLines = true;
        _list.Columns.Add("启用", 46, HorizontalAlignment.Center);
        _list.Columns.Add("名称", 130, HorizontalAlignment.Left);
        _list.Columns.Add("匹配对象", 80, HorizontalAlignment.Left);
        _list.Columns.Add("正则", 220, HorizontalAlignment.Left);
        _list.Columns.Add("动作", 80, HorizontalAlignment.Left);
        _list.Columns.Add("层级", 46, HorizontalAlignment.Center);
        _list.Columns.Add("透明度", 60, HorizontalAlignment.Center);
        _list.SelectedIndexChanged += (_, _) => LoadSelectionIntoEditor();
    }

    // ------------------------------------------------------------ 下半部分：编辑区

    private Control BuildEditorHost()
    {
        var group = new GroupBox
        {
            Text = "规则内容",
            Dock = DockStyle.Bottom,
            Height = 196,
            Padding = new Padding(8, 6, 8, 6),
        };

        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 4,
            RowCount = 3,
        };

        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 70));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 70));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));

        _name.Dock = DockStyle.Fill;
        _name.PlaceholderText = "例如：代码编辑器常驻";

        _target.DropDownStyle = ComboBoxStyle.DropDownList;
        _target.Items.AddRange(new object[] { "进程名", "窗口标题" });
        _target.SelectedIndex = 0;

        _pattern.Dock = DockStyle.Fill;
        _pattern.PlaceholderText = @"正则，例如 ^Code\.exe$ 或 - Visual Studio Code";

        _action.DropDownStyle = ComboBoxStyle.DropDownList;
        _action.Items.AddRange(new object[] { "自动置顶", "排除（永不置顶）" });
        _action.SelectedIndex = 0;

        _rank.Minimum = 1;
        _rank.Maximum = HotkeyWindow.RankCount;
        _rank.Value = 1;

        _opacity.Minimum = WindowAppearance.MinOpacity;
        _opacity.Maximum = WindowAppearance.MaxOpacity;
        _opacity.Value = WindowAppearance.MaxOpacity;

        _clickThrough.Text = "点击穿透";
        _clickThrough.AutoSize = true;
        _enabled.Text = "启用";
        _enabled.AutoSize = true;
        _enabled.Checked = true;

        grid.Controls.Add(NewLabel("规则名称"), 0, 0);
        grid.Controls.Add(_name, 1, 0);
        grid.Controls.Add(NewLabel("匹配对象"), 2, 0);
        grid.Controls.Add(_target, 3, 0);

        grid.Controls.Add(NewLabel("正则"), 0, 1);
        grid.Controls.Add(_pattern, 1, 1);
        grid.SetColumnSpan(_pattern, 3);

        var numbers = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Dock = DockStyle.Fill };
        numbers.Controls.Add(NewLabel("层级"));
        numbers.Controls.Add(_rank);
        numbers.Controls.Add(NewLabel("透明度 %"));
        numbers.Controls.Add(_opacity);
        numbers.Controls.Add(_clickThrough);
        numbers.Controls.Add(_enabled);

        grid.Controls.Add(numbers, 1, 2);
        grid.SetColumnSpan(numbers, 3);

        var commands = new FlowLayoutPanel
        {
            AutoSize = true,
            WrapContents = false,
            Dock = DockStyle.Bottom,
            Padding = new Padding(0, 4, 0, 0),
        };

        var test = new Button { Text = "测试匹配", AutoSize = true };
        test.Click += (_, _) => TestPattern();

        var save = new Button { Text = "保存规则", AutoSize = true };
        save.Click += (_, _) => SaveRule();

        var create = new Button { Text = "新建规则", AutoSize = true };
        create.Click += (_, _) => StartNewRule();

        var delete = new Button { Text = "删除选中", AutoSize = true };
        delete.Click += (_, _) => DeleteSelected();

        var scan = new Button { Text = "立即扫描并应用", AutoSize = true };
        scan.Click += (_, _) => ScanNow();

        commands.Controls.AddRange(new Control[] { save, create, delete, test, scan });

        _hint.Text = "提示：正则区分大小写？不区分。排除规则优先于置顶规则，且对所有入口生效。";
        _hint.AutoSize = true;
        _hint.ForeColor = SystemColors.GrayText;
        _hint.Padding = new Padding(0, 6, 0, 0);

        var host = new Panel { Dock = DockStyle.Fill };
        host.Controls.Add(grid);
        host.Controls.Add(commands);
        host.Controls.Add(_hint);

        group.Controls.Add(host);
        return group;
    }

    private Control BuildStatusBar()
    {
        _status.Dock = DockStyle.Bottom;
        _status.Height = 26;
        _status.TextAlign = ContentAlignment.MiddleLeft;
        _status.ForeColor = SystemColors.GrayText;
        return _status;
    }

    private static Label NewLabel(string text) =>
        new() { Text = text, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 7, 6, 0) };

    // ------------------------------------------------------------ 载入 / 保存

    private void Reload()
    {
        _loading = true;
        try
        {
            _list.BeginUpdate();
            _list.Items.Clear();

            foreach (RuleDto rule in ProfileStore.Current.Rules)
            {
                var row = new ListViewItem(rule.Enabled ? "✓" : string.Empty) { Tag = rule };
                row.SubItems.Add(rule.Name);
                row.SubItems.Add(rule.Target.Equals("title", StringComparison.OrdinalIgnoreCase) ? "窗口标题" : "进程名");
                row.SubItems.Add(rule.Pattern);
                row.SubItems.Add(rule.Action.Equals("exclude", StringComparison.OrdinalIgnoreCase) ? "排除" : "置顶");
                row.SubItems.Add(rule.Rank.ToString());
                row.SubItems.Add($"{rule.Opacity}%{(rule.ClickThrough ? " 穿透" : string.Empty)}");
                _list.Items.Add(row);
            }
        }
        finally
        {
            _list.EndUpdate();
            _loading = false;
        }

        _status.Text = ProfileStore.Current.Rules.Count == 0
            ? "还没有规则。规则会在窗口显示出来时自动接管，启动时也会整体扫描一次。"
            : $"共 {ProfileStore.Current.Rules.Count} 条规则。";

        if (_list.Items.Count > 0 && _list.SelectedItems.Count == 0) _list.Items[0].Selected = true;
    }

    private void LoadSelectionIntoEditor()
    {
        if (_loading || _list.SelectedItems.Count == 0) return;
        if (_list.SelectedItems[0].Tag is not RuleDto rule) return;

        _loading = true;
        try
        {
            _editingOriginalName = rule.Name;
            _name.Text = rule.Name;
            _pattern.Text = rule.Pattern;
            _target.SelectedIndex = rule.Target.Equals("title", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
            _action.SelectedIndex = rule.Action.Equals("exclude", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
            _rank.Value = Math.Clamp(rule.Rank, (int)_rank.Minimum, (int)_rank.Maximum);
            _opacity.Value = Math.Clamp(rule.Opacity, (int)_opacity.Minimum, (int)_opacity.Maximum);
            _clickThrough.Checked = rule.ClickThrough;
            _enabled.Checked = rule.Enabled;
        }
        finally
        {
            _loading = false;
        }

        _status.Text = $"正在编辑「{rule.Name}」。";
    }

    private void StartNewRule()
    {
        _loading = true;
        try
        {
            _editingOriginalName = null;
            _name.Text = string.Empty;
            _pattern.Text = string.Empty;
            _target.SelectedIndex = 0;
            _action.SelectedIndex = 0;
            _rank.Value = 1;
            _opacity.Value = WindowAppearance.MaxOpacity;
            _clickThrough.Checked = false;
            _enabled.Checked = true;
            _list.SelectedItems.Clear();
        }
        finally
        {
            _loading = false;
        }

        _status.Text = "正在新建规则：填好名称与正则后点「保存规则」。";
        _name.Focus();
    }

    private void SaveRule()
    {
        string name = _name.Text.Trim();
        string pattern = _pattern.Text.Trim();

        if (name.Length == 0)
        {
            Warn("请先给规则起个名字。");
            return;
        }

        if (pattern.Length == 0)
        {
            Warn("请填写正则表达式。");
            return;
        }

        try
        {
            _ = new Regex(pattern, RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(120));
        }
        catch (Exception ex)
        {
            // 存下去也会被引擎跳过，不如当场拒绝并说清哪里错了
            Warn($"正则无效：{ex.Message}");
            return;
        }

        var rule = new RuleDto
        {
            Name = name,
            Pattern = pattern,
            Target = _target.SelectedIndex == 1 ? "title" : "process",
            Action = _action.SelectedIndex == 1 ? "exclude" : "pin",
            Rank = (int)_rank.Value,
            Opacity = (int)_opacity.Value,
            ClickThrough = _clickThrough.Checked,
            Enabled = _enabled.Checked,
        };

        // 重命名 = 删掉旧名字再加新名字，否则配置里会同时留下两条
        if (_editingOriginalName is { } original && !string.Equals(original, name, StringComparison.Ordinal))
        {
            RuleEngine.Remove(original);
        }

        RuleEngine.AddOrUpdate(rule);
        _editingOriginalName = name;

        // 排除规则要立刻生效：用户刚说「别碰这个窗口」，就不能等下次启动
        if (rule.Action.Equals("exclude", StringComparison.OrdinalIgnoreCase))
        {
            int removed = RuleEngine.EnforceExclusions();
            if (removed > 0) _status.Text = $"已保存，并从置顶列表移出了 {removed} 个被排除的窗口。";
        }
        else
        {
            int pinned = RuleEngine.ApplyPinRules();
            if (pinned > 0) _status.Text = $"已保存，并自动置顶了 {pinned} 个匹配的窗口。";
        }

        Reload();
    }

    private void DeleteSelected()
    {
        if (_list.SelectedItems.Count == 0 || _list.SelectedItems[0].Tag is not RuleDto rule) return;

        if (Dialogs.Message(this, $"删除规则「{rule.Name}」？", buttons: MessageBoxButtons.OKCancel,
                icon: MessageBoxIcon.Question) != DialogResult.OK)
        {
            return;
        }

        RuleEngine.Remove(rule.Name);
        StartNewRule();
        Reload();
        _status.Text = $"已删除规则「{rule.Name}」。";
    }

    private void TestPattern()
    {
        string pattern = _pattern.Text.Trim();
        if (pattern.Length == 0)
        {
            Warn("请先填写正则表达式。");
            return;
        }

        Regex regex;
        try
        {
            regex = new Regex(pattern, RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(120));
        }
        catch (Exception ex)
        {
            Warn($"正则无效：{ex.Message}");
            return;
        }

        bool byTitle = _target.SelectedIndex == 1;
        var hits = new List<string>();

        foreach (WindowInfo info in WindowEnumerator.EnumeratePinnable())
        {
            string subject = byTitle ? info.Title : info.ProcessName;

            try
            {
                if (regex.IsMatch(subject)) hits.Add(info.ShortDescription);
            }
            catch (RegexMatchTimeoutException)
            {
                Warn("匹配超时：正则写得太复杂了，请简化。");
                return;
            }
        }

        _status.Text = hits.Count == 0
            ? $"当前{(byTitle ? "标题" : "进程名")}里没有窗口匹配这个正则。"
            : $"匹配到 {hits.Count} 个窗口：" + string.Join("、", hits.Take(3)) +
              (hits.Count > 3 ? "…" : string.Empty);
    }

    private void ScanNow()
    {
        int pinned = RuleEngine.ApplyPinRules();
        int removed = RuleEngine.EnforceExclusions();
        _status.Text = $"扫描完成：自动置顶 {pinned} 个，按排除规则移出 {removed} 个。";
    }

    private void Warn(string message) =>
        Dialogs.Message(this, message, icon: MessageBoxIcon.Warning);
}
