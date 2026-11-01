using System;
using System.Drawing;
using Microsoft.Win32;

namespace QuadDock.UI
{
    internal enum ThemeMode
    {
        System = 0,
        Light = 1,
        Dark = 2
    }

    /// <summary>侧栏的两种视觉形态：卡片式（有底、有投影）与列表式（细分隔线、无底）。</summary>
    internal enum StripStyle
    {
        Card = 0,
        List = 1
    }

    internal static class Theme
    {
        public static Color Surface { get; private set; }
        public static Color CardBorder { get; private set; }
        public static Color CardFill { get; private set; }
        public static Color CardHoverFill { get; private set; }
        public static Color CardShadow { get; private set; }
        public static Color CardHighlight { get; private set; }
        public static Color RowHoverFill { get; private set; }
        public static Color Separator { get; private set; }
        public static Color Text { get; private set; }
        public static Color TextDim { get; private set; }
        public static Color EmptyHint { get; private set; }
        public static Color Watermark { get; private set; }
        public static Color Accent { get; private set; }

        /// <summary>悬停描边用的强调色，比 Accent 淡一档，避免看着像"已选中"。</summary>
        public static Color AccentRing { get; private set; }
        public static Color ScrollThumb { get; private set; }
        public static Color Edge { get; private set; }

        public static bool IsDark { get; private set; }

        /// <summary>界面字体。系统默认在中文下会退回宋体，小字号发虚，这里锁定雅黑。</summary>
        public static Font UiFont { get; private set; }

        /// <summary>空侧栏背景上的大号桌面序号水印。</summary>
        public static Font WatermarkFont { get; private set; }

        public static StripStyle Style { get; private set; }

        static Theme()
        {
            UiFont = CreateFont("Microsoft YaHei UI", 9f, FontStyle.Regular);
            WatermarkFont = CreateFont("Microsoft YaHei UI", 22f, FontStyle.Bold);
            Apply(ThemeMode.System);
        }

        public static void ApplyStyle(StripStyle style)
        {
            Style = style;
        }

        private static Font CreateFont(string family, float size, FontStyle style)
        {
            try
            {
                var font = new Font(family, size, style);
                if (string.Equals(font.Name, family, StringComparison.OrdinalIgnoreCase))
                {
                    return font;
                }
                font.Dispose();
            }
            catch
            {
                // 字体不存在时退到系统 UI 字体
            }

            try
            {
                return new Font(SystemFonts.MessageBoxFont.FontFamily, size, style);
            }
            catch
            {
                return new Font(FontFamily.GenericSansSerif, size, style);
            }
        }

        public static void Apply(ThemeMode mode)
        {
            bool dark = mode == ThemeMode.Dark ||
                        (mode == ThemeMode.System && !SystemUsesLightApps());
            IsDark = dark;

            if (dark)
            {
                Surface = Color.FromArgb(24, 28, 36);
                CardBorder = Color.FromArgb(46, 53, 66);
                CardFill = Color.FromArgb(33, 39, 49);
                CardHoverFill = Color.FromArgb(42, 51, 68);
                CardShadow = Color.FromArgb(64, 0, 0, 0);
                CardHighlight = Color.FromArgb(20, 255, 255, 255);
                RowHoverFill = Color.FromArgb(20, 255, 255, 255);
                Separator = Color.FromArgb(24, 255, 255, 255);
                Text = Color.FromArgb(238, 242, 248);
                TextDim = Color.FromArgb(136, 146, 162);
                EmptyHint = Color.FromArgb(118, 128, 145);
                Watermark = Color.FromArgb(40, 46, 57);
                Accent = Color.FromArgb(88, 148, 255);
                AccentRing = Color.FromArgb(132, 88, 148, 255);
                ScrollThumb = Color.FromArgb(96, 106, 122);
                Edge = Color.FromArgb(52, 60, 74);
            }
            else
            {
                Surface = Color.FromArgb(244, 246, 250);
                CardBorder = Color.FromArgb(219, 225, 234);
                CardFill = Color.FromArgb(255, 255, 255);
                CardHoverFill = Color.FromArgb(238, 244, 253);
                CardShadow = Color.FromArgb(46, 52, 74, 108);
                CardHighlight = Color.FromArgb(0, 255, 255, 255);
                RowHoverFill = Color.FromArgb(16, 30, 60, 120);
                Separator = Color.FromArgb(20, 30, 50, 90);
                Text = Color.FromArgb(30, 34, 41);
                TextDim = Color.FromArgb(124, 132, 148);
                EmptyHint = Color.FromArgb(154, 162, 176);
                Watermark = Color.FromArgb(228, 232, 240);
                Accent = Color.FromArgb(64, 128, 255);
                AccentRing = Color.FromArgb(150, 64, 128, 255);
                ScrollThumb = Color.FromArgb(168, 177, 191);
                Edge = Color.FromArgb(206, 214, 226);
            }
        }

        private static bool SystemUsesLightApps()
        {
            try
            {
                object value = Registry.GetValue(
                    @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                    "AppsUseLightTheme",
                    1);
                return value == null || Convert.ToInt32(value) != 0;
            }
            catch
            {
                return true;
            }
        }
    }
}
