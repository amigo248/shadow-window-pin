using Shadow.Persistence;

namespace Shadow.Core;

/// <summary>
/// 场景分组：把「一批窗口 + 它们的层级与外观」存成一个名字，一键切换。
///
/// 典型用法：上班点「写代码」（IDE + 文档 + 终端），摸鱼点「看视频」。
/// 切换是**破坏性**的：会先取消当前所有置顶再按场景重新钉 ——
/// 这正是「一键切换」的意义所在，否则两套窗口叠在一起就没有切换可言了。
/// </summary>
internal static class SceneManager
{
    internal static IReadOnlyList<SceneDto> All => ProfileStore.Current.Scenes;

    internal static SceneDto? Find(string name) =>
        ProfileStore.Current.Scenes.FirstOrDefault(
            scene => string.Equals(scene.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>把当前置顶的窗口（含层级顺序与外观）存成一个场景，重名则覆盖。</summary>
    internal static SceneDto Capture(string name)
    {
        var scene = new SceneDto { Name = name };

        foreach (PinnedWindow item in PinService.Items)
        {
            scene.Windows.Add(new PinnedEntryDto
            {
                ProcessName = item.Snapshot.ProcessName,
                ClassName = item.Snapshot.ClassName,
                Title = item.Snapshot.Title,
                Opacity = item.Opacity,
                ClickThrough = item.ClickThrough,
                Geometry = WindowRecovery.CaptureGeometry(item.Handle),
            });
        }

        List<SceneDto> scenes = ProfileStore.Current.Scenes;
        int index = scenes.FindIndex(
            existing => string.Equals(existing.Name, name, StringComparison.OrdinalIgnoreCase));

        if (index >= 0) scenes[index] = scene;
        else scenes.Add(scene);

        ProfileStore.SaveCurrent();
        Log.Info($"已保存场景「{name}」：{scene.Windows.Count} 个窗口");
        return scene;
    }

    internal static bool Delete(string name)
    {
        bool removed = ProfileStore.Current.Scenes.RemoveAll(
            scene => string.Equals(scene.Name, name, StringComparison.OrdinalIgnoreCase)) > 0;

        if (removed) ProfileStore.SaveCurrent();
        return removed;
    }

    /// <summary>切换到某个场景，返回成功钉住的窗口数与找不到的窗口名。</summary>
    internal static int Apply(string name, out List<string> missing)
    {
        missing = new List<string>();

        SceneDto? scene = Find(name);
        if (scene is null)
        {
            missing.Add($"没有名为「{name}」的场景");
            return 0;
        }

        // 先全部取消：切换场景时把上一套窗口留在 topmost 里，用户看到的就是两堆窗口叠着
        int released = PinService.UnpinAll();
        Log.Info($"切换场景「{name}」：先取消 {released} 个窗口，再恢复 {scene.Windows.Count} 个");

        return WindowRecovery.RePin(scene.Windows, out missing);
    }

    /// <summary>场景里的窗口名，给界面用。</summary>
    internal static string Describe(SceneDto scene)
    {
        if (scene.Windows.Count == 0) return $"{scene.Name}（空）";

        IEnumerable<string> names = scene.Windows
            .Take(4)
            .Select(WindowRecovery.Describe);

        string joined = string.Join("、", names);
        return scene.Windows.Count > 4
            ? $"{scene.Name}（{joined} 等 {scene.Windows.Count} 个）"
            : $"{scene.Name}（{joined}）";
    }
}
