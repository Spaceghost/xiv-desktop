using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Plugin.Services;
using XivDesktop.Core;

namespace XivDesktop.Plugin.Windows;

/// <summary>App icons (PNG through ITextureProvider, else a coloured letter tile) and Font Awesome glyphs.</summary>
public static class IconDrawer
{
    public static void App(ITextureProvider textures, ImDrawListPtr draw, AppInfo app, Vector2 pos, float size, bool dimmed)
    {
        var max = pos + new Vector2(size, size);
        var tint = dimmed ? 0x80FFFFFFu : 0xFFFFFFFFu;
        if (app.IconPath is { } path)
        {
            var wrap = textures.GetFromFile(path).GetWrapOrDefault();
            if (wrap != null)
            {
                draw.AddImage(wrap.Handle, pos, max, Vector2.Zero, Vector2.One, tint);
                return;
            }
        }

        Letter(draw, LetterTile.Letter(app.Name), LetterTile.Color(app.Id), pos, size, dimmed);
    }

    public static void Letter(ImDrawListPtr draw, string letter, uint color, Vector2 pos, float size, bool dimmed)
    {
        var tint = dimmed ? 0x80FFFFFFu : 0xFFFFFFFFu;
        if (dimmed)
            color = (color & 0x00FFFFFFu) | 0x80000000u;
        draw.AddRectFilled(pos, pos + new Vector2(size, size), color, size * 0.22f);
        var font = ImGui.GetFont();
        var fontSize = size * 0.55f;
        var textSize = ImGui.CalcTextSize(letter) * (fontSize / ImGui.GetFontSize());
        draw.AddText(font, fontSize, pos + ((new Vector2(size) - textSize) / 2), tint, letter);
    }

    /// <summary>A Font Awesome glyph centred in a rounded tile of <paramref name="tile"/> colour.</summary>
    public static void Glyph(IUiBuilder ui, ImDrawListPtr draw, FontAwesomeIcon icon, uint tile, uint glyph, Vector2 pos, float size)
    {
        draw.AddRectFilled(pos, pos + new Vector2(size, size), tile, size * 0.22f);
        using (ui.IconFontFixedWidthHandle.Push())
        {
            var text = icon.ToIconString();
            var font = ImGui.GetFont();
            var fontSize = size * 0.5f;
            var textSize = ImGui.CalcTextSize(text) * (fontSize / ImGui.GetFontSize());
            draw.AddText(font, fontSize, pos + ((new Vector2(size) - textSize) / 2), glyph, text);
        }
    }
}
