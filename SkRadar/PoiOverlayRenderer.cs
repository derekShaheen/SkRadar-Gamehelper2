namespace Radar
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Numerics;
    using GameHelper;
    using GameHelper.RemoteObjects.Components;
    using GameHelper.RemoteObjects.States.InGameStateObjects;
    using GameHelper.Utils;
    using ImGuiNET;

    internal sealed class PoiOverlayRenderer
    {
        private readonly Radar owner;
        private FlowFieldPathfinder pathfinder;
        private readonly List<Vector2> pathBuffer = new(1024);
        private readonly List<(string Label, uint Color)> currentPathLabels = new();

        internal PoiOverlayRenderer(Radar owner)
        {
            this.owner = owner;
        }

        internal IReadOnlyList<(string Label, uint Color)> PathLabels => this.currentPathLabels;

        internal bool HasPathLabels => this.currentPathLabels.Count > 0;

        internal void BeginFrame()
        {
            this.currentPathLabels.Clear();
        }

        internal void Draw(Vector2 mapCenter, string areaName)
        {
            var area = Core.States.InGameStateObject.CurrentAreaInstance;
            if (!area.Player.TryGetComponent<Render>(out var playerRender))
                return;

            uint basePoiTextColor = ImGuiHelper.Color(
                (uint)(this.owner.Settings.POIColor.X * 255),
                (uint)(this.owner.Settings.POIColor.Y * 255),
                (uint)(this.owner.Settings.POIColor.Z * 255),
                (uint)(this.owner.Settings.POIColor.W * 255));

            ImDrawListPtr fgDraw = this.owner.Settings.DrawPOIInCull
                ? ImGui.GetWindowDrawList()
                : ImGui.GetBackgroundDrawList();

            var anchor = new Vector2(playerRender.GridPosition.X, playerRender.GridPosition.Y);

            if (this.owner.Settings.ShowPathsToPOI)
            {
                this.EnsurePathfinder(area);

                var labeledPOIs = new List<(Vector2 Pos, string Label, string Key)>(64);
                if (!string.IsNullOrEmpty(areaName) && this.owner.Settings.ImportantTgts.TryGetValue(areaName, out var areaTgts))
                    CollectLabeledPOIs(areaTgts, area, labeledPOIs);
                if (this.owner.Settings.ImportantTgts.TryGetValue("common", out var commonTgts))
                    CollectLabeledPOIs(commonTgts, area, labeledPOIs);

                if (this.owner.Settings.DrawOnlyNearestPOIPath && labeledPOIs.Count > 1)
                {
                    labeledPOIs.Sort((a, b) =>
                        Vector2.DistanceSquared(a.Pos, anchor).CompareTo(Vector2.DistanceSquared(b.Pos, anchor)));
                    labeledPOIs.RemoveRange(1, labeledPOIs.Count - 1);
                }

                foreach (var poi in labeledPOIs)
                {
                    int tx = (int)MathF.Round(poi.Pos.X);
                    int ty = (int)MathF.Round(poi.Pos.Y);

                    var snapped = FindClosestWalkable(area, tx, ty, 12) ?? (tx, ty);
                    var targetForField = (snapped.X, snapped.Y);

                    this.pathfinder.EnsureDirectionField(targetForField);

                    int sx = (int)MathF.Round(anchor.X);
                    int sy = (int)MathF.Round(anchor.Y);
                    this.pathBuffer.Clear();
                    bool havePath = this.pathfinder.TryGetPath((sx, sy), targetForField, this.pathBuffer, 16384);

                    if (!havePath)
                    {
                        var reachable = this.FindNearestReachableNearTarget(area, anchor, tx, ty);
                        if (reachable is (int rx, int ry))
                        {
                            targetForField = (rx, ry);
                            this.pathfinder.EnsureDirectionField(targetForField);
                            this.pathBuffer.Clear();
                            havePath = this.pathfinder.TryGetPath((sx, sy), targetForField, this.pathBuffer, 16384);
                        }
                    }

                    if (!havePath)
                        continue;

                    uint pathColor = this.owner.Settings.UseDistinctPathColors
                        ? DistinctColorForPointU32(targetForField.X, targetForField.Y, this.owner.Settings.PathThickness)
                        : basePoiTextColor;

                    DrawPathPolylineOnMap(
                        fgDraw,
                        mapCenter,
                        area,
                        playerRender,
                        anchor,
                        this.pathBuffer,
                        pathColor,
                        this.owner.Settings.PathThickness,
                        1.0f);

                    var end = this.pathBuffer.Count > 0 ? this.pathBuffer[^1] : new Vector2(targetForField.X, targetForField.Y);
                    float endHeight = HeightAt(area, (int)end.X, (int)end.Y);
                    var endDelta = Helper.DeltaInWorldToMapDelta(end - anchor, -playerRender.TerrainHeight + endHeight);
                    var endPoint = mapCenter + endDelta;
                    fgDraw.AddCircleFilled(endPoint, 3f, pathColor);

                    if (!this.currentPathLabels.Any(x => x.Label == poi.Label && x.Color == pathColor))
                        this.currentPathLabels.Add((poi.Label, pathColor));
                }
            }

            if (this.owner.Settings.ShowImportantPOI)
            {
                void drawString(string text, Vector2 location, Vector2 stringImGuiSize, bool drawBackground)
                {
                    float height = HeightAt(area, (int)location.X, (int)location.Y);
                    var offset = Helper.DeltaInWorldToMapDelta(location - anchor, -playerRender.TerrainHeight + height);
                    if (drawBackground)
                    {
                        fgDraw.AddRectFilled(
                            mapCenter + offset - stringImGuiSize,
                            mapCenter + offset + stringImGuiSize,
                            ImGuiHelper.Color(0, 0, 0, 200));
                    }

                    fgDraw.AddText(
                        ImGui.GetFont(),
                        ImGui.GetFontSize(),
                        mapCenter + offset - stringImGuiSize,
                        basePoiTextColor,
                        text);
                }

                if (!string.IsNullOrEmpty(areaName) && this.owner.Settings.ImportantTgts.TryGetValue(areaName, out var areaTiles))
                {
                    foreach (var tile in areaTiles)
                    {
                        if (!area.TgtTilesLocations.TryGetValue(tile.Key, out var locations))
                            continue;

                        var stringSize = ImGui.CalcTextSize(tile.Value) / 2;
                        for (var i = 0; i < locations.Count; i++)
                            drawString(tile.Value, locations[i], stringSize, this.owner.Settings.EnablePOIBackground);
                    }
                }

                if (this.owner.Settings.ImportantTgts.TryGetValue("common", out var commonTiles))
                {
                    foreach (var tile in commonTiles)
                    {
                        if (!area.TgtTilesLocations.TryGetValue(tile.Key, out var locations))
                            continue;

                        var stringSize = ImGui.CalcTextSize(tile.Value) / 2;
                        for (var i = 0; i < locations.Count; i++)
                            drawString(tile.Value, locations[i], stringSize, this.owner.Settings.EnablePOIBackground);
                    }
                }
            }
        }

        internal void OnAreaChanged(AreaInstance area)
        {
            this.pathfinder?.CancelAll();
            this.pathfinder = new FlowFieldPathfinder(area);
        }

        internal void Reset()
        {
            this.pathfinder?.CancelAll();
            this.pathfinder = null;
            this.currentPathLabels.Clear();
        }

        private void EnsurePathfinder(AreaInstance area)
        {
            if (this.pathfinder == null)
                this.pathfinder = new FlowFieldPathfinder(area);
        }

        private (int X, int Y)? FindNearestReachableNearTarget(
            AreaInstance area,
            Vector2 start,
            int tx,
            int ty,
            int maxRadius = 48,
            int samplesPerRing = 12)
        {
            var first = FindClosestWalkable(area, tx, ty, 48);
            if (first is (int fx, int fy))
            {
                this.pathfinder.EnsureDirectionField(first.Value);
                int sx = (int)MathF.Round(start.X);
                int sy = (int)MathF.Round(start.Y);
                this.pathBuffer.Clear();
                if (this.pathfinder.TryGetPath((sx, sy), first.Value, this.pathBuffer, 16384))
                    return first;
            }

            for (int r = 2; r <= maxRadius; r += 2)
            {
                int samples = Math.Clamp(samplesPerRing + r / 8, samplesPerRing, 32);
                for (int i = 0; i < samples; i++)
                {
                    float ang = (float)(i * (2 * Math.PI / samples));
                    int cx = tx + (int)MathF.Round(r * MathF.Cos(ang));
                    int cy = ty + (int)MathF.Round(r * MathF.Sin(ang));
                    var candidate = FindClosestWalkable(area, cx, cy, 8);
                    if (candidate is not (int gx, int gy))
                        continue;

                    var goal = (gx, gy);
                    this.pathfinder.EnsureDirectionField(goal);

                    int sx = (int)MathF.Round(start.X);
                    int sy = (int)MathF.Round(start.Y);
                    this.pathBuffer.Clear();
                    if (this.pathfinder.TryGetPath((sx, sy), goal, this.pathBuffer, 16384))
                        return goal;
                }
            }

            return null;
        }

        private static void CollectLabeledPOIs(Dictionary<string, string> dict, AreaInstance area, List<(Vector2 Pos, string Label, string Key)> output)
        {
            foreach (var kv in dict)
            {
                if (!area.TgtTilesLocations.TryGetValue(kv.Key, out var locations))
                    continue;

                string label = kv.Value;
                string key = kv.Key;
                for (int i = 0; i < locations.Count; i++)
                    output.Add((locations[i], label, key));
            }
        }

        private static bool IsWalkable(AreaInstance area, int x, int y)
        {
            var bytesPerRow = area.TerrainMetadata.BytesPerRow;
            if (bytesPerRow <= 0)
                return false;

            int width = bytesPerRow * 2;
            int height = area.GridWalkableData.Length / bytesPerRow;
            if ((uint)x >= (uint)width || (uint)y >= (uint)height)
                return false;

            int idx = y * bytesPerRow + (x >> 1);
            byte b = area.GridWalkableData[idx];
            int nibble = ((x & 1) == 0) ? (b & 0xF) : ((b >> 4) & 0xF);
            return nibble > 0;
        }

        private static (int X, int Y)? FindClosestWalkable(AreaInstance area, int x, int y, int maxRadius = 12)
        {
            if (IsWalkable(area, x, y))
                return (x, y);

            for (int r = 1; r <= maxRadius; r++)
            {
                int left = x - r, right = x + r, top = y - r, bottom = y + r;
                for (int i = 0; i <= 2 * r; i++)
                {
                    int gx1 = left + i, gy1 = top;
                    int gx2 = left + i, gy2 = bottom;
                    int gx3 = left, gy3 = top + i;
                    int gx4 = right, gy4 = top + i;

                    if (IsWalkable(area, gx1, gy1)) return (gx1, gy1);
                    if (IsWalkable(area, gx2, gy2)) return (gx2, gy2);
                    if (IsWalkable(area, gx3, gy3)) return (gx3, gy3);
                    if (IsWalkable(area, gx4, gy4)) return (gx4, gy4);
                }
            }

            return null;
        }

        private static float HeightAt(AreaInstance area, int x, int y)
        {
            var grid = area.GridHeightData;
            if ((uint)y < (uint)grid.Length && (uint)x < (uint)grid[y].Length)
                return grid[y][x];
            return 0f;
        }

        private static void DrawPathPolylineOnMap(
            ImDrawListPtr drawList,
            Vector2 mapCenter,
            AreaInstance area,
            Render playerRender,
            Vector2 anchor,
            List<Vector2> path,
            uint color,
            float thickness,
            float trimDistance)
        {
            if (path.Count == 0)
                return;

            if (path.Count == 1)
            {
                var node = path[0];
                float height = HeightAt(area, (int)node.X, (int)node.Y);
                var start = mapCenter + Helper.DeltaInWorldToMapDelta(anchor - anchor, 0);
                var end = mapCenter + Helper.DeltaInWorldToMapDelta(node - anchor, -playerRender.TerrainHeight + height);
                drawList.AddLine(start, end, color, Math.Max(1f, thickness));
                return;
            }

            int startIndex = 0;
            if (trimDistance > 0f)
            {
                for (int i = 0; i < path.Count; i++)
                {
                    if (Vector2.Distance(anchor, path[i]) >= trimDistance)
                    {
                        startIndex = Math.Max(0, i - 1);
                        break;
                    }
                }
            }

            Vector2? last = null;
            for (int i = startIndex; i < path.Count; i += 2)
            {
                var node = path[i];
                float height = HeightAt(area, (int)node.X, (int)node.Y);
                var point = mapCenter + Helper.DeltaInWorldToMapDelta(node - anchor, -playerRender.TerrainHeight + height);
                if (last is Vector2 prev)
                    drawList.AddLine(prev, point, color, thickness);
                last = point;
            }

            if (last is Vector2 lastPoint)
            {
                var endNode = path[^1];
                float endHeight = HeightAt(area, (int)endNode.X, (int)endNode.Y);
                var endPoint = mapCenter + Helper.DeltaInWorldToMapDelta(endNode - anchor, -playerRender.TerrainHeight + endHeight);
                drawList.AddLine(lastPoint, endPoint, color, thickness);
            }
        }

        private static uint DistinctColorForPointU32(int x, int y, float thickness)
        {
            unchecked
            {
                uint h = (uint)(x * 73856093) ^ (uint)(y * 19349663);
                float hue = (h % 360u) / 360f;
                var rgba = HsvToRgb(hue, 0.85f, 1.0f, 0.95f);
                return ImGui.ColorConvertFloat4ToU32(rgba);
            }
        }

        private static Vector4 HsvToRgb(float h, float s, float v, float a = 1f)
        {
            float i = MathF.Floor(h * 6f);
            float f = h * 6f - i;
            float p = v * (1f - s);
            float q = v * (1f - f * s);
            float t = v * (1f - (1f - f) * s);

            return (((int)i) % 6) switch
            {
                0 => new Vector4(v, t, p, a),
                1 => new Vector4(q, v, p, a),
                2 => new Vector4(p, v, t, a),
                3 => new Vector4(p, q, v, a),
                4 => new Vector4(t, p, v, a),
                _ => new Vector4(v, p, q, a),
            };
        }
    }
}

