using System;
using System.IO;
using System.Text.Json;

namespace QuadDock.Core
{
    /// <summary>四边侧栏的持久化配置，长度单位均为 96 DPI 下的逻辑像素。</summary>
    internal sealed class DockSettings
    {
        public const int MinVertical = 64;
        public const int MaxVertical = 520;
        public const int MinHorizontal = 56;
        public const int MaxHorizontal = 400;

        public const int DefaultVertical = 220;
        public const int DefaultHorizontal = 130;

        public int LeftThickness { get; set; } = DefaultVertical;
        public int RightThickness { get; set; } = DefaultVertical;
        public int TopThickness { get; set; } = DefaultHorizontal;
        public int BottomThickness { get; set; } = DefaultHorizontal;

        public bool LeftEnabled { get; set; } = true;
        public bool RightEnabled { get; set; } = true;
        public bool TopEnabled { get; set; } = true;
        public bool BottomEnabled { get; set; } = true;

        /// <summary>0 = 跟随系统，1 = 浅色，2 = 深色。</summary>
        public int ThemeMode { get; set; }

        /// <summary>目标显示器设备名，空表示主显示器。</summary>
        public string Monitor { get; set; } = "";

        /// <summary>桌面槽位总数：中央 1 个 + 四条边。</summary>
        public const int SlotCount = 5;

        /// <summary>
        /// 五个槽位各自存着列表里的第几个桌面（从 1 开始）。
        /// 顺序固定为：中央、左、上、右、下，必须互不重复。
        /// 点击侧栏就是交换它和中央的这两个值。
        /// </summary>
        public int[] Slots { get; set; } = { 1, 2, 3, 4, 5 };

        /// <summary>槽位下标：0 中央，其余与 Edge 对应（左 1、上 2、右 3、下 4）。</summary>
        public static int SlotOf(Edge edge)
        {
            return (int)edge + 1;
        }

        public int GetSlot(int slot)
        {
            if (Slots == null || Slots.Length != SlotCount || slot < 0 || slot >= SlotCount)
            {
                return 0;
            }

            return Slots[slot];
        }

        public void SetSlot(int slot, int desktopNumber)
        {
            if (Slots == null || Slots.Length != SlotCount || slot < 0 || slot >= SlotCount)
            {
                return;
            }

            Slots[slot] = Math.Max(1, Math.Min(SlotCount, desktopNumber));
        }

        public void ResetSlots()
        {
            Slots = new[] { 1, 2, 3, 4, 5 };
        }

        public int HotkeyModifiers { get; set; } = Win32.MOD_CONTROL | Win32.MOD_ALT;
        public int HotkeyKey { get; set; } = 0x20; // Space

        public int GetThickness(Edge edge)
        {
            switch (edge)
            {
                case Edge.Left: return LeftThickness;
                case Edge.Right: return RightThickness;
                case Edge.Top: return TopThickness;
                default: return BottomThickness;
            }
        }

        public void SetThickness(Edge edge, int value)
        {
            int min = edge.IsVertical() ? MinVertical : MinHorizontal;
            int max = edge.IsVertical() ? MaxVertical : MaxHorizontal;
            value = Math.Max(min, Math.Min(max, value));

            switch (edge)
            {
                case Edge.Left: LeftThickness = value; break;
                case Edge.Right: RightThickness = value; break;
                case Edge.Top: TopThickness = value; break;
                default: BottomThickness = value; break;
            }
        }

        public bool IsEnabled(Edge edge)
        {
            switch (edge)
            {
                case Edge.Left: return LeftEnabled;
                case Edge.Right: return RightEnabled;
                case Edge.Top: return TopEnabled;
                default: return BottomEnabled;
            }
        }

        public void SetEnabled(Edge edge, bool value)
        {
            switch (edge)
            {
                case Edge.Left: LeftEnabled = value; break;
                case Edge.Right: RightEnabled = value; break;
                case Edge.Top: TopEnabled = value; break;
                default: BottomEnabled = value; break;
            }
        }
    }

    internal static class SettingsStore
    {
        private static string FilePath
        {
            get { return Path.Combine(Log.DirectoryPath, "settings.json"); }
        }

        public static DockSettings Load()
        {
            var settings = new DockSettings();
            try
            {
                if (File.Exists(FilePath))
                {
                    DockSettings loaded = JsonSerializer.Deserialize<DockSettings>(File.ReadAllText(FilePath));
                    if (loaded != null)
                    {
                        settings = loaded;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Write(ex);
            }

            Normalize(settings);
            return settings;
        }

        public static void Save(DockSettings settings)
        {
            if (settings == null)
            {
                return;
            }

            try
            {
                Normalize(settings);
                Directory.CreateDirectory(Log.DirectoryPath);
                File.WriteAllText(
                    FilePath,
                    JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex)
            {
                Log.Write(ex);
            }
        }

        private static void Normalize(DockSettings settings)
        {
            settings.SetThickness(Edge.Left, settings.LeftThickness);
            settings.SetThickness(Edge.Right, settings.RightThickness);
            settings.SetThickness(Edge.Top, settings.TopThickness);
            settings.SetThickness(Edge.Bottom, settings.BottomThickness);

            NormalizeSlots(settings);

            if (settings.ThemeMode < 0 || settings.ThemeMode > 2)
            {
                settings.ThemeMode = 0;
            }

            const int allowedModifiers =
                Win32.MOD_ALT | Win32.MOD_CONTROL | Win32.MOD_SHIFT | Win32.MOD_WIN;
            settings.HotkeyModifiers &= allowedModifiers;
            if (settings.HotkeyModifiers == 0)
            {
                settings.HotkeyModifiers = Win32.MOD_CONTROL | Win32.MOD_ALT;
            }

            if (settings.HotkeyKey <= 0 || settings.HotkeyKey > 0xFF)
            {
                settings.HotkeyKey = 0x20;
            }

            settings.Monitor = (settings.Monitor ?? "").Trim();
        }

        /// <summary>槽位必须是 1..SlotCount 的一个排列，坏掉就整体回到默认布局。</summary>
        private static void NormalizeSlots(DockSettings settings)
        {
            int[] slots = settings.Slots;
            if (slots == null || slots.Length != DockSettings.SlotCount)
            {
                settings.ResetSlots();
                return;
            }

            var seen = new bool[DockSettings.SlotCount + 1];
            foreach (int value in slots)
            {
                if (value < 1 || value > DockSettings.SlotCount || seen[value])
                {
                    settings.ResetSlots();
                    return;
                }

                seen[value] = true;
            }
        }
    }
}
