﻿using ImGuiNET;

namespace BossMod
{
    public class SeparatorColumn : Timeline.Column
    {
        public uint Color;

        public SeparatorColumn(Timeline timeline, uint color = 0xffffffff, float width = 1)
            : base(timeline)
        {
            Width = width;
            Color = color;
        }

        public override void Draw()
        {
            var trackMin = Timeline.ColumnCoordsToScreenCoords(0, Timeline.MinVisibleTime);
            var trackMax = Timeline.ColumnCoordsToScreenCoords(Width, Timeline.MaxVisibleTime);
            ImGui.GetWindowDrawList().AddRectFilled(trackMin, trackMax, Color);
        }
    }
}
