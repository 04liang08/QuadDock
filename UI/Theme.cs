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

    internal static class Theme
    {
        public static Color Surface { get; private set; }
        public static Color CardBorder { get; private set; }
        public static Color CardFill { get; private set; }
        public static Color Text { get; private set; }
        public static Color EmptyHint { get; private set; }
        public static Color Accent { get; private set; }
        public static Color ScrollThumb { get; private set; }
        public static Color Edge { get; private set; }

        public static bool IsDark { get; private set; }

        static Theme()
        {
            Apply(ThemeMode.System);
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
                Text = Color.FromArgb(238, 242, 248);
                EmptyHint = Color.FromArgb(118, 128, 145);
                Accent = Color.FromArgb(88, 148, 255);
                ScrollThumb = Color.FromArgb(96, 106, 122);
                Edge = Color.FromArgb(52, 60, 74);
            }
            else
            {
                Surface = Color.FromArgb(244, 246, 250);
                CardBorder = Color.FromArgb(212, 218, 228);
                CardFill = Color.FromArgb(232, 236, 243);
                Text = Color.FromArgb(30, 34, 41);
                EmptyHint = Color.FromArgb(154, 162, 176);
                Accent = Color.FromArgb(64, 128, 255);
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
