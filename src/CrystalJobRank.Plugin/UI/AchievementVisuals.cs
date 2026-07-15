using System.Numerics;
using CrystalJobRank.Core;
using CrystalJobRank.Plugin.Models;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;

namespace CrystalJobRank.Plugin.UI;

internal static class AchievementVisuals
{
    private static readonly IReadOnlyList<int> WinThresholds = AchievementThresholds.WinStreak;
    private static readonly IReadOnlyList<int> DeathlessThresholds = AchievementThresholds.DeathlessStreak;

    public static void DrawHeader(IReadOnlyDictionary<CombatRole, RoleStreakProgress> progress)
    {
        ImGui.TextColored(new Vector4(0.93f, 0.79f, 0.38f, 1f), "ROLE ACHIEVEMENTS");
        ImGui.SameLine();
        ImGui.TextDisabled("Casual + Ranked • highest badge stays unlocked");

        const ImGuiTableFlags flags = ImGuiTableFlags.SizingStretchSame | ImGuiTableFlags.PadOuterX;
        if (!ImGui.BeginTable("##achievement-ribbon", 3, flags)) return;
        ImGui.TableNextRow();
        foreach (var role in new[] { CombatRole.Tank, CombatRole.Dps, CombatRole.Healer })
        {
            ImGui.TableNextColumn();
            progress.TryGetValue(role, out var roleProgress);
            DrawRolePanel(role, roleProgress ?? new RoleStreakProgress(), ImGui.GetContentRegionAvail().X);
        }

        ImGui.EndTable();
    }

    private static void DrawRolePanel(CombatRole role, RoleStreakProgress progress, float availableWidth)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var size = new Vector2(MathF.Max(195f * scale, availableWidth - 3f * scale), 68f * scale);
        var origin = ImGui.GetCursorScreenPos();
        var end = origin + size;
        var draw = ImGui.GetWindowDrawList();
        var roleColor = RoleColor(role);

        draw.AddRectFilled(origin, end, Pack(new Vector4(0.06f, 0.07f, 0.105f, 0.96f)), 8f * scale);
        draw.AddRect(origin, end, Pack(WithAlpha(roleColor, 0.65f)), 8f * scale, ImDrawFlags.None, 1.5f * scale);
        draw.AddRectFilled(origin, new Vector2(origin.X + 4f * scale, end.Y), Pack(roleColor), 8f * scale);

        var roleLabel = role == CombatRole.Healer ? "HEALER" : role.ToString().ToUpperInvariant();
        draw.AddText(origin + new Vector2(12f, 8f) * scale, Pack(roleColor), roleLabel);

        var pillY = origin.Y + 29f * scale;
        var gap = 7f * scale;
        var pillWidth = (size.X - 24f * scale - gap) / 2f;
        DrawPill(
            draw,
            new Vector2(origin.X + 12f * scale, pillY),
            new Vector2(pillWidth, 27f * scale),
            "FLAWLESS",
            HighestUnlocked(progress.BestDeathlessStreak, DeathlessThresholds),
            DeathlessThresholds,
            scale);
        DrawPill(
            draw,
            new Vector2(origin.X + 12f * scale + pillWidth + gap, pillY),
            new Vector2(pillWidth, 27f * scale),
            "WIN STREAK",
            HighestUnlocked(progress.BestWinStreak, WinThresholds),
            WinThresholds,
            scale);

        ImGui.Dummy(size);
        if (ImGui.IsItemHovered())
        {
            var nextDeathless = NextThreshold(progress.BestDeathlessStreak, DeathlessThresholds);
            var nextWins = NextThreshold(progress.BestWinStreak, WinThresholds);
            ImGui.SetTooltip(
                $"{roleLabel} achievements\n" +
                $"Flawless: current {progress.CurrentDeathlessStreak}, best {progress.BestDeathlessStreak}, next {NextLabel(nextDeathless)}\n" +
                $"Win streak: current {progress.CurrentWinStreak}, best {progress.BestWinStreak}, next {NextLabel(nextWins)}\n\n" +
                "A match on another role does not break this role's streak.\n" +
                "Custom and Unknown queues neither count nor interrupt it.");
        }
    }

    private static void DrawPill(
        ImDrawListPtr draw,
        Vector2 origin,
        Vector2 size,
        string label,
        int unlocked,
        IReadOnlyList<int> thresholds,
        float scale)
    {
        var unlockedIndex = unlocked == 0 ? -1 : thresholds.IndexOf(unlocked);
        var metal = unlockedIndex < 0 ? new Vector4(0.31f, 0.33f, 0.4f, 1f) : BadgeMetal(unlockedIndex, thresholds.Count);
        var end = origin + size;
        draw.AddRectFilled(origin, end, Pack(WithAlpha(metal, unlockedIndex < 0 ? 0.12f : 0.2f)), 13f * scale);
        draw.AddRect(origin, end, Pack(WithAlpha(metal, unlockedIndex < 0 ? 0.36f : 0.92f)), 13f * scale, ImDrawFlags.None, 1.3f * scale);

        var gemCenter = origin + new Vector2(12f, size.Y / (2f * scale)) * scale;
        draw.AddCircleFilled(gemCenter, 6f * scale, Pack(WithAlpha(metal, unlockedIndex < 0 ? 0.34f : 0.95f)), 18);
        if (unlockedIndex >= 2)
        {
            for (var i = 0; i < Math.Min(6, unlockedIndex + 2); i++)
            {
                var angle = MathF.PI * 2f * i / Math.Min(6, unlockedIndex + 2);
                var direction = new Vector2(MathF.Cos(angle), MathF.Sin(angle));
                draw.AddLine(gemCenter + direction * 7f * scale, gemCenter + direction * 10f * scale, Pack(metal), 1.2f * scale);
            }
        }

        var text = unlocked == 0 ? $"{label}  —" : $"{label}  {unlocked}";
        draw.AddText(origin + new Vector2(23f, 6f) * scale, Pack(unlocked == 0
            ? new Vector4(0.57f, 0.59f, 0.66f, 1f)
            : new Vector4(0.95f, 0.96f, 1f, 1f)), text);
    }

    private static int HighestUnlocked(int best, IReadOnlyList<int> thresholds)
    {
        var result = 0;
        foreach (var threshold in thresholds)
        {
            if (best < threshold) break;
            result = threshold;
        }

        return result;
    }

    private static int? NextThreshold(int best, IReadOnlyList<int> thresholds)
    {
        foreach (var threshold in thresholds)
        {
            if (best < threshold) return threshold;
        }

        return null;
    }

    private static string NextLabel(int? value) => value?.ToString() ?? "complete";

    private static Vector4 BadgeMetal(int index, int count)
    {
        if (index == count - 1) return new Vector4(0.82f, 0.46f, 1f, 1f);
        return index switch
        {
            0 => new Vector4(0.80f, 0.45f, 0.20f, 1f),
            1 => new Vector4(0.78f, 0.83f, 0.9f, 1f),
            2 => new Vector4(0.96f, 0.74f, 0.25f, 1f),
            _ => new Vector4(0.38f, 0.72f, 1f, 1f),
        };
    }

    private static Vector4 RoleColor(CombatRole role) => role switch
    {
        CombatRole.Tank => new Vector4(0.34f, 0.53f, 1f, 1f),
        CombatRole.Healer => new Vector4(0.38f, 0.85f, 0.5f, 1f),
        CombatRole.Dps => new Vector4(1f, 0.38f, 0.44f, 1f),
        _ => new Vector4(0.65f, 0.67f, 0.74f, 1f),
    };

    private static Vector4 WithAlpha(Vector4 color, float alpha) => new(color.X, color.Y, color.Z, alpha);

    private static uint Pack(Vector4 color)
    {
        var r = (uint)(Math.Clamp(color.X, 0f, 1f) * 255f + 0.5f);
        var g = (uint)(Math.Clamp(color.Y, 0f, 1f) * 255f + 0.5f);
        var b = (uint)(Math.Clamp(color.Z, 0f, 1f) * 255f + 0.5f);
        var a = (uint)(Math.Clamp(color.W, 0f, 1f) * 255f + 0.5f);
        return r | (g << 8) | (b << 16) | (a << 24);
    }
}

internal static class ThresholdListExtensions
{
    public static int IndexOf(this IReadOnlyList<int> values, int target)
    {
        for (var index = 0; index < values.Count; index++)
        {
            if (values[index] == target) return index;
        }

        return -1;
    }
}
