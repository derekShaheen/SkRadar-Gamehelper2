namespace Radar
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Numerics;
    using System.Threading.Tasks;
    using Coroutine;
    using GameHelper;
    using GameHelper.CoroutineEvents;
    using GameHelper.Plugin;
    using GameHelper.RemoteEnums;
    using GameHelper.RemoteEnums.Entity;
    using GameHelper.RemoteObjects.Components;
    using GameHelper.Utils;
    using ImGuiNET;
    using Newtonsoft.Json;
    using SixLabors.ImageSharp;
    using SixLabors.ImageSharp.PixelFormats;
    using SixLabors.ImageSharp.Processing.Processors.Transforms;
    using SixLabors.ImageSharp.Processing;

    /// <summary>
    /// <see cref="Radar"/> plugin.
    /// </summary>
    public sealed class Radar : PCore<RadarSettings>
    {
        private readonly string delveChestStarting = "Metadata/Chests/DelveChests/";
        private readonly Dictionary<uint, string> delveChestCache = new();

        private bool skipOneSettingChange = false; // kept if you use it elsewhere; otherwise safe to remove
        private bool isAddNewPOIHeaderOpened = false;
        private ActiveCoroutine onMove;
        private ActiveCoroutine onForegroundChange;
        private ActiveCoroutine onGameClose;
        private ActiveCoroutine onAreaChange;

        private string currentAreaName = string.Empty;
        private string tmpTileName = string.Empty;
        private string tmpDisplayName = string.Empty;
        private int tmpTgtSelectionCounter = 0;
        private string tmpTileFilter = string.Empty;
        private bool addTileForAllAreas = false;

        // --- POI Paths window state ---
        private Vector2 _poiWindowPos = new Vector2(20f, 120f);
        private bool _poiWindowPosSet = false;

        private double miniMapDiagonalLength = 0x00;
        private double largeMapDiagonalLength = 0x00;

        private IntPtr walkableMapTexture = IntPtr.Zero;
        private Vector2 walkableMapDimension = Vector2.Zero;

        private string SettingPathname => Path.Join(this.DllDirectory, "config", "settings.txt");
        private string ImportantTgtPathName => Path.Join(this.DllDirectory, "important_tgt_files.txt");

        // --- Pathing/POI drawing state ---
        private FlowFieldPathfinder pathfinder;                 // Provided in a separate file (not included here)
        private readonly List<Vector2> _pathBuffer = new(1024); // reused per path to reduce allocs

        // UI: path labels for currently drawn paths
        private readonly List<(string Label, uint Color)> _currentPathLabels = new();

        /// <inheritdoc/>
        public override void DrawSettings()
        {
            ImGui.TextWrapped("If your mini/large map icon are not working/visible. Open this " +
                "setting window, click anywhere on it and then hide this setting window. It will fix the issue.");
            ImGui.DragFloat("Large Map Fix", ref this.Settings.LargeMapScaleMultiplier, 0.001f, 0.01f, 0.3f);
            ImGuiHelper.ToolTip("Fix large map (icons) offset per resolution. Only change when resolution changes.");

            ImGui.Checkbox("Hide Radar when in Hideout/Town", ref this.Settings.DrawWhenNotInHideoutOrTown);
            ImGui.Checkbox("Hide Radar when game is in the background", ref this.Settings.DrawWhenForeground);
            ImGui.Checkbox("Hide Radar when game is paused", ref this.Settings.DrawWhenNotPaused);

            ImGui.Separator();
            ImGui.NewLine();

            if (ImGui.Checkbox("Draw Area/Zone Map (maphack)", ref this.Settings.DrawWalkableMap))
            {
                if (this.Settings.DrawWalkableMap)
                {
                    if (this.walkableMapTexture == IntPtr.Zero)
                        this.ReloadMapTexture();
                }
                else
                {
                    this.RemoveMapTexture();
                }
            }

            if (ImGui.ColorEdit4("Drawn Map Color", ref this.Settings.WalkableMapColor))
            {
                if (this.walkableMapTexture != IntPtr.Zero)
                    this.ReloadMapTexture();
            }

            ImGui.Separator();
            ImGui.NewLine();

            ImGui.Checkbox("Show terrain points of interest (A.K.A Terrain POI)", ref this.Settings.ShowImportantPOI);
            ImGui.ColorEdit4("Terrain POI text color", ref this.Settings.POIColor);
            ImGui.Checkbox("Add black background to Terrain POI text", ref this.Settings.EnablePOIBackground);

            ImGui.Separator();
            ImGui.Text("POI Path Options");
            ImGui.Checkbox("Show Paths to POIs", ref this.Settings.ShowPathsToPOI);
            ImGui.Checkbox("Only Nearest POI", ref this.Settings.DrawOnlyNearestPOIPath);
            ImGui.Checkbox("Distinct Path Colors", ref this.Settings.UseDistinctPathColors);
            ImGui.SliderFloat("Path Thickness", ref this.Settings.PathThickness, 1f, 6f);

            // Smoothing UI was previously removed per your request

            ImGui.Separator();
            ImGui.NewLine();

            ImGui.Checkbox("Hide Entities outside the network bubble", ref this.Settings.HideOutsideNetworkBubble);
            ImGui.Checkbox("Show Player Names", ref this.Settings.ShowPlayersNames);
            ImGuiHelper.ToolTip("This button will not work while Player is in the Scourge.");

            if (ImGui.CollapsingHeader("Icons Setting"))
            {
                this.Settings.DrawIconsSettingToImGui(
                    "BaseGame Icons",
                    this.Settings.BaseIcons,
                    "Blockages icon can be set from Delve Icons category i.e. 'Blockage OR DelveWall'");

                this.Settings.DrawPOIMonsterSettingToImGui(this.DllDirectory);
                this.Settings.OtherImportantObjectsSettingToImGui(this.DllDirectory);
                this.Settings.DrawIconsSettingToImGui(
                    "Breach Icons",
                    this.Settings.BreachIcons,
                    "Breach bosses are same as BaseGame Icons -> Unique Monsters.");

                this.Settings.DrawIconsSettingToImGui(
                    "Delirium Icons",
                    this.Settings.DeliriumIcons,
                    string.Empty);

                this.Settings.DrawIconsSettingToImGui(
                    "Expedition Icons",
                    this.Settings.ExpeditionIcons,
                    string.Empty);
            }
        }

        /// <inheritdoc/>
        public override void DrawUI()
        {
            var largeMap = Core.States.InGameStateObject.GameUi.LargeMap;
            var miniMap = Core.States.InGameStateObject.GameUi.MiniMap;
            var areaDetails = Core.States.InGameStateObject.CurrentWorldInstance.AreaDetails;

            if (this.Settings.DrawWhenNotPaused && Core.States.GameCurrentState != GameStateTypes.InGameState)
                return;

            if (Core.States.GameCurrentState is not (GameStateTypes.InGameState or GameStateTypes.EscapeState))
                return;

            if (this.Settings.DrawWhenForeground && !Core.Process.Foreground)
                return;

            if (this.Settings.DrawWhenNotInHideoutOrTown &&
                (areaDetails.IsHideout || areaDetails.IsTown))
                return;

            if (Core.States.InGameStateObject.GameUi.SkillTreeNodesUiElements.Count > 0)
                return;

            // Clear labels each frame; will be repopulated when paths are drawn
            _currentPathLabels.Clear();

            if (largeMap.IsVisible)
            {
                var largeMapRealCenter = largeMap.Center + largeMap.Shift + largeMap.DefaultShift;
                var largeMapModifiedZoom = this.Settings.LargeMapScaleMultiplier * largeMap.Zoom;
                Helper.DiagonalLength = this.largeMapDiagonalLength;
                Helper.Scale = largeMapModifiedZoom;

                // Always use a full-screen transparent window as the culling region
                ImGui.SetNextWindowPos(Vector2.Zero, ImGuiCond.Always);
                ImGui.SetNextWindowSize(new Vector2(Core.Process.WindowArea.Size.Width, Core.Process.WindowArea.Size.Height), ImGuiCond.Always);
                ImGui.SetNextWindowBgAlpha(0f);
                ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
                ImGui.Begin("###FullScreenCull", ImGuiHelper.TransparentWindowFlags);
                ImGui.PopStyleVar();

                this.DrawLargeMap(largeMapRealCenter);
                this.DrawTgtFiles(largeMapRealCenter);      // includes path rendering + labels collection
                this.DrawMapIcons(largeMapRealCenter, largeMapModifiedZoom * 5f);

                ImGui.End();
            }

            if (miniMap.IsVisible)
            {
                Helper.DiagonalLength = this.miniMapDiagonalLength;
                Helper.Scale = miniMap.Zoom;
                var miniMapCenter = miniMap.Postion +
                    (miniMap.Size / 2) +
                    miniMap.DefaultShift +
                    miniMap.Shift;

                ImGui.SetNextWindowPos(miniMap.Postion);
                ImGui.SetNextWindowSize(miniMap.Size);
                ImGui.SetNextWindowBgAlpha(0f);
                ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
                ImGui.Begin("###minimapRadar", ImGuiHelper.TransparentWindowFlags);
                ImGui.PopStyleVar();

                // Only icons in the minimap window to keep it cheap
                this.DrawMapIcons(miniMapCenter, miniMap.Zoom);

                ImGui.End();
            }

            // ---- POI Paths Window (movable, persists position) ----
            if (this.Settings.ShowPathsToPOI && _currentPathLabels.Count > 0)
            {
                // Only apply the saved position when the window appears (so user can still drag it).
                ImGui.SetNextWindowPos(_poiWindowPos, ImGuiCond.Appearing);
                ImGui.SetNextWindowSizeConstraints(new Vector2(200f, 100f), new Vector2(600f, 800f));

                if (ImGui.Begin("POI Paths")) // default flags allow moving
                {
                    // Remember current position every frame so we can restore it after area changes.
                    _poiWindowPos = ImGui.GetWindowPos();
                    _poiWindowPosSet = true;

                    foreach (var (label, colorU32) in _currentPathLabels)
                    {
                        var col = ImGui.ColorConvertU32ToFloat4(colorU32);
                        ImGui.PushStyleColor(ImGuiCol.Text, col);
                        ImGui.TextUnformatted(label);
                        ImGui.PopStyleColor();
                    }
                }
                ImGui.End();
            }

        }

        /// <inheritdoc/>
        public override void OnDisable()
        {
            this.onMove?.Cancel();
            this.onForegroundChange?.Cancel();
            this.onGameClose?.Cancel();
            this.onAreaChange?.Cancel();
            this.onMove = null;
            this.onForegroundChange = null;
            this.onGameClose = null;
            this.onAreaChange = null;
            this.CleanUpRadarPluginCaches();
            this.pathfinder?.CancelAll();
            this.pathfinder = null;
            _currentPathLabels.Clear();
        }

        /// <inheritdoc/>
        public override void OnEnable(bool isGameOpened)
        {
            if (!isGameOpened)
                this.skipOneSettingChange = true;

            if (File.Exists(this.SettingPathname))
            {
                var content = File.ReadAllText(this.SettingPathname);
                this.Settings = JsonConvert.DeserializeObject<RadarSettings>(content);
            }

            if (File.Exists(this.ImportantTgtPathName))
            {
                var tgtfiles = File.ReadAllText(this.ImportantTgtPathName);
                this.Settings.ImportantTgts = JsonConvert.DeserializeObject
                    <Dictionary<string, Dictionary<string, string>>>(tgtfiles);
            }

            this.Settings.AddDefaultIcons(this.DllDirectory);

            this.onMove = CoroutineHandler.Start(this.OnMove());
            this.onForegroundChange = CoroutineHandler.Start(this.OnForegroundChange());
            this.onGameClose = CoroutineHandler.Start(this.OnClose());
            this.onAreaChange = CoroutineHandler.Start(this.ClearCachesAndUpdateAreaInfo());
            this.GenerateMapTexture();
        }

        /// <inheritdoc/>
        public override void SaveSettings()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(this.SettingPathname));
            var settingsData = JsonConvert.SerializeObject(this.Settings, Formatting.Indented);
            File.WriteAllText(this.SettingPathname, settingsData);

            if (this.Settings.ImportantTgts.Count > 0)
            {
                var tgtfiles = JsonConvert.SerializeObject(
                    this.Settings.ImportantTgts, Formatting.Indented);
                File.WriteAllText(this.ImportantTgtPathName, tgtfiles);
            }
        }

        private void DrawLargeMap(Vector2 mapCenter)
        {
            if (!this.Settings.DrawWalkableMap || this.walkableMapTexture == IntPtr.Zero)
                return;

            var player = Core.States.InGameStateObject.CurrentAreaInstance.Player;
            if (!player.TryGetComponent<Render>(out var pRender))
                return;

            var rectf = new RectangleF(
                -pRender.GridPosition.X,
                -pRender.GridPosition.Y,
                this.walkableMapDimension.X,
                this.walkableMapDimension.Y);

            var p1 = Helper.DeltaInWorldToMapDelta(new Vector2(rectf.Left, rectf.Top), -pRender.TerrainHeight);
            var p2 = Helper.DeltaInWorldToMapDelta(new Vector2(rectf.Right, rectf.Top), -pRender.TerrainHeight);
            var p3 = Helper.DeltaInWorldToMapDelta(new Vector2(rectf.Right, rectf.Bottom), -pRender.TerrainHeight);
            var p4 = Helper.DeltaInWorldToMapDelta(new Vector2(rectf.Left, rectf.Bottom), -pRender.TerrainHeight);
            p1 += mapCenter; p2 += mapCenter; p3 += mapCenter; p4 += mapCenter;

            if (this.Settings.DrawMapInCull)
                ImGui.GetWindowDrawList().AddImageQuad(this.walkableMapTexture, p1, p2, p3, p4);
            else
                ImGui.GetBackgroundDrawList().AddImageQuad(this.walkableMapTexture, p1, p2, p3, p4);
        }

        private void DrawTgtFiles(Vector2 mapCenter)
        {
            var area = Core.States.InGameStateObject.CurrentAreaInstance;
            if (!area.Player.TryGetComponent<Render>(out var playerRender))
                return;

            // base POI text color (used if distinct colors disabled)
            uint basePoiTextColor = ImGuiHelper.Color(
                (uint)(this.Settings.POIColor.X * 255),
                (uint)(this.Settings.POIColor.Y * 255),
                (uint)(this.Settings.POIColor.Z * 255),
                (uint)(this.Settings.POIColor.W * 255));

            ImDrawListPtr fgDraw = this.Settings.DrawPOIInCull
                ? ImGui.GetWindowDrawList()
                : ImGui.GetBackgroundDrawList();

            // Raw player grid
            var anchor = new Vector2(playerRender.GridPosition.X, playerRender.GridPosition.Y);

            // ----- PATHS -----
            if (this.Settings.ShowPathsToPOI)
            {
                if (this.pathfinder == null)
                    this.pathfinder = new FlowFieldPathfinder(area);

                // Collect labeled POIs: (location, displayText, key)
                var labeledPOIs = new List<(Vector2 Pos, string Label, string Key)>(64);
                if (this.Settings.ImportantTgts.TryGetValue(this.currentAreaName, out var areaTgts))
                    CollectLabeledPOIs(areaTgts, area, labeledPOIs);
                if (this.Settings.ImportantTgts.TryGetValue("common", out var commonTgts))
                    CollectLabeledPOIs(commonTgts, area, labeledPOIs);

                // Optionally reduce to nearest single POI instance
                if (this.Settings.DrawOnlyNearestPOIPath && labeledPOIs.Count > 1)
                {
                    labeledPOIs.Sort((a, b) =>
                        Vector2.DistanceSquared(a.Pos, anchor).CompareTo(Vector2.DistanceSquared(b.Pos, anchor)));
                    labeledPOIs = new List<(Vector2, string, string)> { labeledPOIs[0] };
                }

                // Draw each path + register label/color for the side window
                foreach (var poi in labeledPOIs)
                {
                    int tx = (int)MathF.Round(poi.Pos.X);
                    int ty = (int)MathF.Round(poi.Pos.Y);

                    // Snap target to nearest walkable if needed
                    var snapped = FindClosestWalkable(area, tx, ty, 12) ?? (tx, ty);
                    var targetForField = (snapped.X, snapped.Y);

                    // Ensure flow field exists
                    this.pathfinder.EnsureDirectionField(targetForField);

                    // Path from anchor to target
                    int sx = (int)MathF.Round(anchor.X);
                    int sy = (int)MathF.Round(anchor.Y);
                    _pathBuffer.Clear();
                    bool havePath = this.pathfinder.TryGetPath((sx, sy), targetForField, _pathBuffer, 16384);

                    if (!havePath)
                    {
                        // Try to find a reachable proxy near the POI
                        var reachable = FindNearestReachableNearTarget(area, this.pathfinder, anchor, tx, ty);
                        if (reachable is (int rx, int ry))
                        {
                            targetForField = (rx, ry);
                            // Ensure field for the new goal, then try again
                            this.pathfinder.EnsureDirectionField(targetForField);
                            _pathBuffer.Clear();
                            havePath = this.pathfinder.TryGetPath((sx, sy), targetForField, _pathBuffer, 16384);
                        }
                    }

                    if (!havePath)
                        continue; // still unreachable; skip drawing

                    // Choose color
                    uint pathColorU32 = this.Settings.UseDistinctPathColors
                        ? DistinctColorForPointU32(targetForField.X, targetForField.Y, this.Settings.PathThickness)
                        : basePoiTextColor;

                    // Draw the path
                    DrawPathPolylineOnMap(
                        fgDraw, mapCenter, area, playerRender, anchor,
                        _pathBuffer, pathColorU32, this.Settings.PathThickness, 1.0f);

                    // Endpoint marker
                    var end = _pathBuffer.Count > 0 ? _pathBuffer[^1] : new Vector2(targetForField.X, targetForField.Y);
                    float eh = HeightAt(area, (int)end.X, (int)end.Y);
                    var endDelta = Helper.DeltaInWorldToMapDelta(end - anchor, -playerRender.TerrainHeight + eh);
                    var endPt = mapCenter + endDelta;
                    fgDraw.AddCircleFilled(endPt, 3f, pathColorU32);

                    // Register label for side window (dedupe identical label/color pairs for cleanliness)
                    if (!_currentPathLabels.Any(x => x.Label == poi.Label && x.Color == pathColorU32))
                        _currentPathLabels.Add((poi.Label, pathColorU32));
                }
            }

            // ----- POI TEXTS ON MAP (unchanged) -----
            uint col = basePoiTextColor;

            void drawString(string text, Vector2 location, Vector2 stringImGuiSize, bool drawBackground)
            {
                float height = HeightAt(area, (int)location.X, (int)location.Y);
                var fpos = Helper.DeltaInWorldToMapDelta(location - anchor, -playerRender.TerrainHeight + height);
                if (drawBackground)
                {
                    fgDraw.AddRectFilled(
                        mapCenter + fpos - stringImGuiSize,
                        mapCenter + fpos + stringImGuiSize,
                        ImGuiHelper.Color(0, 0, 0, 200));
                }

                fgDraw.AddText(
                    ImGui.GetFont(),
                    ImGui.GetFontSize(),
                    mapCenter + fpos - stringImGuiSize,
                    col,
                    text);
            }
            
            if (this.Settings.ShowImportantPOI)
            {
                if (this.Settings.ImportantTgts.TryGetValue(this.currentAreaName, out var importantTgtsOfCurrentArea))
                {
                    foreach (var tile in importantTgtsOfCurrentArea)
                    {
                        if (area.TgtTilesLocations.TryGetValue(tile.Key, out var locations))
                        {
                            var strSize = ImGui.CalcTextSize(tile.Value) / 2;
                            for (var i = 0; i < locations.Count; i++)
                                drawString(tile.Value, locations[i], strSize, this.Settings.EnablePOIBackground);
                        }
                    }
                }

                if (this.Settings.ImportantTgts.TryGetValue("common", out var importantTgtsOfAllAreas))
                {
                    foreach (var tile in importantTgtsOfAllAreas)
                    {
                        if (area.TgtTilesLocations.TryGetValue(tile.Key, out var locations))
                        {
                            var strSize = ImGui.CalcTextSize(tile.Value) / 2;
                            for (var i = 0; i < locations.Count; i++)
                                drawString(tile.Value, locations[i], strSize, this.Settings.EnablePOIBackground);
                        }
                    }
                }
            }
        }

        private void DrawMapIcons(Vector2 mapCenter, float iconSizeMultiplier)
        {
            var fgDraw = ImGui.GetWindowDrawList();
            var currentAreaInstance = Core.States.InGameStateObject.CurrentAreaInstance;
            if (!currentAreaInstance.Player.TryGetComponent<Render>(out var playerRender))
                return;

            var pPos = new Vector2(playerRender.GridPosition.X, playerRender.GridPosition.Y);
            foreach (var entity in currentAreaInstance.AwakeEntities)
            {
                if (this.Settings.HideOutsideNetworkBubble && !entity.Value.IsValid)
                    continue;

                if (entity.Value.EntityState == EntityStates.Useless)
                    continue;

                if (!entity.Value.TryGetComponent<Render>(out var entityRender))
                    continue;

                var ePos = new Vector2(entityRender.GridPosition.X, entityRender.GridPosition.Y);
                var fpos = Helper.DeltaInWorldToMapDelta(ePos - pPos, entityRender.TerrainHeight - playerRender.TerrainHeight);
                var iconSizeMultiplierVector = Vector2.One * iconSizeMultiplier;

                switch (entity.Value.EntityType)
                {
                    case EntityTypes.NPC:
                        {
                            var npcIcon = entity.Value.EntitySubtype switch
                            {
                                EntitySubtypes.SpecialNPC => this.Settings.BaseIcons["Special NPC"],
                                _ => this.Settings.BaseIcons["NPC"],
                            };
                            iconSizeMultiplierVector *= npcIcon.IconScale;
                            fgDraw.AddImage(
                                npcIcon.TexturePtr,
                                mapCenter + fpos - iconSizeMultiplierVector,
                                mapCenter + fpos + iconSizeMultiplierVector,
                                npcIcon.UV0,
                                npcIcon.UV1);
                        }
                        break;

                    case EntityTypes.Player:
                        if (entity.Value.EntitySubtype == EntitySubtypes.PlayerOther)
                        {
                            entity.Value.TryGetComponent<Player>(out var playerComp);
                            if (this.Settings.ShowPlayersNames)
                            {
                                var pNameSizeH = ImGui.CalcTextSize(playerComp.Name) / 2;
                                fgDraw.AddRectFilled(mapCenter + fpos - pNameSizeH, mapCenter + fpos + pNameSizeH,
                                    ImGuiHelper.Color(0, 0, 0, 200));
                                fgDraw.AddText(ImGui.GetFont(), ImGui.GetFontSize(), mapCenter + fpos - pNameSizeH,
                                    ImGuiHelper.Color(255, 128, 128, 255), playerComp.Name);
                            }
                            else
                            {
                                var playerIcon = entity.Value.EntityState == EntityStates.PlayerLeader
                                    ? this.Settings.BaseIcons["Leader"]
                                    : this.Settings.BaseIcons["Player"];
                                iconSizeMultiplierVector *= playerIcon.IconScale;
                                fgDraw.AddImage(
                                    playerIcon.TexturePtr,
                                    mapCenter + fpos - iconSizeMultiplierVector,
                                    mapCenter + fpos + iconSizeMultiplierVector,
                                    playerIcon.UV0,
                                    playerIcon.UV1);
                            }
                        }
                        else
                        {
                            var playerIcon = this.Settings.BaseIcons["Self"];
                            iconSizeMultiplierVector *= playerIcon.IconScale;
                            fgDraw.AddImage(
                                playerIcon.TexturePtr,
                                mapCenter + fpos - iconSizeMultiplierVector,
                                mapCenter + fpos + iconSizeMultiplierVector,
                                playerIcon.UV0,
                                playerIcon.UV1);
                        }
                        break;

                    case EntityTypes.Chest:
                        {
                            IconPicker chestIcon;
                            switch (entity.Value.EntitySubtype)
                            {
                                case EntitySubtypes.None:
                                    chestIcon = this.Settings.BaseIcons["All Other Chest"]; break;
                                case EntitySubtypes.ChestWithRareRarity:
                                    chestIcon = this.Settings.BaseIcons["Rare Chests"]; break;
                                case EntitySubtypes.ChestWithMagicRarity:
                                    chestIcon = this.Settings.BaseIcons["Magic Chests"]; break;
                                case EntitySubtypes.ExpeditionChest:
                                    chestIcon = this.Settings.ExpeditionIcons["Generic Expedition Chests"]; break;
                                case EntitySubtypes.BreachChest:
                                    chestIcon = this.Settings.BreachIcons["Breach Chest"]; break;
                                case EntitySubtypes.Strongbox:
                                    chestIcon = this.Settings.BaseIcons["Strongbox"]; break;
                                default:
                                    chestIcon = this.Settings.BaseIcons["All Other Chest"]; break;
                            }
                            iconSizeMultiplierVector *= chestIcon.IconScale;
                            fgDraw.AddImage(
                                chestIcon.TexturePtr,
                                mapCenter + fpos - iconSizeMultiplierVector,
                                mapCenter + fpos + iconSizeMultiplierVector,
                                chestIcon.UV0,
                                chestIcon.UV1);
                        }
                        break;

                    case EntityTypes.Shrine:
                        if ((entity.Value.TryGetComponent<Shrine>(out var shrineComp) && shrineComp.IsUsed) ||
                            (entity.Value.TryGetComponent<Targetable>(out var targ) && !targ.IsTargetable))
                        {
                            // do not draw used shrines
                            break;
                        }
                        {
                            var shrineIcon = this.Settings.BaseIcons["Shrine"];
                            iconSizeMultiplierVector *= shrineIcon.IconScale;
                            fgDraw.AddImage(
                                shrineIcon.TexturePtr,
                                mapCenter + fpos - iconSizeMultiplierVector,
                                mapCenter + fpos + iconSizeMultiplierVector,
                                shrineIcon.UV0,
                                shrineIcon.UV1);
                        }
                        break;

                    case EntityTypes.Monster:
                        switch (entity.Value.EntityState)
                        {
                            case EntityStates.None:
                                if (entity.Value.EntitySubtype == EntitySubtypes.POIMonster)
                                {
                                    if (!this.Settings.POIMonsters.TryGetValue(entity.Value.EntityCustomGroup, out var poiIcon))
                                        poiIcon = this.Settings.POIMonsters[-1];

                                    iconSizeMultiplierVector *= poiIcon.IconScale;
                                    fgDraw.AddImage(
                                        poiIcon.TexturePtr,
                                        mapCenter + fpos - iconSizeMultiplierVector,
                                        mapCenter + fpos + iconSizeMultiplierVector,
                                        poiIcon.UV0,
                                        poiIcon.UV1);
                                }
                                else if (entity.Value.TryGetComponent<ObjectMagicProperties>(out var omp))
                                {
                                    var monsterIcon = this.RarityToIconMapping(omp.Rarity);
                                    iconSizeMultiplierVector *= monsterIcon.IconScale;
                                    fgDraw.AddImage(
                                        monsterIcon.TexturePtr,
                                        mapCenter + fpos - iconSizeMultiplierVector,
                                        mapCenter + fpos + iconSizeMultiplierVector,
                                        monsterIcon.UV0,
                                        monsterIcon.UV1);
                                }
                                break;

                            case EntityStates.PinnacleBossHidden:
                                {
                                    var bossNotAttackingIcon = this.Settings.BaseIcons["Pinnacle Boss Not Attackable"];
                                    iconSizeMultiplierVector *= bossNotAttackingIcon.IconScale;
                                    fgDraw.AddImage(
                                        bossNotAttackingIcon.TexturePtr,
                                        mapCenter + fpos - iconSizeMultiplierVector,
                                        mapCenter + fpos + iconSizeMultiplierVector,
                                        bossNotAttackingIcon.UV0,
                                        bossNotAttackingIcon.UV1);
                                }
                                break;

                            case EntityStates.MonsterFriendly:
                                {
                                    var friendlyIcon = this.Settings.BaseIcons["Friendly"];
                                    iconSizeMultiplierVector *= friendlyIcon.IconScale;
                                    fgDraw.AddImage(
                                        friendlyIcon.TexturePtr,
                                        mapCenter + fpos - iconSizeMultiplierVector,
                                        mapCenter + fpos + iconSizeMultiplierVector,
                                        friendlyIcon.UV0,
                                        friendlyIcon.UV1);
                                }
                                break;
                        }
                        break;

                    case EntityTypes.DeliriumBomb:
                        {
                            var dHiddenMIcon = this.Settings.DeliriumIcons["Delirium Bomb"];
                            iconSizeMultiplierVector *= dHiddenMIcon.IconScale;
                            fgDraw.AddImage(
                                dHiddenMIcon.TexturePtr,
                                mapCenter + fpos - iconSizeMultiplierVector,
                                mapCenter + fpos + iconSizeMultiplierVector,
                                dHiddenMIcon.UV0,
                                dHiddenMIcon.UV1);
                        }
                        break;

                    case EntityTypes.DeliriumSpawner:
                        {
                            var dHiddenMIcon = this.Settings.DeliriumIcons["Delirium Spawner"];
                            iconSizeMultiplierVector *= dHiddenMIcon.IconScale;
                            fgDraw.AddImage(
                                dHiddenMIcon.TexturePtr,
                                mapCenter + fpos - iconSizeMultiplierVector,
                                mapCenter + fpos + iconSizeMultiplierVector,
                                dHiddenMIcon.UV0,
                                dHiddenMIcon.UV1);
                        }
                        break;

                    case EntityTypes.OtherImportantObjects:
                        {
                            if (!this.Settings.OtherImportantObjects.TryGetValue(entity.Value.EntityCustomGroup, out var mopoiIcon))
                                mopoiIcon = this.Settings.OtherImportantObjects[-1];

                            iconSizeMultiplierVector *= mopoiIcon.IconScale;
                            fgDraw.AddImage(
                                mopoiIcon.TexturePtr,
                                mapCenter + fpos - iconSizeMultiplierVector,
                                mapCenter + fpos + iconSizeMultiplierVector,
                                mopoiIcon.UV0,
                                mopoiIcon.UV1);
                        }
                        break;

                    case EntityTypes.Renderable:
                        fgDraw.AddCircleFilled(mapCenter + fpos, 3f, 0xFFFFFFFF);
                        break;
                }
            }
        }

        private IEnumerator<Wait> ClearCachesAndUpdateAreaInfo()
        {
            while (true)
            {
                yield return new Wait(RemoteEvents.AreaChanged);
                this.CleanUpRadarPluginCaches();
                this.currentAreaName = Core.States.InGameStateObject.CurrentWorldInstance.AreaDetails.Id;

                // Rebuild pathfinder for the new area
                var area = Core.States.InGameStateObject.CurrentAreaInstance;
                this.pathfinder?.CancelAll();
                this.pathfinder = new FlowFieldPathfinder(area);

                this.GenerateMapTexture();
            }
        }

        private IEnumerator<Wait> OnMove()
        {
            while (true)
            {
                yield return new Wait(GameHelperEvents.OnMoved);
                this.UpdateMiniMapDetails();
                this.UpdateLargeMapDetails();
            }
        }

        private IEnumerator<Wait> OnClose()
        {
            while (true)
            {
                yield return new Wait(GameHelperEvents.OnClose);
                this.skipOneSettingChange = true;
                this.CleanUpRadarPluginCaches();
            }
        }

        private IEnumerator<Wait> OnForegroundChange()
        {
            while (true)
            {
                yield return new Wait(GameHelperEvents.OnForegroundChanged);
                this.UpdateMiniMapDetails();
                this.UpdateLargeMapDetails();
            }
        }

        private void UpdateMiniMapDetails()
        {
            var map = Core.States.InGameStateObject.GameUi.MiniMap;
            var widthSq = map.Size.X * map.Size.X;
            var heightSq = map.Size.Y * map.Size.Y;
            this.miniMapDiagonalLength = Math.Sqrt(widthSq + heightSq);
        }

        private void UpdateLargeMapDetails()
        {
            var map = Core.States.InGameStateObject.GameUi.LargeMap;
            var widthSq = map.Size.X * map.Size.X;
            var heightSq = map.Size.Y * map.Size.Y;
            this.largeMapDiagonalLength = Math.Sqrt(widthSq + heightSq);
        }

        private void ReloadMapTexture()
        {
            this.RemoveMapTexture();
            this.GenerateMapTexture();
        }

        private void RemoveMapTexture()
        {
            this.walkableMapTexture = IntPtr.Zero;
            this.walkableMapDimension = Vector2.Zero;
            Core.Overlay.RemoveImage("walkable_map");
        }

        private void GenerateMapTexture()
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
                        image[imageX, imageY] = new Rgba32(this.Settings.WalkableMapColor);
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

        private IconPicker RarityToIconMapping(Rarity rarity)
        {
            return rarity switch
            {
                Rarity.Normal or Rarity.Magic or Rarity.Rare or Rarity.Unique => this.Settings.BaseIcons[$"{rarity} Monster"],
                _ => this.Settings.BaseIcons[$"Normal Monster"],
            };
        }

        private string DelveChestPathToIcon(string path)
        {
            return path.Replace(this.delveChestStarting, null, StringComparison.Ordinal);
        }

        private void DrawEntityPathEnding(string path, ImDrawListPtr fgDraw, Vector2 pos)
        {
            var lastIndex = path.LastIndexOf('/') + 1;
            if (lastIndex < 0 || lastIndex >= path.Length)
                lastIndex = 0;

            var displayName = path.AsSpan(lastIndex, path.Length - lastIndex);
            var pNameSizeH = ImGui.CalcTextSize(displayName) / 2;
            fgDraw.AddRectFilled(pos - pNameSizeH, pos + pNameSizeH, ImGuiHelper.Color(0, 0, 0, 200));
            fgDraw.AddText(ImGui.GetFont(), ImGui.GetFontSize(), pos - pNameSizeH, ImGuiHelper.Color(255, 128, 128, 255), displayName);
        }

        private void AddNewPOIWidget()
        {
            var tgttilesInArea = Core.States.InGameStateObject.CurrentAreaInstance.TgtTilesLocations;
            ImGui.InputText("Area Name", ref this.currentAreaName, 200, ImGuiInputTextFlags.ReadOnly);
            ImGui.NewLine();
            ImGui.InputInt("Filter on Max POI frenquency", ref this.Settings.POIFrequencyFilter);
            ImGui.InputText("Filter by text", ref this.tmpTileFilter, 200);
            if (ImGui.InputInt("Select POI via Index###tgtSelectorCounter", ref this.tmpTgtSelectionCounter) &&
                this.tmpTgtSelectionCounter < tgttilesInArea.Keys.Count)
            {
                this.tmpTileName = tgttilesInArea.Keys.ElementAt(this.tmpTgtSelectionCounter);
            }

            ImGui.NewLine();
            ImGuiHelper.IEnumerableComboBox<string>("POI Path",
                tgttilesInArea.Keys.Where(k => string.IsNullOrEmpty(this.tmpTileFilter) ||
                k.Contains(this.tmpTileFilter, StringComparison.OrdinalIgnoreCase)),
                ref this.tmpTileName);
            ImGui.InputText("POI Display Name", ref this.tmpDisplayName, 200);
            ImGui.Checkbox("Add for all Areas", ref this.addTileForAllAreas);
            ImGui.SameLine();
            if (ImGui.Button("Add POI"))
            {
                var key = this.addTileForAllAreas ? "common" : this.currentAreaName;
                if (!string.IsNullOrEmpty(key) &&
                    !string.IsNullOrEmpty(this.tmpTileName) &&
                    !string.IsNullOrEmpty(this.tmpDisplayName))
                {
                    if (!this.Settings.ImportantTgts.ContainsKey(key))
                        this.Settings.ImportantTgts[key] = new();

                    this.Settings.ImportantTgts[key][this.tmpTileName] = this.tmpDisplayName;

                    this.tmpTileName = string.Empty;
                    this.tmpDisplayName = string.Empty;
                }
            }
        }

        private void CleanUpRadarPluginCaches()
        {
            this.delveChestCache.Clear();
            this.RemoveMapTexture();
            this.currentAreaName = string.Empty;
        }

        // -----------------------
        // Helpers (path colors, height/Walkable, drawing polyline)
        // -----------------------
        // Try to find a walkable tile near (tx,ty) that is actually reachable from 'start'.
        // We keep attempts small to avoid perf spikes (you can tune MAX_RADIUS / SAMPLES_PER_RING).
        private (int X, int Y)? FindNearestReachableNearTarget(
            GameHelper.RemoteObjects.States.InGameStateObjects.AreaInstance area,
            FlowFieldPathfinder pathfinder,
            Vector2 start, int tx, int ty,
            int MAX_RADIUS = 48, int SAMPLES_PER_RING = 12)
        {
            // First, try the exact tile (or its nearest walkable)
            var first = FindClosestWalkable(area, tx, ty, 48);
            if (first is (int fx, int fy) initial)
            {
                pathfinder.EnsureDirectionField(initial);
                int sx = (int)MathF.Round(start.X);
                int sy = (int)MathF.Round(start.Y);
                _pathBuffer.Clear();
                if (pathfinder.TryGetPath((sx, sy), initial, _pathBuffer, 16384))
                    return initial;
            }

            // Sample a small set of points in expanding rings around the target.
            // For each candidate: ensure walkable -> ensure field -> try path
            for (int r = 2; r <= MAX_RADIUS; r += 2)
            {
                // number of angular samples per ring (cap so it doesn't explode)
                int samples = Math.Clamp(SAMPLES_PER_RING + r / 8, SAMPLES_PER_RING, 32);
                for (int i = 0; i < samples; i++)
                {
                    float ang = (float)(i * (2 * Math.PI / samples));
                    int cx = tx + (int)MathF.Round(r * MathF.Cos(ang));
                    int cy = ty + (int)MathF.Round(r * MathF.Sin(ang));
                    var cand = FindClosestWalkable(area, cx, cy, 8);
                    if (cand is not (int gx, int gy)) continue;

                    var goal = (gx, gy);
                    pathfinder.EnsureDirectionField(goal);

                    int sx = (int)MathF.Round(start.X);
                    int sy = (int)MathF.Round(start.Y);
                    _pathBuffer.Clear();
                    if (pathfinder.TryGetPath((sx, sy), goal, _pathBuffer, 16384))
                        return goal;
                }
            }

            return null; // no reachable proxy found
        }

        private void CollectLabeledPOIs(Dictionary<string, string> dict,
            GameHelper.RemoteObjects.States.InGameStateObjects.AreaInstance area,
            List<(Vector2 Pos, string Label, string Key)> outList)
        {
            foreach (var kv in dict)
            {
                if (!area.TgtTilesLocations.TryGetValue(kv.Key, out var locs)) continue;
                string label = kv.Value;
                string key = kv.Key;
                for (int i = 0; i < locs.Count; i++)
                    outList.Add((locs[i], label, key));
            }
        }

        private static bool IsWalkable(GameHelper.RemoteObjects.States.InGameStateObjects.AreaInstance area, int x, int y)
        {
            var bpr = area.TerrainMetadata.BytesPerRow;
            if (bpr <= 0) return false;
            int width = bpr * 2;
            int height = area.GridWalkableData.Length / bpr;
            if ((uint)x >= (uint)width || (uint)y >= (uint)height) return false;
            int idx = y * bpr + (x >> 1);
            byte b = area.GridWalkableData[idx];
            int nib = ((x & 1) == 0) ? (b & 0xF) : ((b >> 4) & 0xF);
            return nib > 0;
        }

        private static (int X, int Y)? FindClosestWalkable(GameHelper.RemoteObjects.States.InGameStateObjects.AreaInstance area, int x, int y, int maxRadius = 12)
        {
            if (IsWalkable(area, x, y)) return (x, y);
            for (int r = 1; r <= maxRadius; r++)
            {
                int left = x - r, right = x + r, top = y - r, bottom = y + r;
                for (int i = 0; i <= 2 * r; i++)
                {
                    int gx1 = left + i, gy1 = top;       // top edge
                    int gx2 = left + i, gy2 = bottom;    // bottom edge
                    int gx3 = left, gy3 = top + i;       // left edge
                    int gx4 = right, gy4 = top + i;      // right edge

                    if (IsWalkable(area, gx1, gy1)) return (gx1, gy1);
                    if (IsWalkable(area, gx2, gy2)) return (gx2, gy2);
                    if (IsWalkable(area, gx3, gy3)) return (gx3, gy3);
                    if (IsWalkable(area, gx4, gy4)) return (gx4, gy4);
                }
            }
            return null;
        }

        private static float HeightAt(GameHelper.RemoteObjects.States.InGameStateObjects.AreaInstance area, int x, int y)
        {
            var gh = area.GridHeightData;
            if ((uint)y < (uint)gh.Length && (uint)x < (uint)gh[y].Length)
                return gh[y][x];
            return 0f;
        }

        private void DrawPathPolylineOnMap(ImDrawListPtr dl, Vector2 mapCenter,
            GameHelper.RemoteObjects.States.InGameStateObjects.AreaInstance area,
            Render playerRender, Vector2 anchor, List<Vector2> path,
            uint color, float thickness, float trimDist)
        {
            if (path.Count == 0) return;

            if (path.Count == 1)
            {
                // draw a minimal hint from anchor to the single node
                var node = path[0];
                float h0 = HeightAt(area, (int)node.X, (int)node.Y);
                var p0 = mapCenter + Helper.DeltaInWorldToMapDelta(anchor - anchor, 0);         // anchor on mapCenter
                var p1 = mapCenter + Helper.DeltaInWorldToMapDelta(node - anchor, -playerRender.TerrainHeight + h0);
                dl.AddLine(p0, p1, color, Math.Max(1f, thickness));
                return;
            }

            int startIdx = 0;
            if (trimDist > 0f)
            {
                for (int i = 0; i < path.Count; i++)
                {
                    if (Vector2.Distance(anchor, path[i]) >= trimDist) { startIdx = Math.Max(0, i - 1); break; }
                }
            }

            Vector2? last = null;
            for (int i = startIdx; i < path.Count; i += 2)
            {
                var node = path[i];
                float h = HeightAt(area, (int)node.X, (int)node.Y);
                var pt = mapCenter + Helper.DeltaInWorldToMapDelta(node - anchor, -playerRender.TerrainHeight + h);
                if (last is Vector2 lp) dl.AddLine(lp, pt, color, thickness);
                last = pt;
            }

            if (last is Vector2 lp2)
            {
                var endNode = path[^1];
                float he = HeightAt(area, (int)endNode.X, (int)endNode.Y);
                var ptEnd = mapCenter + Helper.DeltaInWorldToMapDelta(endNode - anchor, -playerRender.TerrainHeight + he);
                dl.AddLine(lp2, ptEnd, color, thickness);
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
