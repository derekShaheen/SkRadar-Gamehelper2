namespace Radar
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Numerics;
    using Coroutine;
    using GameHelper;
    using GameHelper.CoroutineEvents;
    using GameHelper.Plugin;
    using GameHelper.Utils;
    using ImGuiNET;
    using Newtonsoft.Json;

    /// <summary>
    /// <see cref="Radar"/> plugin.
    /// </summary>
    public sealed class Radar : PCore<RadarSettings>
    {
        private readonly WalkableMapRenderer walkableMapRenderer;
        private readonly PoiOverlayRenderer poiOverlayRenderer;
        private readonly EntityIconRenderer entityIconRenderer;

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

        private string SettingPathname => Path.Join(this.DllDirectory, "config", "settings.txt");
        private string ImportantTgtPathName => Path.Join(this.DllDirectory, "important_tgt_files.txt");

        public Radar()
        {
            this.walkableMapRenderer = new WalkableMapRenderer(this);
            this.poiOverlayRenderer = new PoiOverlayRenderer(this);
            this.entityIconRenderer = new EntityIconRenderer(this);
        }

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
                this.walkableMapRenderer.OnToggleDrawWalkableMap();

            if (ImGui.ColorEdit4("Drawn Map Color", ref this.Settings.WalkableMapColor))
                this.walkableMapRenderer.OnColorChanged();

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

            this.poiOverlayRenderer.BeginFrame();

            if (largeMap.IsVisible)
            {
                var largeMapRealCenter = largeMap.Center + largeMap.Shift + largeMap.DefaultShift;
                var largeMapModifiedZoom = this.Settings.LargeMapScaleMultiplier * largeMap.Zoom;
                Helper.DiagonalLength = this.walkableMapRenderer.LargeMapDiagonalLength;
                Helper.Scale = largeMapModifiedZoom;

                // Always use a full-screen transparent window as the culling region
                ImGui.SetNextWindowPos(Vector2.Zero, ImGuiCond.Always);
                ImGui.SetNextWindowSize(new Vector2(Core.Process.WindowArea.Size.Width, Core.Process.WindowArea.Size.Height), ImGuiCond.Always);
                ImGui.SetNextWindowBgAlpha(0f);
                ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
                ImGui.Begin("###FullScreenCull", ImGuiHelper.TransparentWindowFlags);
                ImGui.PopStyleVar();

                this.walkableMapRenderer.Draw(largeMapRealCenter);
                this.poiOverlayRenderer.Draw(largeMapRealCenter, this.currentAreaName);
                this.entityIconRenderer.Draw(largeMapRealCenter, largeMapModifiedZoom * 5f);

                ImGui.End();
            }

            if (miniMap.IsVisible)
            {
                Helper.DiagonalLength = this.walkableMapRenderer.MiniMapDiagonalLength;
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
                this.entityIconRenderer.Draw(miniMapCenter, miniMap.Zoom);

                ImGui.End();
            }

            // ---- POI Paths Window (movable, persists position) ----
            if (this.Settings.ShowPathsToPOI && this.poiOverlayRenderer.HasPathLabels)
            {
                // Only apply the saved position when the window appears (so user can still drag it).
                ImGui.SetNextWindowPos(_poiWindowPos, ImGuiCond.Appearing);
                ImGui.SetNextWindowSizeConstraints(new Vector2(200f, 100f), new Vector2(600f, 800f));

                if (ImGui.Begin("POI Paths")) // default flags allow moving
                {
                    // Remember current position every frame so we can restore it after area changes.
                    _poiWindowPos = ImGui.GetWindowPos();
                    _poiWindowPosSet = true;

                    foreach (var (label, colorU32) in this.poiOverlayRenderer.PathLabels)
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
        }

        /// <inheritdoc/>
        public override void OnEnable(bool isGameOpened)
        {
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
            this.walkableMapRenderer.GenerateMapTexture();
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

        private IEnumerator<Wait> ClearCachesAndUpdateAreaInfo()
        {
            while (true)
            {
                yield return new Wait(RemoteEvents.AreaChanged);
                this.CleanUpRadarPluginCaches();
                this.currentAreaName = Core.States.InGameStateObject.CurrentWorldInstance.AreaDetails.Id;

                var area = Core.States.InGameStateObject.CurrentAreaInstance;
                this.poiOverlayRenderer.OnAreaChanged(area);

                this.walkableMapRenderer.GenerateMapTexture();
            }
        }

        private IEnumerator<Wait> OnMove()
        {
            while (true)
            {
                yield return new Wait(GameHelperEvents.OnMoved);
                this.walkableMapRenderer.UpdateMiniMapDetails();
                this.walkableMapRenderer.UpdateLargeMapDetails();
            }
        }

        private IEnumerator<Wait> OnClose()
        {
            while (true)
            {
                yield return new Wait(GameHelperEvents.OnClose);
                this.CleanUpRadarPluginCaches();
            }
        }

        private IEnumerator<Wait> OnForegroundChange()
        {
            while (true)
            {
                yield return new Wait(GameHelperEvents.OnForegroundChanged);
                this.walkableMapRenderer.UpdateMiniMapDetails();
                this.walkableMapRenderer.UpdateLargeMapDetails();
            }
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
            this.poiOverlayRenderer.Reset();
            this.walkableMapRenderer.Reset();
            this.currentAreaName = string.Empty;
        }
    }
}
