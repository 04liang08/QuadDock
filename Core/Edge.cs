namespace QuadDock.Core
{
    internal enum Edge
    {
        Left = 0,
        Top = 1,
        Right = 2,
        Bottom = 3
    }

    internal static class EdgeExtensions
    {
        public static bool IsVertical(this Edge edge)
        {
            return edge == Edge.Left || edge == Edge.Right;
        }

        public static string Label(this Edge edge)
        {
            switch (edge)
            {
                case Edge.Left: return "左侧栏";
                case Edge.Right: return "右侧栏";
                case Edge.Top: return "上侧栏";
                default: return "下侧栏";
            }
        }
    }
}
