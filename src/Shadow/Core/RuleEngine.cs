using System.Text.RegularExpressions;
using Shadow.Persistence;

namespace Shadow.Core;

/// <summary>规则命中后的处置。</summary>
internal enum RuleAction
{
    /// <summary>不是规则关心的窗口。</summary>
    None,

    /// <summary>自动置顶。</summary>
    Pin,

    /// <summary>永不置顶（拾取器与热键都会拒绝它）。</summary>
    Exclude,
}

/// <summary>一条规则命中的结果（含要应用的外观）。</summary>
internal sealed record RuleMatch(RuleDto Rule, RuleAction Action)
{
    internal int Rank => Math.Max(1, Rule.Rank);

    internal int Opacity => Math.Clamp(Rule.Opacity, WindowAppearance.MinOpacity, WindowAppearance.MaxOpacity);

    internal bool ClickThrough => Rule.ClickThrough;
}

/// <summary>
/// 规则引擎：按「进程名 / 窗口标题」的正则自动置顶或排除某类窗口。
///
/// 两个刻意的设计取舍：
///  1. 规则只是「自动帮你钉一下」，不接管已手工调整过的层级 —— 自动置顶一律追加到最下层
///     （<see cref="PinPosition.Bottom"/>），否则每次弹出一个被规则命中的窗口都会打乱用户的层级。
///  2. 排除规则是硬约束：拾取器、全局热键、CLI 都得先问过它。用户明确说了「别碰这个窗口」，
///     工具就不该在任何入口偷偷把它钉上去。
/// </summary>
internal static class RuleEngine
{
    private static readonly List<(RuleDto Rule, Regex Pattern)> Compiled = new();

    private static bool _dirty = true;

    private static int _compiledRuleCount = -1;

    /// <summary>
    /// 规则的唯一真相来源是配置，这里只在配置变动后重新编译正则。
    /// 用「脏标记 + 条数」两个条件：脏标记覆盖了引擎自己改规则的情况，
    /// 条数覆盖了别处直接改配置列表的情况。Evaluate 会在窗口显示事件里被频繁调用，
    /// 不能每次都比对正则。
    /// </summary>
    private static void EnsureCompiled()
    {
        List<RuleDto> rules = ProfileStore.Current.Rules;

        if (!_dirty && _compiledRuleCount == rules.Count) return;

        Compiled.Clear();

        foreach (RuleDto rule in rules)
        {
            if (!rule.Enabled || string.IsNullOrWhiteSpace(rule.Pattern)) continue;

            try
            {
                Compiled.Add((rule, new Regex(
                    rule.Pattern,
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                    TimeSpan.FromMilliseconds(120))));
            }
            catch (Exception ex)
            {
                // 用户输入的正则可能是写错的，一条坏规则不能拖垮整个引擎
                Log.Warn($"规则「{rule.Name}」的正则无效，已跳过：{ex.Message}");
            }
        }

        _compiledRuleCount = rules.Count;
        _dirty = false;
    }

    internal static void Invalidate()
    {
        _dirty = true;
        Compiled.Clear();
    }

    /// <summary>这条规则是否匹配某个窗口。</summary>
    private static bool Matches(RuleDto rule, Regex pattern, WindowInfo info)
    {
        string subject = rule.Target.Equals("title", StringComparison.OrdinalIgnoreCase)
            ? info.Title
            : info.ProcessName;

        try
        {
            return pattern.IsMatch(subject);
        }
        catch (RegexMatchTimeoutException)
        {
            Log.Warn($"规则「{rule.Name}」匹配超时，已忽略本次匹配");
            return false;
        }
    }

    /// <summary>对某个窗口求值。排除规则优先于置顶规则（安全第一）。</summary>
    internal static RuleMatch? Evaluate(WindowInfo info)
    {
        if (!info.IsValid) return null;

        EnsureCompiled();
        if (Compiled.Count == 0) return null;

        RuleMatch? pin = null;

        foreach ((RuleDto rule, Regex pattern) in Compiled)
        {
            if (!Matches(rule, pattern, info)) continue;

            if (rule.Action.Equals("exclude", StringComparison.OrdinalIgnoreCase))
            {
                return new RuleMatch(rule, RuleAction.Exclude);
            }

            pin ??= new RuleMatch(rule, RuleAction.Pin);
        }

        return pin;
    }

    /// <summary>这个窗口是不是被排除规则挡下了。</summary>
    internal static bool IsExcluded(WindowInfo info) =>
        Evaluate(info) is { Action: RuleAction.Exclude };

    /// <summary>被排除的原因（给用户看的提示）。</summary>
    internal static string DescribeExclusion(WindowInfo info) =>
        Evaluate(info) is { Action: RuleAction.Exclude } match
            ? $"规则「{match.Rule.Name}」排除了这个窗口（{match.Rule.Pattern}）"
            : "该窗口被排除规则挡下";

    /// <summary>
    /// 扫描当前所有可置顶窗口，把命中的置顶规则钉上去。
    /// 返回新钉住的数量。已在内或已被排除的不会被重复处理。
    /// </summary>
    internal static int ApplyPinRules()
    {
        EnsureCompiled();
        if (Compiled.Count == 0) return 0;

        int pinned = 0;

        foreach (WindowInfo info in WindowEnumerator.EnumeratePinnable())
        {
            RuleMatch? match = Evaluate(info);
            if (match is not { Action: RuleAction.Pin }) continue;
            if (PinService.IsTracked(info.Handle)) continue;

            // 规则命中的窗口一律追加到最下层：自动化绝不能打乱用户手工排好的层级
            if (!PinService.TryPin(info.Handle, out _, PinPosition.Bottom)) continue;

            pinned++;

            if (match.Opacity < WindowAppearance.MaxOpacity || match.ClickThrough)
            {
                PinService.SetAppearance(info.Handle, match.Opacity, match.ClickThrough, out _);
            }

            Log.Info($"规则「{match.Rule.Name}」自动置顶：{info.ShortDescription}");
        }

        return pinned;
    }

    /// <summary>新增或覆盖一条规则（按名字去重），随后立即生效一次。</summary>
    internal static void AddOrUpdate(RuleDto rule)
    {
        List<RuleDto> rules = ProfileStore.Current.Rules;
        int index = rules.FindIndex(item => item.Name == rule.Name);

        if (index >= 0) rules[index] = rule;
        else rules.Add(rule);

        Invalidate();
        ProfileStore.SaveCurrent();
    }

    internal static bool Remove(string name)
    {
        bool removed = ProfileStore.Current.Rules.RemoveAll(item => item.Name == name) > 0;
        if (!removed) return false;

        Invalidate();
        ProfileStore.SaveCurrent();
        return true;
    }

    /// <summary>把当前所有置顶窗口里违反排除规则的摘掉（用户刚加了排除规则时调用）。</summary>
    internal static int EnforceExclusions()
    {
        int removed = 0;

        foreach (PinnedWindow item in PinService.Items)
        {
            if (!item.IsAlive) continue;
            if (!IsExcluded(item.Snapshot)) continue;

            PinService.TryUnpin(item.Handle, out _);
            removed++;
        }

        if (removed > 0) Log.Info($"排除规则生效：移出置顶列表 {removed} 个窗口");
        return removed;
    }
}
