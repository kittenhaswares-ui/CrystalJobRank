using System.Numerics;
using CrystalJobRank.Core;
using CrystalJobRank.Plugin.Models;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Plugin.Services;

namespace CrystalJobRank.Plugin.UI;

internal enum RankTier
{
    Bronze,
    Silver,
    Gold,
    Platinum,
    Diamond,
    Crystal,
}

internal readonly record struct RankBand(
    RankTier Tier,
    string Name,
    int MinimumRating,
    int NextRating,
    string NextName,
    Vector4 MetalColor,
    int OrnamentLevel);

internal static class RankVisuals
{
    private static readonly Vector4 CardBackground = new(0.05f, 0.06f, 0.095f, 0.98f);
    private static readonly Vector4 CardBorder = new(0.28f, 0.31f, 0.42f, 0.72f);
    private static readonly Vector4 PrimaryText = new(0.94f, 0.95f, 1f, 1f);
    private static readonly Vector4 MutedText = new(0.64f, 0.67f, 0.76f, 1f);

    public static RankBand GetBand(int rating) => rating switch
    {
        < 1600 => new(RankTier.Bronze, "Bronze", 1500, 1600, "Silver", Rgb(0xCD7F32), 0),
        < 1700 => new(RankTier.Silver, "Silver", 1600, 1700, "Gold", Rgb(0xC8D0DB), 1),
        < 1800 => new(RankTier.Gold, "Gold", 1700, 1800, "Platinum", Rgb(0xF5C451), 2),
        < 1900 => new(RankTier.Platinum, "Platinum", 1800, 1900, "Diamond", Rgb(0x70D6C7), 3),
        < 2000 => new(RankTier.Diamond, "Diamond", 1900, 2000, "Crystal", Rgb(0x65B8FF), 4),
        _ => new(RankTier.Crystal, "Crystal", 2000, RatingEngine.MaximumRating, "Cap", Rgb(0xD184FF), 5),
    };

    public static Vector4 JobColor(CombatJob job) => job switch
    {
        CombatJob.PLD => Rgb(0xA8D2E6),
        CombatJob.WAR => Rgb(0xCF2621),
        CombatJob.DRK => Rgb(0xD126CC),
        CombatJob.GNB => Rgb(0x796D30),
        CombatJob.WHM => Rgb(0xFFF0DC),
        CombatJob.SCH => Rgb(0x8657FF),
        CombatJob.AST => Rgb(0xFFE74A),
        CombatJob.SGE => Rgb(0x80A0F0),
        CombatJob.MNK => Rgb(0xD69C00),
        CombatJob.DRG => Rgb(0x4164CD),
        CombatJob.NIN => Rgb(0xAF1964),
        CombatJob.SAM => Rgb(0xE46D04),
        CombatJob.RPR => Rgb(0x965A90),
        CombatJob.VPR => Rgb(0x108210),
        CombatJob.BRD => Rgb(0x91BA5E),
        CombatJob.MCH => Rgb(0x6EE1D6),
        CombatJob.DNC => Rgb(0xE2B0AF),
        CombatJob.BLM => Rgb(0xA579D6),
        CombatJob.SMN => Rgb(0x2D9B78),
        CombatJob.RDM => Rgb(0xE87B7B),
        CombatJob.PCT => Rgb(0xFC92E1),
        _ => Rgb(0x9AA2B5),
    };

    public static void DrawRatingCard(
        RatingState rating,
        JobLifetimeStats lifetime,
        ITextureProvider textureProvider,
        float availableWidth)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var width = MathF.Max(326f * scale, availableWidth - 4f * scale);
        var height = 207f * scale;
        var origin = ImGui.GetCursorScreenPos();
        var end = origin + new Vector2(width, height);
        var draw = ImGui.GetWindowDrawList();
        var jobColor = JobColor(rating.Job);
        var band = GetBand(rating.Rating);

        draw.AddRectFilled(origin, end, Pack(CardBackground), 10f * scale);
        draw.AddRectFilled(origin, new Vector2(origin.X + 5f * scale, end.Y), Pack(jobColor), 10f * scale);
        draw.AddRect(origin, end, Pack(CardBorder), 10f * scale);

        var badgeCenter = origin + new Vector2(48f, 60f) * scale;
        JobIconVisuals.Draw(textureProvider, draw, badgeCenter, 34f * scale, rating.Job, band);

        var role = CombatJobs.RoleOf(rating.Job);
        var roleText = role == CombatRole.Healer ? "HEALER" : role.ToString().ToUpperInvariant();
        var roleSize = ImGui.CalcTextSize(roleText);
        draw.AddText(
            new Vector2(badgeCenter.X - roleSize.X / 2f, origin.Y + 101f * scale),
            Pack(WithAlpha(jobColor, 0.86f)),
            roleText);

        var contentX = origin.X + 96f * scale;
        var titleY = origin.Y + 12f * scale;
        draw.AddText(new Vector2(contentX, titleY), Pack(jobColor), rating.Job.ToString());
        draw.AddText(new Vector2(contentX + 39f * scale, titleY), Pack(MutedText), JobName(rating.Job));

        draw.AddText(new Vector2(contentX, origin.Y + 36f * scale), Pack(PrimaryText), $"{rating.Rating:N0}");
        draw.AddText(new Vector2(contentX + 55f * scale, origin.Y + 36f * scale), Pack(band.MetalColor), band.Name.ToUpperInvariant());

        var barStart = new Vector2(contentX, origin.Y + 64f * scale);
        var barEnd = new Vector2(end.X - 14f * scale, barStart.Y + 17f * scale);
        DrawProgressBar(draw, barStart, barEnd, rating.Rating, band, jobColor, scale);

        var progressLabel = band.Tier == RankTier.Crystal
            ? $"{rating.Rating:N0} / {RatingEngine.MaximumRating:N0}"
            : $"{Math.Max(0, rating.Rating - band.MinimumRating):N0} / {band.NextRating - band.MinimumRating:N0} to {band.NextName}";
        draw.AddText(new Vector2(contentX, origin.Y + 88f * scale), Pack(MutedText), progressLabel);

        var provisional = rating.Matches < RatingEngine.ProvisionalMatches
            ? $"  •  provisional {rating.Matches}/{RatingEngine.ProvisionalMatches}"
            : string.Empty;
        draw.AddText(
            new Vector2(contentX, origin.Y + 111f * scale),
            Pack(PrimaryText),
            $"{rating.Wins}W  {rating.Losses}L  •  {rating.WinRate:P0}{provisional}");

        var dividerY = origin.Y + 139f * scale;
        draw.AddLine(
            new Vector2(origin.X + 13f * scale, dividerY),
            new Vector2(end.X - 13f * scale, dividerY),
            Pack(WithAlpha(CardBorder, 0.68f)),
            scale);

        var peakBand = GetBand(lifetime.HighestRating);
        draw.AddText(
            new Vector2(origin.X + 14f * scale, origin.Y + 149f * scale),
            Pack(MutedText),
            "ALL-TIME PEAK");
        draw.AddText(
            new Vector2(origin.X + 116f * scale, origin.Y + 149f * scale),
            Pack(peakBand.MetalColor),
            $"{lifetime.HighestRating:N0}  {peakBand.Name.ToUpperInvariant()}");

        draw.AddText(
            new Vector2(origin.X + 14f * scale, origin.Y + 171f * scale),
            Pack(PrimaryText),
            $"KOs {lifetime.HighestKills:N0}  •  Dealt {Compact(lifetime.HighestDamageDealt)}");
        draw.AddText(
            new Vector2(origin.X + 14f * scale, origin.Y + 190f * scale),
            Pack(PrimaryText),
            $"Taken {Compact(lifetime.HighestDamageTaken)}  •  Healing {Compact(lifetime.HighestHealing)}");

        ImGui.Dummy(new Vector2(width, height));
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                $"{rating.Job} {band.Name}\n" +
                $"Current rating: {rating.Rating:N0}\n" +
                $"All-time highest rating: {lifetime.HighestRating:N0} ({peakBand.Name})\n" +
                $"{rating.Matches} Casual + Ranked matches • {rating.WinRate:P1} observed win rate\n" +
                $"Highest KOs: {lifetime.HighestKills:N0}\n" +
                $"Highest damage dealt: {lifetime.HighestDamageDealt:N0}\n" +
                $"Highest damage taken: {lifetime.HighestDamageTaken:N0}\n" +
                $"Highest healing done: {lifetime.HighestHealing:N0}\n\n" +
                "Scoreboard records include every locally recorded CC match.");
        }
    }

    private static void DrawProgressBar(
        ImDrawListPtr draw,
        Vector2 start,
        Vector2 end,
        int rating,
        RankBand band,
        Vector4 jobColor,
        float scale)
    {
        var fraction = Math.Clamp(
            (rating - band.MinimumRating) / (float)Math.Max(1, band.NextRating - band.MinimumRating),
            0f,
            1f);
        var background = new Vector4(jobColor.X * 0.22f, jobColor.Y * 0.22f, jobColor.Z * 0.22f, 0.95f);
        draw.AddRectFilled(start, end, Pack(background), 8f * scale);

        if (fraction > 0f)
        {
            var fillEnd = new Vector2(start.X + (end.X - start.X) * fraction, end.Y);
            draw.AddRectFilled(start, fillEnd, Pack(WithAlpha(jobColor, 0.92f)), 8f * scale);
            draw.AddLine(
                new Vector2(start.X + 5f * scale, start.Y + 3f * scale),
                new Vector2(MathF.Max(start.X + 5f * scale, fillEnd.X - 5f * scale), start.Y + 3f * scale),
                Pack(WithAlpha(Vector4.One, 0.38f)),
                1.5f * scale);
        }

        for (var i = 1; i < 4; i++)
        {
            var x = start.X + (end.X - start.X) * i / 4f;
            draw.AddLine(
                new Vector2(x, start.Y + 4f * scale),
                new Vector2(x, end.Y - 4f * scale),
                Pack(WithAlpha(Vector4.One, 0.18f)),
                scale);
        }
    }

    private static string Compact(int value) => value switch
    {
        >= 1_000_000 => $"{value / 1_000_000d:0.##}M",
        >= 1_000 => $"{value / 1_000d:0.#}K",
        _ => value.ToString("N0"),
    };

    private static string JobName(CombatJob job) => job switch
    {
        CombatJob.PLD => "Paladin",
        CombatJob.WAR => "Warrior",
        CombatJob.DRK => "Dark Knight",
        CombatJob.GNB => "Gunbreaker",
        CombatJob.WHM => "White Mage",
        CombatJob.SCH => "Scholar",
        CombatJob.AST => "Astrologian",
        CombatJob.SGE => "Sage",
        CombatJob.MNK => "Monk",
        CombatJob.DRG => "Dragoon",
        CombatJob.NIN => "Ninja",
        CombatJob.SAM => "Samurai",
        CombatJob.RPR => "Reaper",
        CombatJob.VPR => "Viper",
        CombatJob.BRD => "Bard",
        CombatJob.MCH => "Machinist",
        CombatJob.DNC => "Dancer",
        CombatJob.BLM => "Black Mage",
        CombatJob.SMN => "Summoner",
        CombatJob.RDM => "Red Mage",
        CombatJob.PCT => "Pictomancer",
        _ => "Unknown",
    };

    private static Vector4 Rgb(uint value) => new(
        ((value >> 16) & 0xFF) / 255f,
        ((value >> 8) & 0xFF) / 255f,
        (value & 0xFF) / 255f,
        1f);

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
