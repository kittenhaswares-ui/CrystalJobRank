using System.Numerics;
using CrystalJobRank.Core;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using Dalamud.Plugin.Services;

namespace CrystalJobRank.Plugin.UI;

internal static class JobIconVisuals
{
    public static void Draw(
        ITextureProvider textureProvider,
        ImDrawListPtr draw,
        Vector2 center,
        float radius,
        CombatJob job,
        RankBand band)
    {
        var scale = radius / 34f;
        var jobColor = RankVisuals.JobColor(job);
        var metal = band.MetalColor;

        draw.AddCircleFilled(center, radius * 1.04f, Pack(WithAlpha(jobColor, 0.12f)), 48);
        DrawRankFrame(draw, center, radius, band, scale);
        DrawJobMotif(draw, center, radius, job, band, scale);

        draw.AddCircleFilled(center + new Vector2(0, 2f * scale), radius * 0.78f, Pack(new Vector4(0.02f, 0.025f, 0.04f, 0.88f)), 40);
        draw.AddCircle(center, radius * 0.79f, Pack(WithAlpha(metal, 0.72f)), 40, 1.5f * scale);

        var iconRadius = radius * 0.58f;
        var iconMin = center - new Vector2(iconRadius);
        var iconMax = center + new Vector2(iconRadius);
        var iconId = IconId(job);
        if (!TryDrawIcon(textureProvider, draw, iconId, true, iconMin, iconMax, metal) &&
            !TryDrawIcon(textureProvider, draw, iconId, false, iconMin, iconMax, metal))
        {
            var label = job.ToString();
            var textSize = ImGui.CalcTextSize(label);
            draw.AddText(center - textSize / 2f, Pack(metal), label);
        }
    }

    private static bool TryDrawIcon(
        ITextureProvider textureProvider,
        ImDrawListPtr draw,
        uint iconId,
        bool highResolution,
        Vector2 iconMin,
        Vector2 iconMax,
        Vector4 tint)
    {
        var lookup = new GameIconLookup(iconId, false, highResolution);
        if (!textureProvider.TryGetFromGameIcon(lookup, out var shared) ||
            !shared.TryGetWrap(out var wrap, out _))
        {
            return false;
        }

        draw.AddImage(wrap.Handle, iconMin, iconMax, Vector2.Zero, Vector2.One, Pack(tint));
        return true;
    }

    public static uint IconId(CombatJob job) => job switch
    {
        CombatJob.PLD => 62019,
        CombatJob.MNK => 62020,
        CombatJob.WAR => 62021,
        CombatJob.DRG => 62022,
        CombatJob.BRD => 62023,
        CombatJob.WHM => 62024,
        CombatJob.BLM => 62025,
        CombatJob.SMN => 62027,
        CombatJob.SCH => 62028,
        CombatJob.NIN => 62030,
        CombatJob.MCH => 62031,
        CombatJob.DRK => 62032,
        CombatJob.AST => 62033,
        CombatJob.SAM => 62034,
        CombatJob.RDM => 62035,
        CombatJob.GNB => 62037,
        CombatJob.DNC => 62038,
        CombatJob.RPR => 62039,
        CombatJob.SGE => 62040,
        CombatJob.VPR => 62041,
        CombatJob.PCT => 62042,
        _ => 62000,
    };

    private static void DrawRankFrame(ImDrawListPtr draw, Vector2 center, float radius, RankBand band, float scale)
    {
        var metal = band.MetalColor;
        if (band.OrnamentLevel >= 5)
        {
            DrawRadialLines(draw, center, radius * 1.05f, radius * 1.34f, 8, MathF.PI / 8f, metal, 2.6f * scale);
        }

        if (band.OrnamentLevel >= 4)
        {
            var d = radius * 1.18f;
            draw.AddLine(center + new Vector2(0, -d), center + new Vector2(d, 0), Pack(WithAlpha(metal, 0.82f)), 2f * scale);
            draw.AddLine(center + new Vector2(d, 0), center + new Vector2(0, d), Pack(WithAlpha(metal, 0.82f)), 2f * scale);
            draw.AddLine(center + new Vector2(0, d), center + new Vector2(-d, 0), Pack(WithAlpha(metal, 0.82f)), 2f * scale);
            draw.AddLine(center + new Vector2(-d, 0), center + new Vector2(0, -d), Pack(WithAlpha(metal, 0.82f)), 2f * scale);
        }

        if (band.OrnamentLevel >= 3)
        {
            DrawWing(draw, center, radius, -1f, metal, scale);
            DrawWing(draw, center, radius, 1f, metal, scale);
        }

        if (band.OrnamentLevel >= 2)
        {
            var crownY = center.Y - radius * 1.05f;
            for (var i = -1; i <= 1; i++)
            {
                var x = center.X + i * 8f * scale;
                draw.AddTriangleFilled(
                    new Vector2(x - 5f * scale, crownY + 7f * scale),
                    new Vector2(x, crownY - (i == 0 ? 6f : 3f) * scale),
                    new Vector2(x + 5f * scale, crownY + 7f * scale),
                    Pack(WithAlpha(metal, 0.9f)));
            }
        }

        draw.AddCircle(center, radius, Pack(metal), 48, 2.7f * scale);
        if (band.OrnamentLevel >= 1)
        {
            draw.AddCircle(center, radius - 4f * scale, Pack(WithAlpha(metal, 0.46f)), 48, 1.2f * scale);
        }
    }

    private static void DrawJobMotif(
        ImDrawListPtr draw,
        Vector2 center,
        float radius,
        CombatJob job,
        RankBand band,
        float scale)
    {
        var color = WithAlpha(RankVisuals.JobColor(job), 0.45f + band.OrnamentLevel * 0.07f);
        switch (job)
        {
            case CombatJob.PLD:
                DrawRadialLines(draw, center, radius * 0.92f, radius * 1.18f, 4, 0f, color, 2f * scale);
                break;
            case CombatJob.WAR:
                DrawSpikes(draw, center, radius, 6, MathF.PI / 6f, color, scale);
                break;
            case CombatJob.DRK:
                DrawLightning(draw, center, radius, color, scale);
                break;
            case CombatJob.GNB:
                DrawRadialLines(draw, center, radius, radius * 1.22f, 3, -MathF.PI / 2f, color, 3f * scale);
                break;
            case CombatJob.WHM:
                DrawPetals(draw, center, radius, color, scale);
                break;
            case CombatJob.SCH:
                DrawWing(draw, center, radius * 0.92f, -1f, color, scale);
                DrawWing(draw, center, radius * 0.92f, 1f, color, scale);
                break;
            case CombatJob.AST:
                draw.AddCircle(center, radius * 1.08f, Pack(color), 40, 1.6f * scale);
                DrawSpikes(draw, center, radius * 0.94f, 4, MathF.PI / 4f, color, scale);
                break;
            case CombatJob.SGE:
                DrawNouliths(draw, center, radius, color, scale);
                break;
            case CombatJob.MNK:
                DrawChakra(draw, center, radius, color, scale);
                break;
            case CombatJob.DRG:
                DrawSpikes(draw, center, radius, 4, MathF.PI / 4f, color, 1.35f * scale);
                break;
            case CombatJob.NIN:
                DrawShuriken(draw, center, radius, color, scale);
                break;
            case CombatJob.SAM:
                DrawSun(draw, center, radius, color, scale);
                break;
            case CombatJob.RPR:
                DrawScythe(draw, center, radius, color, scale);
                break;
            case CombatJob.VPR:
                DrawFangs(draw, center, radius, color, scale);
                break;
            case CombatJob.BRD:
                DrawMusicNotes(draw, center, radius, color, scale);
                break;
            case CombatJob.MCH:
                DrawGear(draw, center, radius, color, scale);
                break;
            case CombatJob.DNC:
                DrawFeathers(draw, center, radius, color, scale);
                break;
            case CombatJob.BLM:
                DrawMeteors(draw, center, radius, color, scale);
                break;
            case CombatJob.SMN:
                DrawHorns(draw, center, radius, color, scale);
                break;
            case CombatJob.RDM:
                DrawRoseRapier(draw, center, radius, color, scale);
                break;
            case CombatJob.PCT:
                DrawPaintDrops(draw, center, radius, color, scale);
                break;
        }
    }

    private static void DrawLightning(ImDrawListPtr draw, Vector2 center, float radius, Vector4 color, float scale)
    {
        var lightning = new Vector4(1f, 0.16f, 0.22f, color.W);
        foreach (var side in new[] { -1f, 1f })
        {
            var a = center + new Vector2(side * radius * 0.78f, -radius * 0.9f);
            var b = center + new Vector2(side * radius * 1.12f, -radius * 0.28f);
            var c = center + new Vector2(side * radius * 0.86f, radius * 0.04f);
            var d = center + new Vector2(side * radius * 1.18f, radius * 0.74f);
            draw.AddLine(a, b, Pack(lightning), 2.4f * scale);
            draw.AddLine(b, c, Pack(lightning), 2.4f * scale);
            draw.AddLine(c, d, Pack(lightning), 2.4f * scale);
        }
    }

    private static void DrawMusicNotes(ImDrawListPtr draw, Vector2 center, float radius, Vector4 color, float scale)
    {
        foreach (var side in new[] { -1f, 1f })
        {
            var head = center + new Vector2(side * radius * 1.02f, radius * 0.46f);
            draw.AddCircleFilled(head, 3.5f * scale, Pack(color), 16);
            draw.AddLine(head + new Vector2(side * 2f, -2f) * scale, head + new Vector2(side * 2f, -19f) * scale, Pack(color), 2f * scale);
            draw.AddLine(head + new Vector2(side * 2f, -19f) * scale, head + new Vector2(side * 10f, -15f) * scale, Pack(color), 2f * scale);
        }
    }

    private static void DrawFeathers(ImDrawListPtr draw, Vector2 center, float radius, Vector4 color, float scale)
    {
        foreach (var side in new[] { -1f, 1f })
        {
            for (var i = 0; i < 3; i++)
            {
                var root = center + new Vector2(side * radius * 0.72f, (i - 1) * 7f * scale);
                var tip = center + new Vector2(side * radius * (1.18f + i * 0.08f), (-0.58f + i * 0.52f) * radius);
                draw.AddLine(root, tip, Pack(color), (3.8f - i * 0.7f) * scale);
            }
        }
    }

    private static void DrawPetals(ImDrawListPtr draw, Vector2 center, float radius, Vector4 color, float scale)
    {
        for (var i = 0; i < 4; i++)
        {
            var angle = MathF.PI * i / 2f + MathF.PI / 4f;
            var petal = center + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * radius * 1.02f;
            draw.AddCircle(petal, 5f * scale, Pack(color), 18, 2f * scale);
        }
    }

    private static void DrawNouliths(ImDrawListPtr draw, Vector2 center, float radius, Vector4 color, float scale)
    {
        for (var i = 0; i < 4; i++)
        {
            var angle = MathF.PI * i / 2f + MathF.PI / 4f;
            var point = center + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * radius * 1.08f;
            draw.AddRectFilled(point - new Vector2(3f, 8f) * scale, point + new Vector2(3f, 8f) * scale, Pack(color), 2f * scale);
        }
    }

    private static void DrawChakra(ImDrawListPtr draw, Vector2 center, float radius, Vector4 color, float scale)
    {
        for (var i = 0; i < 6; i++)
        {
            var angle = MathF.PI * i / 3f;
            var point = center + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * radius * 1.05f;
            draw.AddCircleFilled(point, 3f * scale, Pack(color), 14);
        }
    }

    private static void DrawShuriken(ImDrawListPtr draw, Vector2 center, float radius, Vector4 color, float scale)
    {
        var d = radius * 1.18f;
        draw.AddLine(center + new Vector2(-d, 0), center + new Vector2(d, 0), Pack(color), 2f * scale);
        draw.AddLine(center + new Vector2(0, -d), center + new Vector2(0, d), Pack(color), 2f * scale);
        draw.AddLine(center + new Vector2(-d * 0.72f), center + new Vector2(d * 0.72f), Pack(color), 1.5f * scale);
        draw.AddLine(center + new Vector2(-d * 0.72f, d * 0.72f), center + new Vector2(d * 0.72f, -d * 0.72f), Pack(color), 1.5f * scale);
    }

    private static void DrawSun(ImDrawListPtr draw, Vector2 center, float radius, Vector4 color, float scale)
    {
        var sun = center + new Vector2(0, -radius * 0.96f);
        draw.AddCircle(sun, 9f * scale, Pack(color), 24, 2f * scale);
        DrawRadialLines(draw, sun, 11f * scale, 18f * scale, 7, -MathF.PI, color, 1.5f * scale);
    }

    private static void DrawScythe(ImDrawListPtr draw, Vector2 center, float radius, Vector4 color, float scale)
    {
        foreach (var side in new[] { -1f, 1f })
        {
            var top = center + new Vector2(side * radius * 0.82f, -radius * 0.82f);
            var middle = center + new Vector2(side * radius * 1.16f, 0);
            var bottom = center + new Vector2(side * radius * 0.75f, radius * 0.86f);
            draw.AddLine(top, middle, Pack(color), 3f * scale);
            draw.AddLine(middle, bottom, Pack(color), 2f * scale);
        }
    }

    private static void DrawFangs(ImDrawListPtr draw, Vector2 center, float radius, Vector4 color, float scale)
    {
        foreach (var side in new[] { -1f, 1f })
        {
            var outer = center + new Vector2(side * radius * 1.14f, -radius * 0.48f);
            draw.AddTriangleFilled(
                outer,
                outer + new Vector2(-side * 8f, 7f) * scale,
                outer + new Vector2(-side * 3f, 20f) * scale,
                Pack(color));
        }
    }

    private static void DrawGear(ImDrawListPtr draw, Vector2 center, float radius, Vector4 color, float scale)
    {
        draw.AddCircle(center, radius * 1.07f, Pack(color), 24, 2f * scale);
        DrawRadialLines(draw, center, radius * 1.02f, radius * 1.2f, 8, 0f, color, 3f * scale);
    }

    private static void DrawMeteors(ImDrawListPtr draw, Vector2 center, float radius, Vector4 color, float scale)
    {
        for (var i = -1; i <= 1; i++)
        {
            var point = center + new Vector2(i * radius * 0.72f, -radius * (1.02f + Math.Abs(i) * 0.12f));
            draw.AddCircleFilled(point, (4f - Math.Abs(i)) * scale, Pack(color), 14);
            draw.AddLine(point, point + new Vector2(-i * 6f, 10f) * scale, Pack(color), 1.5f * scale);
        }
    }

    private static void DrawHorns(ImDrawListPtr draw, Vector2 center, float radius, Vector4 color, float scale)
    {
        foreach (var side in new[] { -1f, 1f })
        {
            var root = center + new Vector2(side * radius * 0.68f, -radius * 0.62f);
            var elbow = center + new Vector2(side * radius * 1.12f, -radius * 0.92f);
            var tip = center + new Vector2(side * radius * 1.2f, -radius * 0.35f);
            draw.AddLine(root, elbow, Pack(color), 3f * scale);
            draw.AddLine(elbow, tip, Pack(color), 2f * scale);
        }
    }

    private static void DrawRoseRapier(ImDrawListPtr draw, Vector2 center, float radius, Vector4 color, float scale)
    {
        var top = center + new Vector2(0, -radius * 1.2f);
        var bottom = center + new Vector2(0, radius * 1.2f);
        draw.AddLine(top, bottom, Pack(color), 2f * scale);
        for (var i = 0; i < 5; i++)
        {
            var angle = MathF.PI * 2f * i / 5f;
            var petal = center + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * radius * 1.04f;
            draw.AddCircle(petal, 4f * scale, Pack(color), 14, 1.5f * scale);
        }
    }

    private static void DrawPaintDrops(ImDrawListPtr draw, Vector2 center, float radius, Vector4 color, float scale)
    {
        var colors = new[]
        {
            new Vector4(1f, 0.35f, 0.72f, color.W),
            new Vector4(0.24f, 0.78f, 1f, color.W),
            new Vector4(1f, 0.72f, 0.18f, color.W),
        };
        for (var i = 0; i < colors.Length; i++)
        {
            var angle = -MathF.PI * 0.85f + i * MathF.PI * 0.85f;
            var point = center + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * radius * 1.12f;
            draw.AddCircleFilled(point, (3f + i) * scale, Pack(colors[i]), 16);
        }
    }

    private static void DrawSpikes(ImDrawListPtr draw, Vector2 center, float radius, int count, float rotation, Vector4 color, float scale)
    {
        for (var i = 0; i < count; i++)
        {
            var angle = rotation + MathF.PI * 2f * i / count;
            var direction = new Vector2(MathF.Cos(angle), MathF.Sin(angle));
            var tangent = new Vector2(-direction.Y, direction.X);
            var root = center + direction * radius * 0.95f;
            draw.AddTriangleFilled(
                root - tangent * 4f * scale,
                center + direction * radius * 1.24f,
                root + tangent * 4f * scale,
                Pack(color));
        }
    }

    private static void DrawRadialLines(
        ImDrawListPtr draw,
        Vector2 center,
        float inner,
        float outer,
        int count,
        float rotation,
        Vector4 color,
        float thickness)
    {
        for (var i = 0; i < count; i++)
        {
            var angle = rotation + MathF.PI * 2f * i / count;
            var direction = new Vector2(MathF.Cos(angle), MathF.Sin(angle));
            draw.AddLine(center + direction * inner, center + direction * outer, Pack(color), thickness);
        }
    }

    private static void DrawWing(ImDrawListPtr draw, Vector2 center, float radius, float side, Vector4 color, float scale)
    {
        var root = center + new Vector2(side * radius * 0.72f, 3f * scale);
        for (var i = 0; i < 3; i++)
        {
            var inner = root + new Vector2(side * i * 2f, -i * 4f) * scale;
            var outer = root + new Vector2(side * (14f + i * 4f), (-11f + i * 8f)) * scale;
            draw.AddLine(inner, outer, Pack(WithAlpha(color, 0.82f)), (4f - i * 0.7f) * scale);
        }
    }

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
