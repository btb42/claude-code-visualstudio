using System;
using System.Collections.Generic;
using System.Drawing;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;

namespace ClaudeCode.VisualStudio.Services
{
    /// <summary>
    /// Maps the current Visual Studio theme to the CSS custom properties the chat UI uses,
    /// so the WebView matches VS light/dark/blue themes. Raises <see cref="ThemeChanged"/>
    /// when the user switches themes.
    /// </summary>
    public sealed class ThemeService
    {
        public event Action<Dictionary<string, string>> ThemeChanged;

        public ThemeService()
        {
            VSColorTheme.ThemeChanged += _ => ThemeChanged?.Invoke(GetThemeVariables());
        }

        public Dictionary<string, string> GetThemeVariables()
        {
            var bg = GetColor(EnvironmentColors.ToolWindowBackgroundColorKey, Color.FromArgb(30, 30, 30));
            var fg = GetColor(EnvironmentColors.ToolWindowTextColorKey, Color.FromArgb(212, 212, 212));
            // fg-strong: push fg toward the high-contrast extreme for the current theme.
            // On dark themes bg is dark → push toward white; on light themes push toward black.
            bool isDark = Luminance(bg) < 0.5;
            var fgStrong = isDark ? Blend(fg, Color.White, 0.6f) : Blend(fg, Color.Black, 0.5f);
            // fg-mid: halfway between fg and fg-dim, for tool summary text.
            var fgDim = GetColor(EnvironmentColors.SystemGrayTextColorKey, Color.FromArgb(157, 157, 157));
            var fgMid = Blend(fg, fgDim, 0.4f);
            return new Dictionary<string, string>
            {
                ["--bg"] = Hex(bg),
                ["--bg-alt"] = Hex(EnvironmentColors.CommandBarGradientBeginColorKey, Color.FromArgb(37, 37, 38)),
                ["--bg-input"] = Hex(EnvironmentColors.ComboBoxBackgroundColorKey, Color.FromArgb(60, 60, 60)),
                ["--fg"] = Hex(fg),
                ["--fg-dim"] = Hex(fgDim),
                ["--fg-strong"] = Hex(fgStrong),
                ["--fg-mid"] = Hex(fgMid),
                ["--border"] = Hex(EnvironmentColors.ToolWindowBorderColorKey, Color.FromArgb(60, 60, 60)),
                ["--code-bg"] = Hex(EnvironmentColors.ToolWindowBackgroundColorKey, Color.FromArgb(27, 27, 27)),
                ["--user-bg"] = Hex(EnvironmentColors.ToolWindowTabSelectedTabColorKey, Color.FromArgb(45, 58, 79)),
                // Claude brand accent stays constant across themes.
                ["--accent"] = "#cc7a3b",
                ["--accent-fg"] = "#ffffff",
            };
        }

        private static Color GetColor(ThemeResourceKey key, Color fallback)
        {
            try { var c = VSColorTheme.GetThemedColor(key); return c.A == 0 ? fallback : c; }
            catch { return fallback; }
        }

        private static string Hex(Color c) => "#" + c.R.ToString("X2") + c.G.ToString("X2") + c.B.ToString("X2");

        private static string Hex(ThemeResourceKey key, Color fallback) => Hex(GetColor(key, fallback));

        private static Color Blend(Color a, Color b, float t) => Color.FromArgb(
            (int)(a.R + (b.R - a.R) * t),
            (int)(a.G + (b.G - a.G) * t),
            (int)(a.B + (b.B - a.B) * t));

        // Relative luminance (sRGB, approximate). 0 = black, 1 = white.
        private static float Luminance(Color c) => (0.299f * c.R + 0.587f * c.G + 0.114f * c.B) / 255f;
    }
}
