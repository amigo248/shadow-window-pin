namespace Shadow.App;

/// <summary>
/// 全局热键的候选表与注册策略。
///
/// 为什么需要候选表：RegisterHotKey 占用的是「全局热键命名空间」，先到先得，
/// 被别的程序占走时返回 ERROR_HOTKEY_ALREADY_REGISTERED(1409)。
/// 如果只注册一个组合就报错，用户拿到的是一个「快捷键没反应」的工具 ——
/// 所以按优先级依次尝试，用第一个能注册上的，并把实际生效的组合显示出来。
/// </summary>
internal static class HotkeyDefaults
{
    private static readonly (HotkeyModifiers Modifiers, Keys Key)[] ToggleCandidates =
    {
        (HotkeyModifiers.Control | HotkeyModifiers.Alt, Keys.T),
        (HotkeyModifiers.Control | HotkeyModifiers.Alt, Keys.P),
        (HotkeyModifiers.Control | HotkeyModifiers.Alt, Keys.W),
        (HotkeyModifiers.Control | HotkeyModifiers.Alt, Keys.F9),
    };

    private static readonly (HotkeyModifiers Modifiers, Keys Key)[] UnpinAllCandidates =
    {
        (HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Shift, Keys.T),
        (HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Shift, Keys.P),
        (HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Shift, Keys.F9),
    };

    /// <summary>候选组合的文字形式（设置界面里用来告诉用户「备用会退到哪些组合」）。</summary>
    internal static IReadOnlyList<string> ToggleCandidateTexts =>
        ToggleCandidates.Select(item => HotkeyWindow.Describe(item.Modifiers, item.Key)).ToArray();

    internal static IReadOnlyList<string> UnpinAllCandidateTexts =>
        UnpinAllCandidates.Select(item => HotkeyWindow.Describe(item.Modifiers, item.Key)).ToArray();

    /// <summary>首个候选组合的文字描述，用于失败时提示「我们试过什么」。</summary>
    internal static string PreferredToggleText =>
        HotkeyWindow.Describe(ToggleCandidates[0].Modifiers, ToggleCandidates[0].Key);

    internal static string PreferredUnpinAllText =>
        HotkeyWindow.Describe(UnpinAllCandidates[0].Modifiers, UnpinAllCandidates[0].Key);

    /// <summary>
    /// 把「Ctrl+Alt+P」这样的文字解析回修饰键与按键。
    /// 解析失败（用户手改配置文件写错了）时返回 false，调用方会退回默认候选表 ——
    /// 配置写坏绝不能导致热键彻底失灵。
    /// </summary>
    internal static bool TryParse(string? text, out HotkeyModifiers modifiers, out Keys key)
    {
        modifiers = HotkeyModifiers.None;
        key = Keys.None;

        if (string.IsNullOrWhiteSpace(text)) return false;

        string[] parts = text.Split('+', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Trim())
            .ToArray();

        if (parts.Length < 2) return false;

        for (int i = 0; i < parts.Length - 1; i++)
        {
            switch (parts[i].ToLowerInvariant())
            {
                case "ctrl":
                case "control": modifiers |= HotkeyModifiers.Control; break;
                case "alt": modifiers |= HotkeyModifiers.Alt; break;
                case "shift": modifiers |= HotkeyModifiers.Shift; break;
                case "win":
                case "windows": modifiers |= HotkeyModifiers.Win; break;
                default: return false;
            }
        }

        if (modifiers == HotkeyModifiers.None) return false;

        string last = parts[^1];

        // 单字符（字母/数字）走 Keys 枚举的名字；其余交给 Enum.TryParse
        if (last.Length == 1)
        {
            char c = char.ToUpperInvariant(last[0]);
            if (c is >= 'A' and <= 'Z') { key = Keys.A + (c - 'A'); return true; }
            if (c is >= '0' and <= '9') { key = Keys.D0 + (c - '0'); return true; }
            return false;
        }

        if (last.StartsWith('F') &&
            int.TryParse(last[1..], out int functionKey) &&
            functionKey is >= 1 and <= 24)
        {
            key = Keys.F1 + (functionKey - 1);
            return true;
        }

        return Enum.TryParse(last, ignoreCase: true, out key) && key != Keys.None;
    }

    /// <summary>
    /// 按候选顺序注册，返回是否成功；出参为实际生效的组合与尝试次数。
    /// <paramref name="preferred"/> 是用户自定义的组合（配置里的 Override），
    /// 解析成功时排在候选表最前面 —— 用户明确指定了就用它，被占用才退回候选表。
    /// </summary>
    internal static bool TryRegister(
        HotkeyWindow window,
        int id,
        bool togglePin,
        out string description,
        out int lastError,
        out int attempts,
        string? preferred = null)
    {
        List<(HotkeyModifiers Modifiers, Keys Key)> candidates =
            new(togglePin ? ToggleCandidates : UnpinAllCandidates);

        if (TryParse(preferred, out HotkeyModifiers customModifiers, out Keys customKey))
        {
            candidates.Insert(0, (customModifiers, customKey));
        }

        description = string.Empty;
        lastError = 0;
        attempts = 0;

        foreach ((HotkeyModifiers modifiers, Keys key) in candidates)
        {
            attempts++;

            if (window.Register(id, modifiers, key, out int error, out string text))
            {
                description = text;
                return true;
            }

            lastError = error;
            description = text;
        }

        return false;
    }

    /// <summary>
    /// 层级热键 Ctrl+Alt+1…9（把鼠标下的窗口设为第 N 层）。
    /// 数字键也常被截图工具、输入法之类抢走，所以退一步再试 Ctrl+Shift+数字。
    /// </summary>
    internal static bool TryRegisterRank(HotkeyWindow window, int rank, out string description)
    {
        // Keys.D1 == Keys.D0 + 1，所以 rank 1…9 直接偏移即可
        Keys digit = Keys.D0 + rank;

        (HotkeyModifiers Modifiers, Keys Key)[] candidates =
        {
            (HotkeyModifiers.Control | HotkeyModifiers.Alt, digit),
            (HotkeyModifiers.Control | HotkeyModifiers.Shift, digit),
        };

        description = string.Empty;

        foreach ((HotkeyModifiers modifiers, Keys key) in candidates)
        {
            if (window.Register(
                    HotkeyWindow.RankId(rank), modifiers, key, out _, out string text))
            {
                description = text;
                return true;
            }
        }

        return false;
    }
}
