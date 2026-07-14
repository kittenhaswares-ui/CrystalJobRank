using System.Numerics;
using CrystalJobRank.Core;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;

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
    private static readonly Vector4 CardBackground = new(0.075f, 0.085f, 0.12f, 0.98f);
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

    public static void DrawRatingCard(RatingState rating, float availableWidth)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var width = MathF.Max(310f * scale, availableWidth - 4f * scale);
        var height = 138f * scale;
        var origin = ImGui.GetCursorScreenPos();
        var end = origin + new Vector2(width, height);
        var draw = ImGui.GetWindowDrawList();
        var jobColor = JobColor(rating.Job);
        var band = GetBand(rating.Rating);

        draw.AddRectFilled(origin, end, Pack(CardBackground), 10f * scale);
        draw.AddRectFilled(
            origin,
            new Vector2(origin.X + 6f * scale, end.Y),
            Pack(jobColor),
            10f * scale);
        draw.AddRect(origin, end, Pack(CardBorder), 10f * scale);

        var badgeCenter = origin + new Vector2(45f, 52f) * scale;
        DrawRankBadge(draw, badgeCenter, 29f * scale, band);

        var contentX = origin.X + 86f * scale;
        var titleY = origin.Y + 13f * scale;
        draw.AddText(new Vector2(contentX, titleY), Pack(jobColor), rating.Job.ToString());
        draw.AddText(new Vector2(contentX + 39f * scale, titleY), Pack(MutedText), JobName(rating.Job));

        var ratingText = $"{rating.Rating:N0}";
        draw.AddText(new Vector2(contentX, origin.Y + 36f * scale), Pack(PrimaryText), ratingText);
        draw.AddText(new Vector2(contentX + 55f * scale, origin.Y + 36f * scale), Pack(band.MetalColor), band.Name.ToUpperInvariant());

        var barStart = new Vector2(contentX, origin.Y + 65f * scale);
        var barEnd = new Vector2(end.X - 14f * scale, barStart.Y + 17f * scale);
        DrawProgressBar(draw, barStart, barEnd, rating.Rating, band, jobColor, scale);

        var progressLabel = band.Tier == RankTier.Crystal
            ? $"{rating.Rating:N0} / {RatingEngine.MaximumRating:N0}"
            : $"{Math.Max(0, rating.Rating - band.MinimumRating):N0} / {band.NextRating - band.MinimumRating:N0} to {band.NextName}";
        draw.AddText(new Vector2(contentX, origin.Y + 88f * scale), Pack(MutedText), progressLabel);

        var provisional = rating.Matches < RatingEngine.ProvisionalMatches
            ? $"  •  provisional {rating.Matches}/{RatingEngine.ProvisionalMatches}"
            : string.Empty;
        var stats = $"{rating.Wins}W  {rating.Losses}L  •  {rating.WinRate:P0}{provisional}";
        draw.AddText(new Vector2(contentX, origin.Y + 111f * scale), Pack(PrimaryText), stats);

        ImGui.Dummy(new Vector2(width, height));
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                $"{rating.Job} {band.Name}\n" +
                $"{rating.Rating:N0} rating\n" +
                $"{rating.Matches} Ranked matches • {rating.WinRate:P1} observed win rate\n" +
                $"{RatingEngine.EstimatedWinProbability(rating.Rating):P1} estimated win probability vs reference");
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

    private static void DrawRankBadge(ImDrawListPtr draw, Vector2 center, float radius, RankBand band)
    {
        var scale = radius / 29f;
        var metal = band.MetalColor;
        var shadow = Rgb(0x171A24);

        if (band.OrnamentLevel >= 5)
        {
            for (var i = 0; i < 8; i++)
            {
                var angle = MathF.PI * i / 4f;
                var inner = center + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * radius;
                var outer = center + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * radius * 1.28f;
                draw.AddLine(inner, outer, Pack(WithAlpha(metal, 0.78f)), 3f * scale);
            }
        }

        if (band.OrnamentLevel >= 4)
        {
            var diamondRadius = radius * 1.12f;
            var top = center + new Vector2(0, -diamondRadius);
            var right = center + new Vector2(diamondRadius, 0);
            var bottom = center + new Vector2(0, diamondRadius);
            var left = center + new Vector2(-diamondRadius, 0);
            draw.AddTriangleFilled(top, right, bottom, Pack(WithAlpha(metal, 0.24f)));
            draw.AddTriangleFilled(top, bottom, left, Pack(WithAlpha(metal, 0.16f)));
        }

        if (band.OrnamentLevel >= 3)
        {
            DrawWing(draw, center, radius, metal, -1f);
            DrawWing(draw, center, radius, metal, 1f);
        }

        draw.AddCircleFilled(center + new Vector2(0, 2f * scale), radius, Pack(WithAlpha(shadow, 0.8f)), 40);
        draw.AddCircleFilled(center, radius, Pack(Rgb(0x24293A)), 40);
        draw.AddCircle(center, radius, Pack(metal), 40, 2.5f * scale);

        if (band.OrnamentLevel >= 1)
        {
            draw.AddCircle(center, radius - 4f * scale, Pack(WithAlpha(metal, 0.5f)), 40, 1.2f * scale);
        }

        if (band.OrnamentLevel >= 2)
        {
            var crownY = center.Y - radius - 2f * scale;
            draw.AddTriangleFilled(
                new Vector2(center.X - 10f * scale, crownY + 5f * scale),
                new Vector2(center.X - 5f * scale, crownY - 3f * scale),
                new Vector2(center.X, crownY + 5f * scale),
                Pack(metal));
            draw.AddTriangleFilled(
                new Vector2(center.X, crownY + 5f * scale),
                new Vector2(center.X + 5f * scale, crownY - 3f * scale),
                new Vector2(center.X + 10f * scale, crownY + 5f * scale),
                Pack(metal));
        }

        DrawSword(
            draw,
            center + new Vector2(-14f, 17f) * scale,
            center + new Vector2(14f, -17f) * scale,
            metal,
            scale);
        DrawSword(
            draw,
            center + new Vector2(14f, 17f) * scale,
            center + new Vector2(-14f, -17f) * scale,
            metal,
            scale);

        if (band.OrnamentLevel >= 4)
        {
            var gem = band.OrnamentLevel >= 5 ? Rgb(0xFFF4FF) : Rgb(0xDDF5FF);
            var gemCenter = center + new Vector2(0, 13f * scale);
            draw.AddTriangleFilled(
                gemCenter + new Vector2(0, -5f * scale),
                gemCenter + new Vector2(5f * scale, 0),
                gemCenter + new Vector2(0, 6f * scale),
                Pack(gem));
            draw.AddTriangleFilled(
                gemCenter + new Vector2(0, -5f * scale),
                gemCenter + new Vector2(0, 6f * scale),
                gemCenter + new Vector2(-5f * scale, 0),
                Pack(WithAlpha(gem, 0.72f)));
        }
    }

    private static void DrawSword(
        ImDrawListPtr draw,
        Vector2 handle,
        Vector2 tip,
        Vector4 metal,
        float scale)
    {
        var direction = Vector2.Normalize(tip - handle);
        var perpendicular = new Vector2(-direction.Y, direction.X);
        var guardCenter = handle + direction * 7f * scale;
        var bladeBase = guardCenter + direction * 2f * scale;
        var tipBase = tip - direction * 7f * scale;
        var dark = new Vector4(metal.X * 0.42f, metal.Y * 0.42f, metal.Z * 0.42f, 1f);

        draw.AddLine(bladeBase + new Vector2(1.5f, 2f) * scale, tip + new Vector2(1.5f, 2f) * scale, Pack(Rgb(0x10131B)), 7f * scale);
        draw.AddLine(bladeBase, tipBase, Pack(dark), 6f * scale);
        draw.AddTriangleFilled(tip, tipBase + perpendicular * 3f * scale, tipBase - perpendicular * 3f * scale, Pack(metal));
        draw.AddLine(bladeBase + perpendicular * 1.3f * scale, tipBase + perpendicular * 1.3f * scale, Pack(WithAlpha(Vector4.One, 0.48f)), 1.2f * scale);

        draw.AddLine(guardCenter - perpendicular * 7f * scale, guardCenter + perpendicular * 7f * scale, Pack(Rgb(0x11141C)), 5f * scale);
        draw.AddLine(guardCenter - perpendicular * 7f * scale, guardCenter + perpendicular * 7f * scale, Pack(metal), 2.8f * scale);
        draw.AddLine(handle, guardCenter, Pack(dark), 4f * scale);
        draw.AddCircleFilled(handle, 3.2f * scale, Pack(metal), 12);
    }

    private static void DrawWing(ImDrawListPtr draw, Vector2 center, float radius, Vector4 metal, float direction)
    {
        var scale = radius / 29f;
        var root = center + new Vector2(direction * radius * 0.72f, 4f * scale);
        for (var i = 0; i < 3; i++)
        {
            var inner = root + new Vector2(direction * i * 3f, -i * 4f) * scale;
            var outer = root + new Vector2(direction * (14f + i * 3f), (-10f + i * 7f)) * scale;
            draw.AddLine(inner, outer, Pack(WithAlpha(metal, 0.82f)), (4f - i * 0.7f) * scale);
        }
    }

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
