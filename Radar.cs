namespace OriathHub.Plugins.Radar
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Numerics;
    using System.Runtime.InteropServices;
    using System.Threading;
    using System.Threading.Tasks;
    using Coroutine;
    using ImGuiNET;
    using Newtonsoft.Json;
    using OriathHub;
    using OriathHub.CoroutineEvents;
    using OriathHub.Plugin;
    using OriathHub.RemoteEnums;
    using OriathHub.RemoteEnums.Entity;
    using OriathHub.RemoteObjects.Components;
    using OriathHub.RemoteObjects.States.InGameStateObjects;
    using OriathHub.RemoteObjects.UiElement;
    using OriathHub.Utils;
    using SixLabors.ImageSharp;
    using SixLabors.ImageSharp.PixelFormats;
    using SixLabors.ImageSharp.Processing;

    /// <summary>
    /// <see cref="Radar"/> plugin.
    /// </summary>
    public sealed class Radar : PluginBase
    {
        private const string TempleTgtPrefix = "Metadata/Terrain/Leagues/Incursion/Tiles/Features/Waygates/WaygateDevice";

        private readonly string delveChestStarting = "Metadata/Chests/DelveChests/";
        private readonly Dictionary<uint, string> delveChestCache = new();

        private RadarSettings Settings = new();

        /// <summary>
        /// If we don't do this, user will be asked to
        /// setup the culling window everytime they open the game.
        /// </summary>
        private bool skipOneSettingChange = false;
        private bool isAddNewPOIHeaderOpened = false;
        private ActiveCoroutine? onMove;
        private ActiveCoroutine? onForegroundChange;
        private ActiveCoroutine? onGameClose;
        private ActiveCoroutine? onAreaChange;

        private string currentAreaName = string.Empty;

        private volatile PathCacheEntry[] _poiPaths = Array.Empty<PathCacheEntry>();
        private static readonly Vector4[] DefaultPathColors =
        {
            new(1.0f, 0.20f, 0.20f, 0.9f),
            new(0.20f, 1.0f, 0.20f, 0.9f),
            new(0.20f, 0.60f, 1.0f, 0.9f),
            new(1.0f, 1.0f, 0.20f, 0.9f),
            new(1.0f, 0.50f, 0.0f, 0.9f),
            new(0.80f, 0.20f, 1.0f, 0.9f),
        };
        private CancellationTokenSource _pathCts = new();
        private Task? _pathTask;
        private Vector2 _lastPathfindPlayerPos = new Vector2(float.MaxValue, float.MaxValue);

        private string tmpTileName = string.Empty;
        private string tmpDisplayName = string.Empty;
        private int tmpTgtSelectionCounter = 0;
        private string tmpTileFilter = string.Empty;
        private bool addTileForAllAreas = false;

        private string _pendingPoiPath = string.Empty;
        private string _pendingDisplayName = string.Empty;
        private bool _pendingPoiFocusSet = false;
        private bool _prevHeaderOpen = false;

        private double miniMapDiagonalLength = 0x00;

        private double largeMapDiagonalLength = 0x00;

        private float largeMapAutoScale = 0.1738f;

        // Diagonal + scale of the map currently being drawn (large or mini), set before
        // each map's draw calls and passed to LargeMapHelper.GridDeltaToMapPixels.
        private double activeMapDiagonal = 0x00;

        private float activeMapScale = 0.5f;

        private IntPtr walkableMapTexture = IntPtr.Zero;
        private Vector2 walkableMapDimension = Vector2.Zero;
        private readonly Dictionary<string, Vector2> textHalfSizeCache = new(StringComparer.Ordinal);
        private readonly Dictionary<int, Vector2> poiIndexHalfSizeCache = new();

        private List<(Vector2 min, Vector2 max, string key)> _lastPoiBoxRects = new();
        private Vector2 _debugWinPos = Vector2.Zero;
        private Vector2 _debugWinSize = Vector2.Zero;

        private string SettingPathname => Path.Join(this.DllDirectory, "config", "settings.txt");

        private string ImportantTgtPathName => Path.Join(this.DllDirectory, "important_tgt_files.txt");

        private string BossArenaTgtPathName => Path.Join(this.DllDirectory, "boss_arena_tgt_files.txt");

        private string StairsTgtPathName => Path.Join(this.DllDirectory, "stairs_tgt_files.txt");

        /// <inheritdoc/>
        public override string Name => "Radar";

        /// <inheritdoc/>
        public override string Description =>
            "Draws a maphack, terrain points of interest and entity icons on the in-game mini/large map.";

        /// <inheritdoc/>
        public override string Author => "OriathHub";

        /// <inheritdoc/>
        public override string Version => "1.0.0";

        /// <inheritdoc/>
        public override void DrawSettings()
        {
            ImGui.Checkbox("Hide when in Hideout/Town", ref this.Settings.DrawWhenNotInHideoutOrTown);
            ImGui.Checkbox("Hide when game is in the background", ref this.Settings.DrawWhenForeground);
            ImGui.Checkbox("Hide when game is paused", ref this.Settings.DrawWhenNotPaused);

            ImGui.Separator();
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

            if (ImGui.ColorEdit4("Map Color", ref this.Settings.WalkableMapColor))
            {
                if (this.walkableMapTexture != IntPtr.Zero)
                    this.ReloadMapTexture();
            }

            ImGui.SetNextItemWidth(180f);
            if (ImGui.SliderInt("Map Border Thickness", ref this.Settings.WalkableMapBorderThickness, 1, 8))
            {
                if (this.walkableMapTexture != IntPtr.Zero)
                    this.ReloadMapTexture();
            }

            ImGui.Separator();
            ImGui.Checkbox("Show terrain points of interest (Terrain POI)", ref this.Settings.ShowImportantPOI);
            ImGui.ColorEdit4("Terrain POI text color", ref this.Settings.POIColor);
            ImGui.Checkbox("Add black background to Terrain POI text", ref this.Settings.EnablePOIBackground);

            ImGui.Separator();
            ImGui.Checkbox("Hide entities outside the network bubble", ref this.Settings.HideOutsideNetworkBubble);
            ImGui.Checkbox("Show Player Names", ref this.Settings.ShowPlayersNames);
            ImGuiHelper.ToolTip("Does not work while player is in the Scourge.");
        }

        /// <inheritdoc/>
        public override void DrawAdvancedSettings()
        {
            this.DrawLargeMapFixControls();

            ImGui.Separator();
            this.DrawCullingWindowControls();

            ImGui.Separator();
            this._prevHeaderOpen = this.isAddNewPOIHeaderOpened;
            this.isAddNewPOIHeaderOpened = ImGui.CollapsingHeader("Add or Modify Terrain POI");
            if (this._prevHeaderOpen && !this.isAddNewPOIHeaderOpened && this.Settings.DebugRealWorld)
            {
                this.Settings.DebugRealWorld = false;
                this._lastPoiBoxRects.Clear();
                this._pendingPoiPath = string.Empty;
            }

            if (this.isAddNewPOIHeaderOpened)
            {
                this.AddNewPOIWidget();
                this.ShowPOIWidget();
            }

            ImGui.Separator();
            if (ImGui.CollapsingHeader("Path to POI"))
            {
                this.DrawPathToPoiControls();
            }

            ImGui.Separator();
            if (ImGui.CollapsingHeader("Icons Setting"))
            {
                this.DrawIconsControls();
            }
        }

        private void DrawLargeMapFixControls()
        {
            ImGui.TextWrapped("If mini/large map icons are misaligned, adjust Large Map Fix until icons are stable while moving.");
            ImGui.Checkbox("Auto-compute Large Map Fix from aspect ratio", ref this.Settings.AutoLargeMapScale);
            ImGuiHelper.ToolTip("Derives the fix from the game's viewport aspect ratio. Turn off to tune it manually.");

            if (this.Settings.AutoLargeMapScale)
            {
                ImGui.Text($"Computed Large Map Fix: {this.largeMapAutoScale:0.0000}");
            }
            else
            {
                ImGui.DragFloat("Large Map Fix", ref this.Settings.LargeMapScaleMultiplier, 0.001f, 0.1f, 2.0f);
                ImGuiHelper.ToolTip("Fixes large map icon offset. Find a value that keeps icons stable while moving. " +
                    "Per-resolution setting — no impact on mini-map. Press CTRL+LMB for precise input.");
            }
        }

        private void DrawCullingWindowControls()
        {
            if (ImGui.Checkbox("Modify Large Map Culling Window", ref this.Settings.ModifyCullWindow))
            {
                if (this.Settings.ModifyCullWindow)
                    this.Settings.MakeCullWindowFullScreen = false;
            }

            ImGui.TreePush("radar_culling_window");
            if (ImGui.Checkbox("Make Culling Window Cover Whole Game", ref this.Settings.MakeCullWindowFullScreen))
            {
                this.Settings.ModifyCullWindow = !this.Settings.MakeCullWindowFullScreen;
                this.Settings.CullWindowPos = Vector2.Zero;
                this.Settings.CullWindowSize.X = Core.Process.WindowArea.Width;
                this.Settings.CullWindowSize.Y = Core.Process.WindowArea.Height;
            }

            if (ImGui.TreeNode("Culling window options"))
            {
                ImGui.Checkbox("Draw maphack in culling window", ref this.Settings.DrawMapInCull);
                ImGui.Checkbox("Draw POIs in culling window", ref this.Settings.DrawPOIInCull);
                ImGui.TreePop();
            }

            ImGui.TreePop();
        }

        private void DrawPathToPoiControls()
        {
            // Toggling on while standing still must force an immediate recompute, otherwise
            // the lines only appear after the player walks past the recompute threshold.
            if (ImGui.Checkbox("Show pathfinding lines to POI", ref this.Settings.ShowPOIPathLines)
                && this.Settings.ShowPOIPathLines)
            {
                this._lastPathfindPlayerPos = new Vector2(float.MaxValue, float.MaxValue);
            }

            if (this.Settings.ShowPOIPathLines)
            {
                ImGui.SliderFloat("Line thickness", ref this.Settings.POIPathLineThickness, 1f, 8f);
                if (ImGui.TreeNode("Path colors (per POI in current area)"))
                {
                    var instance = Core.States.InGameStateObject.CurrentAreaInstance;
                    var seenLabels = new HashSet<string>();
                    int defaultIdx = 0;
                    void showColors(Dictionary<string, string> tgts)
                    {
                        foreach (var tile in tgts)
                        {
                            if (!instance.TgtTilesLocations.ContainsKey(tile.Key)) continue;
                            if (!seenLabels.Add(tile.Value)) continue;
                            var label = tile.Value;
                            var fallback = DefaultPathColors[defaultIdx % DefaultPathColors.Length];
                            defaultIdx++;

                            bool enabled = !this.Settings.POIPathEnabled.TryGetValue(label, out var e) || e;
                            if (ImGui.Checkbox($"##pathon_{label}", ref enabled))
                            {
                                this.Settings.POIPathEnabled[label] = enabled;
                                this._lastPathfindPlayerPos = new Vector2(float.MaxValue, float.MaxValue);
                            }

                            ImGui.SameLine();
                            var col = this.Settings.POIPathColors.TryGetValue(label, out var stored) ? stored : fallback;
                            if (ImGui.ColorEdit4($"{label}##pathcol_{label}", ref col))
                                this.Settings.POIPathColors[label] = col;
                        }
                    }

                    if (this.Settings.ImportantTgts.TryGetValue(this.currentAreaName, out var areaTgts))
                        showColors(areaTgts);
                    if (this.Settings.ImportantTgts.TryGetValue("common", out var commonTgts))
                        showColors(commonTgts);
                    if (seenLabels.Count == 0)
                        ImGui.TextDisabled("No POI detected in the current area.");
                    ImGui.TreePop();
                }
            }
        }

        private void DrawIconsControls()
        {
            this.Settings.DrawIconsSettingToImGui(
                "BaseGame Icons",
                this.Settings.BaseIcons,
                "Blockages icon can be set from Delve Icons category i.e. 'Blockage OR DelveWall'");

            this.Settings.DrawPOIMonsterSettingToImGui(this.DllDirectory);
            this.Settings.OtherImportantObjectsSettingToImGui(this.DllDirectory);
            this.Settings.DrawIconsSettingToImGui("Breach Icons", this.Settings.BreachIcons,
                "Breach bosses are same as BaseGame Icons -> Unique Monsters.");
            this.Settings.DrawIconsSettingToImGui("Delirium Icons", this.Settings.DeliriumIcons, string.Empty);
            this.Settings.DrawIconsSettingToImGui("Expedition Icons", this.Settings.ExpeditionIcons, string.Empty);
            this.Settings.DrawIconsSettingToImGui("Expedition Marker Icons", this.Settings.ExpeditionMarkerIcons,
                "Icons for expedition markers, keyed by MinimapIcon name. Set size to 0 to disable.");
            this.Settings.DrawIconsSettingToImGui("Expedition Remnant Icons", this.Settings.ExpeditionRemnantIcons,
                "Icons for expedition remnants with specific mods. Set size to 0 to disable.");
            this.Settings.DrawIconsSettingToImGui("Temple Icons", this.Settings.TempleIcons,
                "Icons for Incursion Waygate devices (Vaal Ruins).");
            this.Settings.DrawIconsSettingToImGui("Boss Icons", this.Settings.BossIcons,
                "Icons for map boss arenas.");
        }

        /// <inheritdoc/>
        public override IEnumerable<SettingSearchEntry> GetSearchableSettings() => new[]
        {
            new SettingSearchEntry("Settings", "Hide when in Hideout/Town",
                () => ImGui.Checkbox("Hide when in Hideout/Town", ref this.Settings.DrawWhenNotInHideoutOrTown)),
            new SettingSearchEntry("Settings", "Hide when game is in the background",
                () => ImGui.Checkbox("Hide when game is in the background", ref this.Settings.DrawWhenForeground)),
            new SettingSearchEntry("Settings", "Hide when game is paused",
                () => ImGui.Checkbox("Hide when game is paused", ref this.Settings.DrawWhenNotPaused)),
            new SettingSearchEntry("Settings", "Draw Area/Zone Map (maphack)", () =>
            {
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
            }, "maphack walkable terrain map"),
            new SettingSearchEntry("Settings", "Map Color", () =>
            {
                if (ImGui.ColorEdit4("Map Color", ref this.Settings.WalkableMapColor) && this.walkableMapTexture != IntPtr.Zero)
                    this.ReloadMapTexture();
            }),
            new SettingSearchEntry("Settings", "Map Border Thickness", () =>
            {
                ImGui.SetNextItemWidth(180f);
                if (ImGui.SliderInt("Map Border Thickness", ref this.Settings.WalkableMapBorderThickness, 1, 8) && this.walkableMapTexture != IntPtr.Zero)
                    this.ReloadMapTexture();
            }),
            new SettingSearchEntry("Settings", "Show terrain points of interest (Terrain POI)",
                () => ImGui.Checkbox("Show terrain points of interest (Terrain POI)", ref this.Settings.ShowImportantPOI), "terrain poi"),
            new SettingSearchEntry("Settings", "Terrain POI text color",
                () => ImGui.ColorEdit4("Terrain POI text color", ref this.Settings.POIColor)),
            new SettingSearchEntry("Settings", "Add black background to Terrain POI text",
                () => ImGui.Checkbox("Add black background to Terrain POI text", ref this.Settings.EnablePOIBackground)),
            new SettingSearchEntry("Settings", "Hide entities outside the network bubble",
                () => ImGui.Checkbox("Hide entities outside the network bubble", ref this.Settings.HideOutsideNetworkBubble), "network bubble"),
            new SettingSearchEntry("Settings", "Show Player Names", () =>
            {
                ImGui.Checkbox("Show Player Names", ref this.Settings.ShowPlayersNames);
                ImGuiHelper.ToolTip("Does not work while player is in the Scourge.");
            }),

            new SettingSearchEntry("Advanced", "Large Map Fix", this.DrawLargeMapFixControls,
                "large map fix scale icon offset alignment misaligned auto aspect ratio"),
            new SettingSearchEntry("Advanced", "Large Map Culling Window", this.DrawCullingWindowControls,
                "culling window maphack pois fullscreen cover whole game"),
            new SettingSearchEntry("Advanced", "Add or Modify Terrain POI", () =>
            {
                this.isAddNewPOIHeaderOpened = true;
                this.AddNewPOIWidget();
                this.ShowPOIWidget();
            }, "add modify terrain poi"),
            new SettingSearchEntry("Advanced", "Show pathfinding lines to POI", this.DrawPathToPoiControls,
                "path pathfinding lines poi thickness colors"),
            new SettingSearchEntry("Advanced", "Icons Setting", this.DrawIconsControls,
                "icons base breach delirium expedition temple ritual boss minimap"),
        };

        /// <inheritdoc/>
        public override void DrawUI()
        {
            var largeMap = Core.States.InGameStateObject.GameUi.LargeMap;
            var miniMap = Core.States.InGameStateObject.GameUi.MiniMap;
            var areaDetails = Core.States.InGameStateObject.CurrentWorldInstance.AreaDetails;

            if (this.Settings.ModifyCullWindow)
            {
                ImGui.SetNextWindowPos(largeMap.Center, ImGuiCond.Appearing);
                ImGui.SetNextWindowSize(new Vector2(400f), ImGuiCond.Appearing);
                ImGui.Begin("Large Map Culling Window");
                ImGui.TextWrapped("This is a culling window for the large map icons. " +
                                  "Any large map icons outside of this window will be hidden automatically. " +
                                  "Feel free to change the position/size of this window. " +
                                  "Once you are happy with the dimensions, double click this window. " +
                                  "You can bring this window back from the settings menu.");
                this.Settings.CullWindowPos = ImGui.GetWindowPos();
                this.Settings.CullWindowSize = ImGui.GetWindowSize();
                if (ImGui.IsWindowHovered() && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
                {
                    this.Settings.ModifyCullWindow = false;
                }

                ImGui.End();
            }

            if ((this.Settings.DrawWhenNotPaused && Core.States.GameCurrentState != GameStateTypes.InGameState) ||
                Core.States.GameCurrentState is not (GameStateTypes.InGameState or GameStateTypes.EscapeState) ||
                (this.Settings.DrawWhenForeground && !FocusHelper.IsGameOrOverlayForeground()) ||
                (this.Settings.DrawWhenNotInHideoutOrTown && (areaDetails.IsHideout || areaDetails.IsTown)) ||
                Core.States.InGameStateObject.GameUi.IsSkillTreeOpen)
            {
                return;
            }

            if (this.Settings.MakeCullWindowFullScreen)
            {
                this.Settings.CullWindowPos = Vector2.Zero;
                this.Settings.CullWindowSize.X = Core.Process.WindowArea.Size.Width;
                this.Settings.CullWindowSize.Y = Core.Process.WindowArea.Size.Height;
            }

            // The in-area LargeMap stays visible underneath the checkpoint/world-travel screen,
            // but that screen pans independently so the radar icons would be stuck in place.
            // Only draw the large-map radar for the actual area map, not the travel panel.
            if (largeMap.IsVisible && !Core.States.InGameStateObject.GameUi.WorldMapPanel.IsVisible)
            {
                if (this.largeMapDiagonalLength <= 0)
                {
                    this.UpdateLargeMapDetails();
                }

                var largeMapRealCenter = largeMap.Center + largeMap.Shift + largeMap.DefaultShift;
                var largeMapScale = this.Settings.AutoLargeMapScale
                    ? this.largeMapAutoScale
                    : this.Settings.LargeMapScaleMultiplier;
                var largeMapModifiedZoom = largeMapScale * largeMap.Zoom;
                this.activeMapDiagonal = this.largeMapDiagonalLength;
                this.activeMapScale = largeMapModifiedZoom;
                ImGui.SetNextWindowPos(this.Settings.CullWindowPos);
                ImGui.SetNextWindowSize(this.Settings.CullWindowSize);
                ImGui.SetNextWindowBgAlpha(0f);
                ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
                ImGui.Begin("Large Map Culling Window", ImGuiHelper.TransparentWindowFlags);
                ImGui.PopStyleVar();
                this.DrawLargeMap(largeMapRealCenter);
                this.DrawTgtFiles(largeMapRealCenter);
                this.DrawTgtIcons(largeMapRealCenter, largeMapModifiedZoom * 5f);
                this.DrawMapIcons(largeMapRealCenter, largeMapModifiedZoom * 5f);
                ImGui.End();
            }

            if (miniMap.IsVisible)
            {
                if (this.miniMapDiagonalLength <= 0)
                {
                    this.UpdateMiniMapDetails();
                }

                this.activeMapDiagonal = this.miniMapDiagonalLength;
                this.activeMapScale = miniMap.Zoom;
                var miniMapCenter = miniMap.Position +
                    (miniMap.Size / 2) +
                    miniMap.DefaultShift +
                    miniMap.Shift;
                ImGui.SetNextWindowPos(miniMap.Position);
                ImGui.SetNextWindowSize(miniMap.Size);
                ImGui.SetNextWindowBgAlpha(0f);
                ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
                ImGui.Begin("###minimapRadar", ImGuiHelper.TransparentWindowFlags);
                ImGui.PopStyleVar();
                this.DrawTgtIcons(miniMapCenter, miniMap.Zoom);
                this.DrawMapIcons(miniMapCenter, miniMap.Zoom);
                ImGui.End();
            }

            if (this.Settings.DebugRealWorld)
            {
                this.DrawRealWorldPOIOverlay();
                this.DrawPOIDebugWindow();
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
            if (!isGameOpened)
            {
                this.skipOneSettingChange = true;
            }

            if (File.Exists(this.SettingPathname))
            {
                var content = File.ReadAllText(this.SettingPathname);
                // Skip individual fields that fail (e.g. POIPathColors migrated from List to Dict).
                var lenient = new JsonSerializerSettings { Error = (_, e) => e.ErrorContext.Handled = true };
                this.Settings = JsonConvert.DeserializeObject<RadarSettings>(content, lenient) ?? new RadarSettings();
                // Saved settings store absolute paths. Re-resolve all icon paths against
                // the current DLL directory so the plugin survives folder renames/moves.
                this.Settings.ReinitializeIconPaths(this.DllDirectory);
            }

            if (File.Exists(this.ImportantTgtPathName))
            {
                var tgtfiles = File.ReadAllText(this.ImportantTgtPathName);
                this.Settings.ImportantTgts = JsonConvert.DeserializeObject
                    <Dictionary<string, Dictionary<string, string>>>(tgtfiles)
                    ?? new Dictionary<string, Dictionary<string, string>>();
            }

            if (File.Exists(this.BossArenaTgtPathName))
            {
                var bossfiles = File.ReadAllText(this.BossArenaTgtPathName);
                this.Settings.BossArenaTgts = JsonConvert.DeserializeObject
                    <Dictionary<string, string>>(bossfiles) ?? new Dictionary<string, string>();
            }

            if (File.Exists(this.StairsTgtPathName))
            {
                var stairsfiles = File.ReadAllText(this.StairsTgtPathName);
                this.Settings.StairsTgts = JsonConvert.DeserializeObject
                    <Dictionary<string, string>>(stairsfiles) ?? new Dictionary<string, string>();
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
            Directory.CreateDirectory(Path.GetDirectoryName(this.SettingPathname) ?? string.Empty);
            var settingsData = JsonConvert.SerializeObject(this.Settings, Formatting.Indented);
            File.WriteAllText(this.SettingPathname, settingsData);

            if (this.Settings.ImportantTgts.Count > 0)
            {
                var tgtfiles = JsonConvert.SerializeObject(
                    this.Settings.ImportantTgts, Formatting.Indented);
                File.WriteAllText(this.ImportantTgtPathName, tgtfiles);
            }

            if (this.Settings.BossArenaTgts.Count > 0)
            {
                var bossfiles = JsonConvert.SerializeObject(
                    this.Settings.BossArenaTgts, Formatting.Indented);
                File.WriteAllText(this.BossArenaTgtPathName, bossfiles);
            }

            if (this.Settings.StairsTgts.Count > 0)
            {
                var stairsfiles = JsonConvert.SerializeObject(
                    this.Settings.StairsTgts, Formatting.Indented);
                File.WriteAllText(this.StairsTgtPathName, stairsfiles);
            }
        }

        private void DrawLargeMap(Vector2 mapCenter)
        {
            if (!this.Settings.DrawWalkableMap)
            {
                return;
            }

            if (this.walkableMapTexture == IntPtr.Zero)
            {
                // The terrain grid may only stream in a few frames after the area change (e.g. Trial
                // of the Sekhemas rooms), so the one-shot generation on AreaChanged can run too early.
                // Retry here once the walkable grid is available, then stop (texture becomes non-zero).
                if (Core.States.InGameStateObject.CurrentAreaInstance.GridWalkableData.Length > 0)
                {
                    this.GenerateMapTexture();
                }

                if (this.walkableMapTexture == IntPtr.Zero)
                {
                    return;
                }
            }

            var player = Core.States.InGameStateObject.CurrentAreaInstance.Player;
            if (!player.TryGetComponent<Render>(out var pRender))
            {
                return;
            }

            var rectf = new RectangleF(
                -pRender.GridPosition.X,
                -pRender.GridPosition.Y,
                this.walkableMapDimension.X,
                this.walkableMapDimension.Y);

            var p1 = LargeMapHelper.GridDeltaToMapPixels(
                new Vector2(rectf.Left, rectf.Top), -pRender.TerrainHeight, this.activeMapDiagonal, this.activeMapScale);
            var p2 = LargeMapHelper.GridDeltaToMapPixels(
                new Vector2(rectf.Right, rectf.Top), -pRender.TerrainHeight, this.activeMapDiagonal, this.activeMapScale);
            var p3 = LargeMapHelper.GridDeltaToMapPixels(
                new Vector2(rectf.Right, rectf.Bottom), -pRender.TerrainHeight, this.activeMapDiagonal, this.activeMapScale);
            var p4 = LargeMapHelper.GridDeltaToMapPixels(
                new Vector2(rectf.Left, rectf.Bottom), -pRender.TerrainHeight, this.activeMapDiagonal, this.activeMapScale);
            p1 += mapCenter;
            p2 += mapCenter;
            p3 += mapCenter;
            p4 += mapCenter;

            if (this.Settings.DrawMapInCull)
            {
                ImGui.GetWindowDrawList().AddImageQuad(this.walkableMapTexture, p1, p2, p3, p4);
            }
            else
            {
                ImGui.GetBackgroundDrawList().AddImageQuad(this.walkableMapTexture, p1, p2, p3, p4);
            }
        }

        private void DrawTgtFiles(Vector2 mapCenter)
        {
            var col = ImGuiHelper.Color(
                (uint)(this.Settings.POIColor.X * 255),
                (uint)(this.Settings.POIColor.Y * 255),
                (uint)(this.Settings.POIColor.Z * 255),
                (uint)(this.Settings.POIColor.W * 255));

            ImDrawListPtr fgDraw;
            if (this.Settings.DrawPOIInCull)
            {
                fgDraw = ImGui.GetWindowDrawList();
            }
            else
            {
                fgDraw = ImGui.GetBackgroundDrawList();
            }

            var currentAreaInstance = Core.States.InGameStateObject.CurrentAreaInstance;
            if (!currentAreaInstance.Player.TryGetComponent<Render>(out var playerRender))
            {
                return;
            }

            var pPos = new Vector2(playerRender.GridPosition.X, playerRender.GridPosition.Y);
            var clipMin = ImGui.GetWindowPos();
            var clipMax = clipMin + ImGui.GetWindowSize();

            void drawString(string text, Vector2 location, Vector2 stringImGuiSize, bool drawBackground)
            {
                float height = 0;
                if (currentAreaInstance.GridHeightData.Length > 0 &&
                    location.Y < currentAreaInstance.GridHeightData.Length &&
                    location.X < currentAreaInstance.GridHeightData[0].Length)
                {
                    height = currentAreaInstance.GridHeightData[(int)location.Y][(int)location.X];
                }

                var fpos = LargeMapHelper.GridDeltaToMapPixels(
                    location - pPos, -playerRender.TerrainHeight + height, this.activeMapDiagonal, this.activeMapScale);
                var textMin = mapCenter + fpos - stringImGuiSize;
                var textMax = mapCenter + fpos + stringImGuiSize;
                if (textMax.X < clipMin.X || textMin.X > clipMax.X || textMax.Y < clipMin.Y || textMin.Y > clipMax.Y)
                {
                    return;
                }

                if (drawBackground)
                {
                    fgDraw.AddRectFilled(
                        textMin,
                        textMax,
                        ImGuiHelper.Color(0, 0, 0, 200));
                }

                fgDraw.AddText(
                    ImGui.GetFont(),
                    ImGui.GetFontSize(),
                    textMin,
                    col,
                    text);
            }

            if (this.isAddNewPOIHeaderOpened && !this.Settings.DebugRealWorld)
            {
                var counter = 0;
                foreach (var tgtKV in currentAreaInstance.TgtTilesLocations)
                {
                    if (!(this.Settings.POIFrequencyFilter > 0 &&
                        tgtKV.Value.Count > this.Settings.POIFrequencyFilter))
                    {
                        if (!this.poiIndexHalfSizeCache.TryGetValue(counter, out var tgtKImGuiSize))
                        {
                            tgtKImGuiSize = ImGui.CalcTextSize(counter.ToString()) / 2;
                            this.poiIndexHalfSizeCache[counter] = tgtKImGuiSize;
                        }

                        for (var i = 0; i < tgtKV.Value.Count; i++)
                        {
                            drawString(counter.ToString(), tgtKV.Value[i], tgtKImGuiSize, false);
                        }
                    }

                    counter++;
                }
            }
            if (this.Settings.ShowImportantPOI)
            {
                if (this.Settings.ImportantTgts.TryGetValue(this.currentAreaName, out var importantTgtsOfCurrentArea))
                {
                    foreach (var tile in importantTgtsOfCurrentArea)
                    {
                        if (currentAreaInstance.TgtTilesLocations.TryGetValue(tile.Key, out var locations))
                        {
                            var strSize = this.GetTextHalfSize(tile.Value);
                            for (var i = 0; i < locations.Count; i++)
                            {
                                drawString(tile.Value, locations[i], strSize, this.Settings.EnablePOIBackground);
                            }
                        }
                    }
                }

                if (this.Settings.ImportantTgts.TryGetValue("common", out var importantTgtsOfAllAreas))
                {
                    foreach (var tile in importantTgtsOfAllAreas)
                    {
                        if (currentAreaInstance.TgtTilesLocations.TryGetValue(tile.Key, out var locations))
                        {
                            var strSize = this.GetTextHalfSize(tile.Value);
                            for (var i = 0; i < locations.Count; i++)
                            {
                                drawString(tile.Value, locations[i], strSize, this.Settings.EnablePOIBackground);
                            }
                        }
                    }
                }
            }

            if (this.Settings.ShowPOIPathLines && !this.isAddNewPOIHeaderOpened && this.Settings.ShowImportantPOI)
            {
                if (Vector2.DistanceSquared(pPos, this._lastPathfindPlayerPos) > 20f * 20f)
                    this.TriggerPathCompute(pPos);

                var paths = this._poiPaths;
                if (paths.Length > 0)
                {
                    float thickness = this.Settings.POIPathLineThickness;
                    for (int pathIdx = 0; pathIdx < paths.Length; pathIdx++)
                    {
                        var entry = paths[pathIdx];
                        if (entry?.Path == null || entry.Path.Count < 2) continue;
                        if (this.Settings.POIPathEnabled.TryGetValue(entry.Label, out var lineEnabled) && !lineEnabled)
                            continue;

                        var cv = this.Settings.POIPathColors.TryGetValue(entry.Label, out var stored)
                            ? stored
                            : DefaultPathColors[pathIdx % DefaultPathColors.Length];
                        uint lineCol = ImGuiHelper.Color(
                            (uint)(cv.X * 255), (uint)(cv.Y * 255),
                            (uint)(cv.Z * 255), (uint)(cv.W * 255));

                        // Trim waypoints the player has already walked past. Waypoints are sparse
                        // after string pulling, so project the player onto the nearest path
                        // *segment* (not the nearest vertex) and draw from the next waypoint ahead.
                        // The line starts at the player's exact position (mapCenter) and follows the
                        // route live every frame without re-running A*.
                        var path = entry.Path;
                        int startNode = NearestSegmentIndex(path, pPos) + 1;

                        Vector2? prevScreen = mapCenter;
                        for (int n = startNode; n < path.Count; n++)
                        {
                            var gridPos = path[n];
                            float h = 0f;
                            int ix = (int)gridPos.X;
                            int iy = (int)gridPos.Y;
                            if (currentAreaInstance.GridHeightData.Length > 0 &&
                                iy < currentAreaInstance.GridHeightData.Length &&
                                ix < currentAreaInstance.GridHeightData[0].Length)
                            {
                                h = currentAreaInstance.GridHeightData[iy][ix];
                            }

                            var fpos = LargeMapHelper.GridDeltaToMapPixels(gridPos - pPos, -playerRender.TerrainHeight + h, this.activeMapDiagonal, this.activeMapScale);
                            var screenPos = mapCenter + fpos;

                            fgDraw.AddLine(prevScreen.Value, screenPos, lineCol, thickness);
                            prevScreen = screenPos;
                        }
                    }
                }
            }
        }

        private void DrawTgtIcons(Vector2 mapCenter, float iconSizeMultiplier)
        {
            var fgDraw = ImGui.GetWindowDrawList();
            var currentAreaInstance = Core.States.InGameStateObject.CurrentAreaInstance;
            if (!currentAreaInstance.Player.TryGetComponent<Render>(out var playerRender))
            {
                return;
            }

            var pPos = new Vector2(playerRender.GridPosition.X, playerRender.GridPosition.Y);

            foreach (var tgtKV in currentAreaInstance.TgtTilesLocations)
            {
                if (tgtKV.Key.StartsWith(TempleTgtPrefix) && tgtKV.Key.EndsWith(":1-y:1"))
                {
                    if (!this.Settings.IsGroupEnabled("Temple Icons") ||
                        !this.Settings.IsItemEnabled("Temple Icons", "Vaal Ruins"))
                        continue;
                    if (!this.Settings.TempleIcons.TryGetValue("Vaal Ruins", out var templeIcon))
                        continue;
                    this.DrawIconAtTgtLocations(fgDraw, mapCenter, pPos, playerRender, tgtKV.Value, templeIcon, iconSizeMultiplier, shiftUp: true);
                }
                else if (this.Settings.BossArenaTgts.ContainsKey(tgtKV.Key))
                {
                    if (!this.Settings.IsGroupEnabled("Boss Icons") ||
                        !this.Settings.IsItemEnabled("Boss Icons", "Boss Arena"))
                        continue;
                    if (!this.Settings.BossIcons.TryGetValue("Boss Arena", out var bossIcon))
                        continue;
                    this.DrawIconAtTgtLocations(fgDraw, mapCenter, pPos, playerRender, tgtKV.Value, bossIcon, iconSizeMultiplier);
                }
                else if (this.Settings.StairsTgts.ContainsKey(tgtKV.Key))
                {
                    if (!this.Settings.IsGroupEnabled("BaseGame Icons") ||
                        !this.Settings.IsItemEnabled("BaseGame Icons", "Stairs"))
                        continue;
                    if (!this.Settings.BaseIcons.TryGetValue("Stairs", out var stairsIcon))
                        continue;
                    this.DrawIconAtTgtLocations(fgDraw, mapCenter, pPos, playerRender, tgtKV.Value, stairsIcon, iconSizeMultiplier);
                }
            }
        }

        private void DrawIconAtTgtLocations(
            ImDrawListPtr fgDraw,
            Vector2 mapCenter,
            Vector2 pPos,
            Render playerRender,
            List<Vector2> locations,
            IconPicker icon,
            float iconSizeMultiplier,
            bool shiftUp = false)
        {
            var currentAreaInstance = Core.States.InGameStateObject.CurrentAreaInstance;
            for (var i = 0; i < locations.Count; i++)
            {
                var location = locations[i];
                float height = 0;
                if (currentAreaInstance.GridHeightData.Length > 0 &&
                    location.Y < currentAreaInstance.GridHeightData.Length &&
                    location.X < currentAreaInstance.GridHeightData[0].Length)
                {
                    height = currentAreaInstance.GridHeightData[(int)location.Y][(int)location.X];
                }

                var fpos = LargeMapHelper.GridDeltaToMapPixels(
                    location - pPos, -playerRender.TerrainHeight + height, this.activeMapDiagonal, this.activeMapScale);
                var iconSizeMultiplierVector = new Vector2(iconSizeMultiplier);
                iconSizeMultiplierVector *= icon.IconScale;
                var offset = shiftUp ? new Vector2(0, iconSizeMultiplierVector.Y) : Vector2.Zero;
                fgDraw.AddImage(
                    icon.TexturePtr,
                    mapCenter + fpos - iconSizeMultiplierVector - offset,
                    mapCenter + fpos + iconSizeMultiplierVector - offset,
                    icon.UV0,
                    icon.UV1);
            }
        }

        private void DrawMapIcons(Vector2 mapCenter, float iconSizeMultiplier)
        {
            var fgDraw = ImGui.GetWindowDrawList();
            var currentAreaInstance = Core.States.InGameStateObject.CurrentAreaInstance;
            if (!currentAreaInstance.Player.TryGetComponent<Render>(out var playerRender))
            {
                return;
            }

            var clipMin = ImGui.GetWindowPos();
            var clipMax = clipMin + ImGui.GetWindowSize();
            var clipPadding = iconSizeMultiplier * 4f;
            var pPos = new Vector2(playerRender.GridPosition.X, playerRender.GridPosition.Y);

            var baseIcons = this.Settings.BaseIcons;
            var expeditionIcons = this.Settings.ExpeditionIcons;
            var breachIcons = this.Settings.BreachIcons;
            var deliriumIcons = this.Settings.DeliriumIcons;
            var poiMonsterIcons = this.Settings.POIMonsters;
            var otherImportantObjects = this.Settings.OtherImportantObjects;

            if (!baseIcons.ContainsKey("NPC"))
                return;

            var npcIcon = baseIcons["NPC"];
            var specialNpcIcon = baseIcons["Special NPC"];
            var leaderIcon = baseIcons["Leader"];
            var playerIcon = baseIcons["Player"];
            var selfIcon = baseIcons["Self"];
            var allOtherChestIcon = baseIcons["All Other Chest"];
            var rareChestIcon = baseIcons["Rare Chests"];
            var magicChestIcon = baseIcons["Magic Chests"];
            var expeditionChestIcon = expeditionIcons["Generic Expedition Chests"];
            var breachChestIcon = breachIcons["Breach Chest"];
            var strongboxIcon = baseIcons["Strongbox"];
            var shrineIcon = baseIcons["Shrine"];
            var pinnacleBossHiddenIcon = baseIcons["Pinnacle Boss Not Attackable"];
            var friendlyIcon = baseIcons["Friendly"];
            var deliriumBombIcon = deliriumIcons["Delirium Bomb"];
            var deliriumSpawnerIcon = deliriumIcons["Delirium Spawner"];
            var normalMonsterIcon = baseIcons["Normal Monster"];
            var magicMonsterIcon = baseIcons["Magic Monster"];
            var rareMonsterIcon = baseIcons["Rare Monster"];
            var uniqueMonsterIcon = baseIcons["Unique Monster"];

            foreach (var entity in currentAreaInstance.AwakeEntities)
            {
                var entityValue = entity.Value;
                if (this.Settings.HideOutsideNetworkBubble && !entityValue.IsValid)
                {
                    continue;
                }

                if (entityValue.EntityState == EntityStates.Useless)
                {
                    continue;
                }

                if (!entityValue.TryGetComponent<Render>(out var entityRender))
                {
                    continue;
                }

                var ePos = new Vector2(entityRender.GridPosition.X, entityRender.GridPosition.Y);
                var fpos = LargeMapHelper.GridDeltaToMapPixels(ePos - pPos, entityRender.TerrainHeight - playerRender.TerrainHeight, this.activeMapDiagonal, this.activeMapScale);
                var screenPos = mapCenter + fpos;
                if (screenPos.X < clipMin.X - clipPadding || screenPos.X > clipMax.X + clipPadding ||
                    screenPos.Y < clipMin.Y - clipPadding || screenPos.Y > clipMax.Y + clipPadding)
                {
                    continue;
                }

                var iconSizeMultiplierVector = Vector2.One * iconSizeMultiplier;

                void DrawIcon(IconPicker icon, string groupName, string itemKey)
                {
                    if (!this.Settings.IsGroupEnabled(groupName) || !this.Settings.IsItemEnabled(groupName, itemKey))
                        return;
                    var scaled = iconSizeMultiplierVector * icon.IconScale;
                    fgDraw.AddImage(
                        icon.TexturePtr,
                        screenPos - scaled,
                        screenPos + scaled,
                        icon.UV0,
                        icon.UV1);
                }

                switch (entityValue.EntityType)
                {
                    case EntityTypes.NPC:
                        if (entityValue.EntitySubtype == EntitySubtypes.SpecialNPC)
                            DrawIcon(specialNpcIcon, "BaseGame Icons", "Special NPC");
                        else
                            DrawIcon(npcIcon, "BaseGame Icons", "NPC");
                        break;
                    case EntityTypes.Player:
                        if (entityValue.EntitySubtype == EntitySubtypes.PlayerOther)
                        {
                            if (this.Settings.ShowPlayersNames && entityValue.TryGetComponent<Player>(out var playerComp))
                            {
                                if (this.Settings.IsGroupEnabled("BaseGame Icons") && this.Settings.IsItemEnabled("BaseGame Icons", "Player"))
                                {
                                    var pNameSizeH = this.GetTextHalfSize(playerComp.Name);
                                    fgDraw.AddRectFilled(screenPos - pNameSizeH, screenPos + pNameSizeH,
                                        ImGuiHelper.Color(0, 0, 0, 200));
                                    fgDraw.AddText(ImGui.GetFont(), ImGui.GetFontSize(), screenPos - pNameSizeH,
                                        ImGuiHelper.Color(255, 128, 128, 255), playerComp.Name);
                                }
                            }
                            else
                            {
                                var isLeader = entityValue.EntityState == EntityStates.PlayerLeader;
                                DrawIcon(isLeader ? leaderIcon : playerIcon, "BaseGame Icons", isLeader ? "Leader" : "Player");
                            }
                        }
                        else
                        {
                            DrawIcon(selfIcon, "BaseGame Icons", "Self");
                        }

                        break;
                    case EntityTypes.Chest:
                        switch (entityValue.EntitySubtype)
                        {
                            case EntitySubtypes.None:
                                DrawIcon(allOtherChestIcon, "BaseGame Icons", "All Other Chest");
                                break;
                            case EntitySubtypes.ChestWithRareRarity:
                                DrawIcon(rareChestIcon, "BaseGame Icons", "Rare Chests");
                                break;
                            case EntitySubtypes.ChestWithMagicRarity:
                                DrawIcon(magicChestIcon, "BaseGame Icons", "Magic Chests");
                                break;
                            case EntitySubtypes.ExpeditionChest:
                                if (entityValue.Path.Contains("LeagueFaction") &&
                                    this.Settings.ExpeditionMarkerIcons.TryGetValue("Logbook", out var logbookIcon) &&
                                    logbookIcon.IconScale > 0)
                                {
                                    DrawIcon(logbookIcon, "Expedition Marker Icons", "Logbook");
                                }
                                else
                                {
                                    DrawIcon(expeditionChestIcon, "Expedition Icons", "Generic Expedition Chests");
                                }

                                break;
                            case EntitySubtypes.BreachChest:
                                DrawIcon(breachChestIcon, "Breach Icons", "Breach Chest");
                                break;
                            case EntitySubtypes.Strongbox:
                                DrawIcon(strongboxIcon, "BaseGame Icons", "Strongbox");
                                break;
                        }

                        break;
                    case EntityTypes.Shrine:
                        if (entityValue.TryGetComponent<Shrine>(out var shrineComp) && shrineComp.IsUsed)
                            break;
                        DrawIcon(shrineIcon, "BaseGame Icons", "Shrine");
                        break;
                    case EntityTypes.Monster:
                        if (entityValue.TryGetComponent<Life>(out var monLife) && !monLife.IsAlive)
                            break;
                        if (IsMonsterModHelper(entityValue))
                            break;

                        switch (entityValue.EntityState)
                        {
                            case EntityStates.None:
                                if (entityValue.EntitySubtype == EntitySubtypes.POIMonster)
                                {
                                    if (!poiMonsterIcons.TryGetValue(entityValue.EntityCustomGroup, out var poiIcon))
                                        poiIcon = poiMonsterIcons[-1];
                                    var poiItemKey = this.Settings.GroupItemKey(entityValue.EntityCustomGroup);
                                    DrawIcon(poiIcon, "Monster POI Icons", poiItemKey);
                                }
                                else if (entityValue.TryGetComponent<ObjectMagicProperties>(out var omp))
                                {
                                    var rarityIcon = this.RarityToIconMapping(omp.Rarity, normalMonsterIcon, magicMonsterIcon, rareMonsterIcon, uniqueMonsterIcon);
                                    var rarityItemKey = omp.Rarity switch
                                    {
                                        Rarity.Magic => "Magic Monster",
                                        Rarity.Rare => "Rare Monster",
                                        Rarity.Unique => "Unique Monster",
                                        _ => "Normal Monster"
                                    };
                                    DrawIcon(rarityIcon, "BaseGame Icons", rarityItemKey);
                                }

                                break;
                            case EntityStates.PinnacleBossHidden:
                                DrawIcon(pinnacleBossHiddenIcon, "BaseGame Icons", "Pinnacle Boss Not Attackable");
                                break;
                            case EntityStates.MonsterFriendly:
                                DrawIcon(friendlyIcon, "BaseGame Icons", "Friendly");
                                break;
                            default:
                                break;
                        }

                        break;
                    case EntityTypes.DeliriumBomb:
                        DrawIcon(deliriumBombIcon, "Delirium Icons", "Delirium Bomb");
                        break;
                    case EntityTypes.DeliriumSpawner:
                        DrawIcon(deliriumSpawnerIcon, "Delirium Icons", "Delirium Spawner");
                        break;
                    case EntityTypes.OtherImportantObjects:
                        if (entityValue.EntityCustomGroup == RadarSettings.ExpeditionMarkerGroup)
                        {
                            if (entityValue.TryGetComponent<MinimapIcon>(out var minimapIcon) &&
                                !string.IsNullOrEmpty(minimapIcon.IconName) &&
                                RadarSettings.ExpeditionMarkerIconNameMap.TryGetValue(minimapIcon.IconName, out var displayName) &&
                                this.Settings.ExpeditionMarkerIcons.TryGetValue(displayName, out var expMarkerIcon) &&
                                expMarkerIcon.IconScale > 0)
                            {
                                DrawIcon(expMarkerIcon, "Expedition Marker Icons", displayName);
                            }
                        }
                        else if (entityValue.EntityCustomGroup == RadarSettings.ExpeditionRemnantGroup)
                        {
                            if (entityValue.TryGetComponent<ObjectMagicProperties>(out var remnantOmp))
                            {
                                foreach (var modName in remnantOmp.ModNames)
                                {
                                    foreach (var (modSubstring, remnantDisplayName) in RadarSettings.ExpeditionRemnantModMap)
                                    {
                                        if (modName.Contains(modSubstring) &&
                                            this.Settings.ExpeditionRemnantIcons.TryGetValue(remnantDisplayName, out var remnantIcon) &&
                                            remnantIcon.IconScale > 0)
                                        {
                                            DrawIcon(remnantIcon, "Expedition Remnant Icons", remnantDisplayName);
                                            goto doneRemnant;
                                        }
                                    }
                                }
                                doneRemnant:;
                            }
                        }
                        else
                        {
                            if (entityValue.EntityCustomGroup == RadarSettings.RitualRuneGroup && IsRitualComplete(entityValue))
                                break;
                            if (entityValue.EntityCustomGroup == RadarSettings.Expedition2EncounterGroup && IsExpedition2Complete(entityValue))
                                break;
                            if (entityValue.EntityCustomGroup == RadarSettings.BrequelInitiatorGroup && IsBrequelComplete(entityValue))
                                break;
                            if (!otherImportantObjects.TryGetValue(entityValue.EntityCustomGroup, out var mopoiIcon))
                                mopoiIcon = otherImportantObjects[-1];
                            var mopoiItemKey = this.Settings.GroupItemKey(entityValue.EntityCustomGroup);
                            DrawIcon(mopoiIcon, "Special Objects Icons", mopoiItemKey);
                        }

                        break;
                    case EntityTypes.Renderable:
                        fgDraw.AddCircleFilled(screenPos, 3f, 0xFFFFFFFF);
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
                // Terrain grid buffers can stream in a few frames after the area change (notably in
                // Trial of the Sekhemas rooms), so the texture may not be buildable yet. Attempt it
                // here; DrawLargeMap retries each frame until the walkable grid is available.
                this.GenerateMapTexture();
            }
        }

        private IEnumerator<Wait> OnMove()
        {
            while (true)
            {
                yield return new Wait(OriathEvents.OnMoved);
                this.UpdateMiniMapDetails();
                this.UpdateLargeMapDetails();
                if (this.Settings.MakeCullWindowFullScreen)
                {
                    this.Settings.CullWindowPos = Vector2.Zero;

                    this.Settings.CullWindowSize.X = Core.Process.WindowArea.Size.Width;
                    this.Settings.CullWindowSize.Y = Core.Process.WindowArea.Size.Height;
                    this.skipOneSettingChange = false;
                }
                else if (this.skipOneSettingChange)
                {
                    this.skipOneSettingChange = false;
                }
                else
                {
                    this.Settings.ModifyCullWindow = true;
                }
            }
        }

        private IEnumerator<Wait> OnClose()
        {
            while (true)
            {
                yield return new Wait(OriathEvents.OnClose);
                this.skipOneSettingChange = true;
                this.CleanUpRadarPluginCaches();
            }
        }

        private IEnumerator<Wait> OnForegroundChange()
        {
            while (true)
            {
                yield return new Wait(OriathEvents.OnForegroundChanged);
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

            var area = Core.Process.WindowArea;
            if (area.Width > 0 && area.Height > 0)
            {
                this.largeMapAutoScale = LargeMapHelper.ComputeAutoScale(area.Width, area.Height);
            }
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
            {
                return;
            }

            var instance = Core.States.InGameStateObject.CurrentAreaInstance;
            var gridHeightData = instance.GridHeightData;
            var mapWalkableData = instance.GridWalkableData;
            var bytesPerRow = instance.TerrainMetadata.BytesPerRow;
            var worldToGridHeightMultiplier = instance.WorldToGridConvertor * 2f;
            if (bytesPerRow <= 0)
            {
                return;
            }

            var mapEdgeDetector = new MapEdgeDetector(mapWalkableData, bytesPerRow);
            if (mapEdgeDetector.TotalRows <= 0)
            {
                return;
            }

            var configuration = Configuration.Default.Clone();
            configuration.PreferContiguousImageBuffers = true;
            var imageWidth = bytesPerRow * 2;
            var imageHeight = mapEdgeDetector.TotalRows;
            var borderMask = new byte[imageWidth * imageHeight];
            var borderThickness = Math.Clamp(this.Settings.WalkableMapBorderThickness, 1, 8);
            var borderStartOffset = -(borderThickness / 2);
            var borderEndOffset = borderStartOffset + borderThickness;

            Parallel.For(0, gridHeightData.Length, y =>
            {
                for (var x = 1; x < gridHeightData[y].Length - 1; x++)
                {
                    if (!mapEdgeDetector.IsBorder(x, y))
                    {
                        continue;
                    }

                    var height = (int)(gridHeightData[y][x] / worldToGridHeightMultiplier);
                    var imageX = x - height;
                    var imageY = y - height;

                    if (mapEdgeDetector.IsInsideMapBoundary(imageX, imageY))
                    {
                        for (var dy = borderStartOffset; dy < borderEndOffset; dy++)
                        {
                            var py = imageY + dy;
                            if ((uint)py >= (uint)imageHeight)
                                continue;

                            for (var dx = borderStartOffset; dx < borderEndOffset; dx++)
                            {
                                var px = imageX + dx;
                                if ((uint)px >= (uint)imageWidth)
                                    continue;

                                borderMask[(py * imageWidth) + px] = 1;
                            }
                        }
                    }
                }
            });

            using Image<Rgba32> image = new(configuration, imageWidth, imageHeight);
            var mapColor = new Rgba32(this.Settings.WalkableMapColor);
            for (var i = 0; i < borderMask.Length; i++)
            {
                if (borderMask[i] == 0)
                    continue;

                image[i % imageWidth, i / imageWidth] = mapColor;
            }

#if DEBUG
            image.Save(this.DllDirectory +
                       @$"/current_map_{Core.States.InGameStateObject.CurrentAreaInstance.AreaHash}.jpeg");
#endif
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

                // Resize in place. The previous ResizeProcessor.CreatePixelSpecificCloningProcessor
                // produced a clone whose result was discarded, leaving `image` at full size — so areas
                // taller/wider than 8192px (e.g. Trial of the Sekhemas, ~9000px) uploaded an oversized
                // texture that the overlay failed to create, making the maphack invisible there.
                var targetSize = new Size(newWidth, newHeight);
                image.Mutate(x => x.Resize(targetSize));
            }

            Core.Overlay.AddOrGetImagePointer("walkable_map", image, false, out var t);
            this.walkableMapTexture = t;
        }

        private static bool IsRitualComplete(Entity entity)
        {
            if (!entity.TryGetComponent<StateMachine>(out var sm))
                return false;
            var states = sm.States;
            for (int i = 0; i < states.Count; i++)
            {
                if (states[i].Name == "rituals_completed" && states[i].Value == 3)
                    return true;
            }

            return false;
        }

        private static bool IsExpedition2Complete(Entity entity)
        {
            if (!entity.TryGetComponent<StateMachine>(out var sm))
                return false;
            var states = sm.States;
            for (int i = 0; i < states.Count; i++)
            {
                if (states[i].Name == "activated" && states[i].Value == 7)
                    return true;
            }

            return false;
        }

        private static bool IsBrequelComplete(Entity entity)
        {
            if (!entity.TryGetComponent<StateMachine>(out var sm))
                return false;
            var states = sm.States;
            for (int i = 0; i < states.Count; i++)
            {
                if (states[i].Name == "encounter_state" && states[i].Value == 4)
                    return true;
            }

            return false;
        }

        private static bool IsMonsterModHelper(Entity entity)
        {
            if (entity.TryGetComponent<DiesAfterTime>(out _))
            {
                return true;
            }

            if (entity.Path.StartsWith("Metadata/Monsters/MonsterMods/", StringComparison.Ordinal))
            {
                return true;
            }

            return entity.TryGetComponent<ObjectMagicProperties>(out var oComp, false) &&
                oComp.ModNames.Contains("RateLimitedDaemon") &&
                oComp.ModNames.Contains("MonsterNoDropsOrExperience");
        }

        private IconPicker RarityToIconMapping(
            Rarity rarity,
            IconPicker normalMonsterIcon,
            IconPicker magicMonsterIcon,
            IconPicker rareMonsterIcon,
            IconPicker uniqueMonsterIcon)
        {
            return rarity switch
            {
                Rarity.Magic => magicMonsterIcon,
                Rarity.Rare => rareMonsterIcon,
                Rarity.Unique => uniqueMonsterIcon,
                _ => normalMonsterIcon,
            };
        }

        private Vector2 GetTextHalfSize(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return Vector2.Zero;
            }

            if (!this.textHalfSizeCache.TryGetValue(text, out var size))
            {
                size = ImGui.CalcTextSize(text) / 2;
                this.textHalfSizeCache[text] = size;
            }

            return size;
        }

        private string DelveChestPathToIcon(string path)
        {
            return path.Replace(this.delveChestStarting, null, StringComparison.Ordinal);
        }

        private void DrawEntityPathEnding(string path, ImDrawListPtr fgDraw, Vector2 pos)
        {
            var lastIndex = path.LastIndexOf('/') + 1;
            if (lastIndex < 0 || lastIndex >= path.Length)
            {
                lastIndex = 0;
            }

            var displayName = path.AsSpan(lastIndex, path.Length - lastIndex);
            var pNameSizeH = ImGui.CalcTextSize(displayName) / 2;
            fgDraw.AddRectFilled(pos - pNameSizeH, pos + pNameSizeH,
                ImGuiHelper.Color(0, 0, 0, 200));
            fgDraw.AddText(ImGui.GetFont(), ImGui.GetFontSize(), pos - pNameSizeH,
                ImGuiHelper.Color(255, 128, 128, 255), displayName);

        }

        private void AddNewPOIWidget()
        {
            var tgttilesInArea = Core.States.InGameStateObject.CurrentAreaInstance.TgtTilesLocations;
            ImGui.InputText("Area Name", ref this.currentAreaName, 200, ImGuiInputTextFlags.ReadOnly);
            ImGui.Checkbox("Debug Real World", ref this.Settings.DebugRealWorld);
            ImGuiHelper.ToolTip("Renders POI indices in the 3D world and opens a debug list window.\n" +
                "Replaces the large-map counter numbers while active.\n" +
                "Orange label = not yet added.  Green label = already added.\n" +
                "Click a label to instantly add it to the current area's POI list.\n" +
                "NOTE: while active, game mouse input is captured by the overlay.");
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
                    {
                        this.Settings.ImportantTgts[key] = new();
                    }

                    this.Settings.ImportantTgts[key]
                        [this.tmpTileName] = this.tmpDisplayName;

                    this.tmpTileName = string.Empty;
                    this.tmpDisplayName = string.Empty;
                }
            }
        }

        private void ShowPOIWidget()
        {
            if (ImGui.TreeNode($"Important Terrain POIs common for all Areas"))
            {
                if (this.Settings.ImportantTgts.ContainsKey("common"))
                {
                    foreach (var tgt in this.Settings.ImportantTgts["common"])
                    {
                        if (ImGui.SmallButton($"Delete##{tgt.Key}"))
                        {
                            this.Settings.ImportantTgts["common"].Remove(tgt.Key);
                        }

                        ImGui.SameLine();
                        ImGui.Text($"POI Path: {tgt.Key}, Display: {tgt.Value}");
                        ImGuiHelper.ToolTip("Click me to Modify.");
                        if (ImGui.IsItemClicked())
                        {
                            this.tmpTileName = tgt.Key;
                            this.tmpDisplayName = tgt.Value;
                        }
                    }
                }

                ImGui.TreePop();
            }

            if (ImGui.TreeNode($"Important Terrain POIs in Area: {this.currentAreaName}##import_time_in_area"))
            {
                if (this.Settings.ImportantTgts.ContainsKey(this.currentAreaName))
                {
                    foreach (var tgt in this.Settings.ImportantTgts[this.currentAreaName])
                    {
                        if (ImGui.SmallButton($"Delete##{tgt.Key}"))
                        {
                            this.Settings.ImportantTgts[this.currentAreaName].Remove(tgt.Key);
                        }

                        ImGui.SameLine();
                        ImGui.Text($"POI Path: {tgt.Key}, Display: {tgt.Value}");
                        ImGuiHelper.ToolTip("Click me to Modify.");
                        if (ImGui.IsItemClicked())
                        {
                            this.tmpTileName = tgt.Key;
                            this.tmpDisplayName = tgt.Value;
                        }
                    }
                }

                ImGui.TreePop();
            }
        }

        private void DrawRealWorldPOIOverlay()
        {
            var area = Core.States.InGameStateObject.CurrentAreaInstance;
            var world = Core.States.InGameStateObject.CurrentWorldInstance;
            var gridToWorld = area.WorldToGridConvertor;
            int winW = Core.Process.WindowArea.Width;
            int winH = Core.Process.WindowArea.Height;

            // Determine cursor position in overlay-local coordinates via Win32 (works regardless
            // of WS_EX_TRANSPARENT state — no WM_MOUSEMOVE needed).
            GetCursorPos(out var cp);
            var winArea = Core.Process.WindowArea;
            float cx = cp.X - winArea.X;
            float cy = cp.Y - winArea.Y;

            // Check against previous frame's box rects (1-frame lag, imperceptible).
            bool cursorOverPOI = false;
            foreach (var (min, max, _) in this._lastPoiBoxRects)
            {
                if (cx >= min.X && cx <= max.X && cy >= min.Y && cy <= max.Y)
                {
                    cursorOverPOI = true;
                    break;
                }
            }

            // When cursor is NOT over a POI box, keep this window as NoInputs so that
            // ClickableTransparentOverlay can restore WS_EX_TRANSPARENT and game input passes through.
            var captureFlags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoNav |
                ImGuiWindowFlags.NoBringToFrontOnFocus | ImGuiWindowFlags.NoMove |
                ImGuiWindowFlags.NoSavedSettings;
            if (!cursorOverPOI)
                captureFlags |= ImGuiWindowFlags.NoInputs;

            ImGui.SetNextWindowPos(Vector2.Zero);
            ImGui.SetNextWindowSize(new Vector2(winW, winH));
            ImGui.SetNextWindowBgAlpha(0f);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
            ImGui.Begin("###dbgPoiCapture", captureFlags);
            ImGui.PopStyleVar();

            // Render boxes via the foreground draw list (visible in all modes).
            var drawList = ImGui.GetForegroundDrawList();
            var pad = new Vector2(3, 2);
            this.Settings.ImportantTgts.TryGetValue(this.currentAreaName, out var areaPoIs);

            var thisFrameRects = new List<(Vector2 min, Vector2 max, string key)>();

            int counter = 0;
            foreach (var tgtKV in area.TgtTilesLocations)
            {
                bool skip = this.Settings.POIFrequencyFilter > 0 && tgtKV.Value.Count > this.Settings.POIFrequencyFilter;
                if (!skip && !string.IsNullOrEmpty(this.tmpTileFilter) &&
                    !tgtKV.Key.Contains(this.tmpTileFilter, StringComparison.OrdinalIgnoreCase))
                    skip = true;

                if (!skip)
                {
                    bool alreadyAdded = areaPoIs?.ContainsKey(tgtKV.Key) ?? false;
                    uint labelColor = alreadyAdded
                        ? ImGuiHelper.Color(100, 255, 100, 255)
                        : ImGuiHelper.Color(255, 200, 100, 255);

                    var label = counter.ToString();
                    var half = ImGui.CalcTextSize(label) / 2f;
                    var boxSize = (half + pad) * 2f;

                    for (int i = 0; i < tgtKV.Value.Count; i++)
                    {
                        var gridPos = tgtKV.Value[i];
                        float height = 0;
                        int ix = (int)gridPos.X, iy = (int)gridPos.Y;
                        if (area.GridHeightData.Length > 0 &&
                            iy < area.GridHeightData.Length &&
                            ix < area.GridHeightData[0].Length)
                            height = area.GridHeightData[iy][ix];

                        var screenPos = world.WorldToScreen(
                            new Vector2(gridPos.X * gridToWorld, gridPos.Y * gridToWorld), height);

                        if (screenPos.X < 0 || screenPos.X > winW ||
                            screenPos.Y < 0 || screenPos.Y > winH)
                            continue;

                        var boxMin = screenPos - half - pad;
                        var boxMax = boxMin + boxSize;

                        drawList.AddRectFilled(boxMin, boxMax, ImGuiHelper.Color(0, 0, 0, 180));
                        drawList.AddRect(boxMin, boxMax, labelColor);
                        drawList.AddText(screenPos - half, labelColor, label);

                        thisFrameRects.Add((boxMin, boxMax, tgtKV.Key));

                        // Only register interactive elements when the window is actually receiving input.
                        if (cursorOverPOI)
                        {
                            ImGui.SetCursorScreenPos(boxMin);
                            if (ImGui.InvisibleButton($"##p{counter}_{i}", boxSize))
                            {
                                if (alreadyAdded)
                                    this.TogglePOIForCurrentArea(tgtKV.Key);
                                else
                                {
                                    this._pendingPoiPath = tgtKV.Key;
                                    this._pendingDisplayName = tgtKV.Key.AsSpan(tgtKV.Key.LastIndexOf('/') + 1).ToString();
                                    this._pendingPoiFocusSet = false;
                                }
                            }

                            if (ImGui.IsItemHovered())
                            {
                                if (alreadyAdded && areaPoIs != null && areaPoIs.TryGetValue(tgtKV.Key, out var dn))
                                    ImGui.SetTooltip($"{tgtKV.Key}\nDisplay: {dn}\nClick to remove");
                                else
                                    ImGui.SetTooltip($"{tgtKV.Key}\nClick to add");
                            }
                        }
                    }
                }

                counter++;
            }

            this._lastPoiBoxRects = thisFrameRects;
            ImGui.End();

            // Display name input dialog — shown when a non-added POI was clicked.
            if (!string.IsNullOrEmpty(this._pendingPoiPath))
            {
                var dialogSize = new Vector2(380, 100);
                ImGui.SetNextWindowPos(
                    new Vector2((winW - dialogSize.X) / 2f, (winH - dialogSize.Y) / 2f),
                    ImGuiCond.Always);
                ImGui.SetNextWindowSize(dialogSize, ImGuiCond.Always);
                ImGui.SetNextWindowBgAlpha(0.95f);
                ImGui.Begin("Add POI###dbgAddPoiDialog",
                    ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove |
                    ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoSavedSettings |
                    ImGuiWindowFlags.NoBringToFrontOnFocus);

                ImGui.TextUnformatted("POI Display Name (Enter to confirm, Esc to cancel):");
                ImGui.SetNextItemWidth(-1);
                if (!this._pendingPoiFocusSet)
                {
                    ImGui.SetKeyboardFocusHere();
                    this._pendingPoiFocusSet = true;
                }

                bool confirmed = ImGui.InputText("##dbgAddPoiName", ref this._pendingDisplayName, 200,
                    ImGuiInputTextFlags.EnterReturnsTrue);
                if (confirmed || ImGui.Button("OK", new Vector2(80, 0)))
                {
                    if (!string.IsNullOrEmpty(this._pendingDisplayName))
                        this.AddPOIWithDisplayName(this._pendingPoiPath, this._pendingDisplayName);
                    this._pendingPoiPath = string.Empty;
                    this._pendingDisplayName = string.Empty;
                    this._pendingPoiFocusSet = false;
                }

                ImGui.SameLine();
                if (ImGui.Button("Cancel", new Vector2(80, 0)) || ImGui.IsKeyPressed(ImGuiKey.Escape))
                {
                    this._pendingPoiPath = string.Empty;
                    this._pendingDisplayName = string.Empty;
                    this._pendingPoiFocusSet = false;
                }

                ImGui.End();
            }
        }

        private void AddPOIWithDisplayName(string path, string displayName)
        {
            if (string.IsNullOrEmpty(this.currentAreaName)) return;
            if (!this.Settings.ImportantTgts.ContainsKey(this.currentAreaName))
                this.Settings.ImportantTgts[this.currentAreaName] = new();
            this.Settings.ImportantTgts[this.currentAreaName][path] = displayName;
        }

        private void AddPOIForCurrentArea(string path)
        {
            if (string.IsNullOrEmpty(this.currentAreaName)) return;
            var displayName = path.AsSpan(path.LastIndexOf('/') + 1).ToString();
            if (!this.Settings.ImportantTgts.ContainsKey(this.currentAreaName))
                this.Settings.ImportantTgts[this.currentAreaName] = new();
            this.Settings.ImportantTgts[this.currentAreaName].TryAdd(path, displayName);
        }

        private void TogglePOIForCurrentArea(string path)
        {
            if (string.IsNullOrEmpty(this.currentAreaName)) return;
            if (this.Settings.ImportantTgts.TryGetValue(this.currentAreaName, out var area) && area.ContainsKey(path))
                area.Remove(path);
            else
                this.AddPOIForCurrentArea(path);
        }

        private void DrawPOIDebugWindow()
        {
            var area = Core.States.InGameStateObject.CurrentAreaInstance;

            // Same cursor-aware NoInputs pattern: interactive only when cursor is inside the window.
            // Uses previous frame's stored pos/size (1-frame lag, imperceptible).
            GetCursorPos(out var cp);
            var winArea2 = Core.Process.WindowArea;
            float cx2 = cp.X - winArea2.X;
            float cy2 = cp.Y - winArea2.Y;
            bool cursorOver = this._debugWinSize != Vector2.Zero
                && cx2 >= this._debugWinPos.X && cx2 <= this._debugWinPos.X + this._debugWinSize.X
                && cy2 >= this._debugWinPos.Y && cy2 <= this._debugWinPos.Y + this._debugWinSize.Y;

            ImGui.SetNextWindowSize(new Vector2(640, 420), ImGuiCond.Appearing);
            ImGui.SetNextWindowBgAlpha(0.88f);
            ImGui.Begin("Terrain POI Debug###radarPoiDebugWin",
                cursorOver ? ImGuiWindowFlags.None : ImGuiWindowFlags.NoInputs);

            this._debugWinPos = ImGui.GetWindowPos();
            this._debugWinSize = ImGui.GetWindowSize();

            ImGui.TextColored(new Vector4(1f, 0.8f, 0.4f, 1f), $"Area: {this.currentAreaName}");
            ImGui.Separator();

            ImGui.SetNextItemWidth(180f);
            ImGui.InputInt("Max Frequency", ref this.Settings.POIFrequencyFilter);
            ImGui.SameLine();
            ImGui.SetNextItemWidth(220f);
            ImGui.InputText("Text Filter##dbgFilter", ref this.tmpTileFilter, 200);

            ImGui.Separator();
            ImGui.TextDisabled("Click a row to instantly add it to the current area's POI list.");
            ImGui.BeginChild("poi_debug_list###poiDbgList", Vector2.Zero, ImGuiChildFlags.Borders);

            int counter = 0;
            foreach (var tgtKV in area.TgtTilesLocations)
            {
                bool skip = this.Settings.POIFrequencyFilter > 0 && tgtKV.Value.Count > this.Settings.POIFrequencyFilter;
                if (!skip && !string.IsNullOrEmpty(this.tmpTileFilter) &&
                    !tgtKV.Key.Contains(this.tmpTileFilter, StringComparison.OrdinalIgnoreCase))
                    skip = true;

                if (!skip)
                {
                    bool alreadyAdded = this.Settings.ImportantTgts.TryGetValue(this.currentAreaName, out var areaTgts)
                        && areaTgts.ContainsKey(tgtKV.Key);
                    if (ImGui.Selectable($"[{counter}]  {tgtKV.Key}  (x{tgtKV.Value.Count})##poi{counter}", alreadyAdded))
                        this.TogglePOIForCurrentArea(tgtKV.Key);
                    if (ImGui.IsItemHovered())
                        ImGuiHelper.ToolTip(tgtKV.Key);
                }

                counter++;
            }

            ImGui.EndChild();
            ImGui.End();
        }

        // Index of the path segment [i, i+1] whose closest point to <paramref name="p"/> is
        // nearest. Used to trim already-walked portions of a string-pulled (sparse) path.
        private static int NearestSegmentIndex(System.Collections.Generic.List<Vector2> path, Vector2 p)
        {
            int best = 0;
            float bestDistSq = float.MaxValue;
            for (int i = 0; i < path.Count - 1; i++)
            {
                Vector2 a = path[i];
                Vector2 ab = path[i + 1] - a;
                float len2 = ab.LengthSquared();
                float t = len2 > 0f ? Math.Clamp(Vector2.Dot(p - a, ab) / len2, 0f, 1f) : 0f;
                float d = Vector2.DistanceSquared(p, a + ab * t);
                if (d < bestDistSq)
                {
                    bestDistSq = d;
                    best = i;
                }
            }

            return best;
        }

        private void TriggerPathCompute(Vector2 playerGridPos)
        {
            // Single-flight: never interrupt a running compute. The render loop re-checks the
            // movement threshold every frame, so when the in-flight task finishes a fresh compute
            // starts from the current position. This guarantees computes always complete (paths
            // stay fresh while walking) instead of being perpetually cancelled mid-search.
            if (this._pathTask is { IsCompleted: false })
                return;

            this._lastPathfindPlayerPos = playerGridPos;

            var instance = Core.States.InGameStateObject.CurrentAreaInstance;
            var walkableData = instance.GridWalkableData;
            var bytesPerRow = instance.TerrainMetadata.BytesPerRow;

            if (bytesPerRow <= 0 || walkableData.Length == 0)
                return;

            // Group by label: multiple metadata keys often share a label (e.g. all "Mud Burrow" variants).
            // For each unique label, find the single closest tile location across all matching keys.
            var labelToClosest = new Dictionary<string, Vector2>();
            void collect(Dictionary<string, string> tgts)
            {
                foreach (var tile in tgts)
                {
                    if (!instance.TgtTilesLocations.TryGetValue(tile.Key, out var locs) || locs.Count == 0)
                        continue;
                    var label = tile.Value;
                    if (this.Settings.POIPathEnabled.TryGetValue(label, out var en) && !en)
                        continue;
                    foreach (var loc in locs)
                    {
                        if (!labelToClosest.TryGetValue(label, out var current) ||
                            Vector2.DistanceSquared(loc, playerGridPos) < Vector2.DistanceSquared(current, playerGridPos))
                        {
                            labelToClosest[label] = loc;
                        }
                    }
                }
            }

            if (this.Settings.ImportantTgts.TryGetValue(this.currentAreaName, out var areaTargets))
                collect(areaTargets);
            if (this.Settings.ImportantTgts.TryGetValue("common", out var commonTargets))
                collect(commonTargets);

            if (labelToClosest.Count == 0)
                return;

            var uniqueTargets = labelToClosest.ToList();
            var ct = this._pathCts.Token;
            var start = playerGridPos;
            this._pathTask = Task.Run(() =>
            {
                var entries = new PathCacheEntry[uniqueTargets.Count];
                for (int i = 0; i < uniqueTargets.Count; i++)
                {
                    if (ct.IsCancellationRequested) return;
                    var label = uniqueTargets[i].Key;
                    var goal = uniqueTargets[i].Value;
                    var path = PathFinder.FindPath(walkableData, bytesPerRow, start, goal, ct);
                    entries[i] = new PathCacheEntry(label, goal, path);
                }
                // Publish the full set in one atomic swap. The previously computed lines stay
                // visible until the new route is fully ready, so nothing blinks out mid-recompute.
                if (!ct.IsCancellationRequested)
                    this._poiPaths = entries;
            }, ct);
        }

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT cursorPos);

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X; public int Y; }

        private void CleanUpRadarPluginCaches()
        {
            this.delveChestCache.Clear();
            this.textHalfSizeCache.Clear();
            this.poiIndexHalfSizeCache.Clear();
            this._lastPoiBoxRects.Clear();
            this.RemoveMapTexture();
            this.currentAreaName = string.Empty;
            // Cancel any in-flight compute and hand out a fresh token source — the cancelled one
            // would otherwise abort every future compute. The old task observes its cancelled token
            // and won't publish; nulling the handle lets the new area start computing immediately.
            this._pathCts.Cancel();
            this._pathCts = new CancellationTokenSource();
            this._pathTask = null;
            this._poiPaths = Array.Empty<PathCacheEntry>();
            this._lastPathfindPlayerPos = new Vector2(float.MaxValue, float.MaxValue);
        }
    }

    /// <summary>A cached pathfinding result for a single POI.</summary>
    internal sealed record PathCacheEntry(string Label, Vector2 Goal, List<Vector2>? Path);
}
