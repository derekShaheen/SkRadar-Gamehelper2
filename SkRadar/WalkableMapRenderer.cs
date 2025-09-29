namespace Radar
{
    using System;
    using System.Numerics;
    using System.Threading.Tasks;
    using GameHelper;
    using GameHelper.RemoteEnums;
    using GameHelper.RemoteObjects.Components;
    using GameHelper.Utils;
    using ImGuiNET;
    using SixLabors.ImageSharp;
    using SixLabors.ImageSharp.PixelFormats;
    using SixLabors.ImageSharp.Processing;
    using SixLabors.ImageSharp.Processing.Processors.Transforms;

    internal sealed class WalkableMapRenderer
    {
        private readonly Radar owner;
        private double miniMapDiagonalLength;
        private double largeMapDiagonalLength;
        private IntPtr walkableMapTexture = IntPtr.Zero;
        private Vector2 walkableMapDimension = Vector2.Zero;

        internal WalkableMapRenderer(Radar owner)
        {
            this.owner = owner;
        }

        internal double MiniMapDiagonalLength => this.miniMapDiagonalLength;

        internal double LargeMapDiagonalLength => this.largeMapDiagonalLength;

        internal void UpdateMiniMapDetails()
        {
            var map = Core.States.InGameStateObject.GameUi.MiniMap;
            var widthSq = map.Size.X * map.Size.X;
            var heightSq = map.Size.Y * map.Size.Y;
            this.miniMapDiagonalLength = Math.Sqrt(widthSq + heightSq);
        }

        internal void UpdateLargeMapDetails()
        {
            var map = Core.States.InGameStateObject.GameUi.LargeMap;
            var widthSq = map.Size.X * map.Size.X;
            var heightSq = map.Size.Y * map.Size.Y;
            this.largeMapDiagonalLength = Math.Sqrt(widthSq + heightSq);
        }

        internal void Draw(Vector2 mapCenter)
        {
            if (!this.owner.Settings.DrawWalkableMap || this.walkableMapTexture == IntPtr.Zero)
                return;

            var player = Core.States.InGameStateObject.CurrentAreaInstance.Player;
            if (!player.TryGetComponent<Render>(out var pRender))
                return;

            var rect = new RectangleF(
                -pRender.GridPosition.X,
                -pRender.GridPosition.Y,
                this.walkableMapDimension.X,
                this.walkableMapDimension.Y);

            var p1 = Helper.DeltaInWorldToMapDelta(new Vector2(rect.Left, rect.Top), -pRender.TerrainHeight);
            var p2 = Helper.DeltaInWorldToMapDelta(new Vector2(rect.Right, rect.Top), -pRender.TerrainHeight);
            var p3 = Helper.DeltaInWorldToMapDelta(new Vector2(rect.Right, rect.Bottom), -pRender.TerrainHeight);
            var p4 = Helper.DeltaInWorldToMapDelta(new Vector2(rect.Left, rect.Bottom), -pRender.TerrainHeight);
            p1 += mapCenter; p2 += mapCenter; p3 += mapCenter; p4 += mapCenter;

            if (this.owner.Settings.DrawMapInCull)
                ImGui.GetWindowDrawList().AddImageQuad(this.walkableMapTexture, p1, p2, p3, p4);
            else
                ImGui.GetBackgroundDrawList().AddImageQuad(this.walkableMapTexture, p1, p2, p3, p4);
        }

        internal void GenerateMapTexture()
        {
            if (Core.States.GameCurrentState is not (GameStateTypes.InGameState or GameStateTypes.EscapeState))
                return;

            var instance = Core.States.InGameStateObject.CurrentAreaInstance;
            var gridHeightData = instance.GridHeightData;
            var mapWalkableData = instance.GridWalkableData;
            var bytesPerRow = instance.TerrainMetadata.BytesPerRow;
            var worldToGridHeightMultiplier = instance.WorldToGridConvertor * 2f;
            if (bytesPerRow <= 0)
                return;

            var mapEdgeDetector = new MapEdgeDetector(mapWalkableData, bytesPerRow);
            var configuration = Configuration.Default.Clone();
            configuration.PreferContiguousImageBuffers = true;
            using Image<Rgba32> image = new(configuration, bytesPerRow * 2, mapEdgeDetector.TotalRows);
            Parallel.For(0, gridHeightData.Length, y =>
            {
                for (var x = 1; x < gridHeightData[y].Length - 1; x++)
                {
                    if (!mapEdgeDetector.IsBorder(x, y))
                        continue;

                    var height = (int)(gridHeightData[y][x] / worldToGridHeightMultiplier);
                    var imageX = x - height;
                    var imageY = y - height;

                    if (mapEdgeDetector.IsInsideMapBoundary(imageX, imageY))
                        image[imageX, imageY] = new Rgba32(this.owner.Settings.WalkableMapColor);
                }
            });

            this.walkableMapDimension = new Vector2(image.Width, image.Height);
            if (Math.Max(image.Width, image.Height) > 8192)
            {
                var (newWidth, newHeight) = (image.Width, image.Height);
                if (image.Height > image.Width)
                {
                    newWidth = newWidth * 8192 / newHeight;
                    newHeight = 8192;
                }
                else
                {
                    newHeight = newHeight * 8192 / newWidth;
                    newWidth = 8192;
                }

                var targetSize = new Size(newWidth, newHeight);
                var resizer = new ResizeProcessor(new ResizeOptions { Size = targetSize }, image.Size)
                    .CreatePixelSpecificCloningProcessor(configuration, image, image.Bounds);
                resizer.Execute();
            }

            Core.Overlay.AddOrGetImagePointer("walkable_map", image, false, out var t);
            this.walkableMapTexture = t;
        }

        internal void Reload()
        {
            this.RemoveMapTexture();
            this.GenerateMapTexture();
        }

        internal void RemoveMapTexture()
        {
            this.walkableMapTexture = IntPtr.Zero;
            this.walkableMapDimension = Vector2.Zero;
            Core.Overlay.RemoveImage("walkable_map");
        }

        internal void OnToggleDrawWalkableMap()
        {
            if (this.owner.Settings.DrawWalkableMap)
            {
                if (this.walkableMapTexture == IntPtr.Zero)
                    this.GenerateMapTexture();
            }
            else
            {
                this.RemoveMapTexture();
            }
        }

        internal void OnColorChanged()
        {
            if (this.walkableMapTexture != IntPtr.Zero)
                this.Reload();
        }

        internal void Reset()
        {
            this.RemoveMapTexture();
            this.miniMapDiagonalLength = 0;
            this.largeMapDiagonalLength = 0;
        }
    }
}

