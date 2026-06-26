namespace OriathHub.Plugins.Radar
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Numerics;
    using ImGuiNET;
    using Newtonsoft.Json;
    using OriathHub.Utils;

    /// <summary>
    /// <see cref="Radar"/> plugin settings class.
    /// </summary>
    public sealed class RadarSettings
    {
        private static readonly Vector2 IconSize = new(64, 64);
        private static int poiMonsterGroupNumber = 0;
        private static int poiSpecialObjectGroupNumber = 0;

        // Transient per-frame staging — not serialized.
        [JsonIgnore] private static readonly Dictionary<string, int> iconGroupSortOrder = new();
        [JsonIgnore] private static readonly Dictionary<int, string> poiMonsterLabelStage = new();
        [JsonIgnore] private static readonly Dictionary<int, int> poiMonsterNumStage = new();
        [JsonIgnore] private static readonly Dictionary<int, string> specialObjLabelStage = new();
        [JsonIgnore] private static readonly Dictionary<int, int> specialObjNumStage = new();

        /// <summary>
        /// Multipler to apply to the Large Map icons
        /// so they display correctly on the screen.
        /// </summary>
        public float LargeMapScaleMultiplier = 0.1738f;

        /// <summary>
        /// When true, the Large Map Fix is computed automatically from the viewport
        /// aspect ratio instead of using the manual <see cref="LargeMapScaleMultiplier"/>.
        /// </summary>
        public bool AutoLargeMapScale = true;

        /// <summary>
        /// Do not draw the Radar plugin stuff when game is in the background.
        /// </summary>
        public bool DrawWhenForeground = true;

        /// <summary>
        /// Do not draw the Radar plugin stuff when user is in hideout/town.
        /// </summary>
        public bool DrawWhenNotInHideoutOrTown = true;

        /// <summary>
        /// Do not draw the Radar plugin stuff when user is in pause menu.
        /// </summary>
        public bool DrawWhenNotPaused = true;

        /// <summary>
        /// Hides all the entities that are outside the network bubble.
        /// </summary>
        public bool HideOutsideNetworkBubble = false;

        /// <summary>
        /// Gets a value indicating whether user wants to modify large map culling window or not.
        /// </summary>
        public bool ModifyCullWindow = false;

        /// <summary>
        /// Gets a value indicating whether user wants culling window
        /// to cover the full game or not.
        /// </summary>
        public bool MakeCullWindowFullScreen = true;

        /// <summary>
        /// Gets a value indicating whether to draw the map in culling window or not.
        /// </summary>
        public bool DrawMapInCull = true;

        /// <summary>
        /// Gets a value indicating whether to draw the POI in culling window or not.
        /// </summary>
        public bool DrawPOIInCull = true;

        /// <summary>
        /// Gets a value indicating whether user wants to draw walkable map or not.
        /// </summary>
        public bool DrawWalkableMap = true;

        /// <summary>
        /// Gets a value indicating what color to use for drawing walkable map.
        /// </summary>
        public Vector4 WalkableMapColor = new Vector4(150f) / 255f;

        /// <summary>
        /// Gets the map border thickness in generated texture pixels.
        /// </summary>
        public int WalkableMapBorderThickness = 1;

        /// <summary>
        /// Gets the position of the cull window that the user wants.
        /// </summary>
        public Vector2 CullWindowPos = Vector2.Zero;

        /// <summary>
        /// Get the size of the cull window that the user wants.
        /// </summary>
        public Vector2 CullWindowSize = Vector2.Zero;

        /// <summary>
        /// Gets a value indicating wether user wants to show Player icon or names.
        /// </summary>
        public bool ShowPlayersNames = false;

        /// <summary>
        /// Gets a value indicating what is the maximum frequency a POI should have
        /// </summary>
        public int POIFrequencyFilter = 0;

        /// <summary>
        /// Gets a value indicating wether user want to show important tgt names or not.
        /// </summary>
        public bool ShowImportantPOI = true;

        /// <summary>
        /// Gets a value indicating what color to use for drawing the POI.
        /// </summary>
        public Vector4 POIColor = new(1f, 0.5f, 0.5f, 1f);

        /// <summary>
        /// Gets a value indicating wether user want to draw a background when drawing the POI.
        /// </summary>
        public bool EnablePOIBackground = true;

        /// <summary>
        /// When true, renders POI indices in the 3D game world and opens the POI debug window.
        /// </summary>
        public bool DebugRealWorld = false;

        /// <summary>
        /// Draw a pathfound line from the player to each configured POI.
        /// </summary>
        public bool ShowPOIPathLines = true;

        /// <summary>
        /// Width in pixels of each POI path line.
        /// </summary>
        public float POIPathLineThickness = 2f;

        /// <summary>
        /// Per-label colors for POI path lines. Keyed by label string; missing entries fall back to defaults.
        /// </summary>
        public Dictionary<string, Vector4> POIPathColors = new();

        /// <summary>
        /// Per-label enable flag for POI path lines. Keyed by label string; missing entries default to enabled.
        /// </summary>
        public Dictionary<string, bool> POIPathEnabled = new();

        /// <summary>
        /// Gets the Tgts and their expected clusters per area/zone/map.
        /// </summary>
        [JsonIgnore]
        public Dictionary<string, Dictionary<string, string>> ImportantTgts = new();

        /// <summary>
        /// Icons to display on the map. Base game includes normal chests, strongboxes, monsters etc.
        /// </summary>
        public Dictionary<string, IconPicker> BaseIcons = new();

        /// <summary>
        /// Icons to display on the map. POIMonsters includes icons for monsters that are in custom category created by user
        /// </summary>
        public Dictionary<int, IconPicker> POIMonsters = new();

        /// <summary>
        /// Icons to display on the map. Breach includes breach chests.
        /// </summary>
        public Dictionary<string, IconPicker> BreachIcons = new();

        /// <summary>
        /// Icons to display on the map. Delirium includes the special spawners and bombs that
        /// delirium brings and they can't be convered by base icons.
        /// </summary>
        public Dictionary<string, IconPicker> DeliriumIcons = new();

        /// <summary>
        /// Icons to display on the map. Delirium includes the special spawners and bombs that
        /// delirium brings and they can't be convered by base icons.
        /// </summary>
        public Dictionary<string, IconPicker> ExpeditionIcons = new();

        /// <summary>
        /// Icons to display on the map. Temple includes the Incursion waygate devices.
        /// </summary>
        public Dictionary<string, IconPicker> TempleIcons = new();

        /// <summary>
        /// Icons to display on the map. Boss arena icons for endgame maps.
        /// </summary>
        public Dictionary<string, IconPicker> BossIcons = new();

        /// <summary>
        /// Gets the boss arena TGT paths and their display names.
        /// </summary>
        [JsonIgnore]
        public Dictionary<string, string> BossArenaTgts = new();

        /// <summary>
        /// Gets the stairs TGT paths and their display names.
        /// </summary>
        [JsonIgnore]
        public Dictionary<string, string> StairsTgts = new();

        /// <summary>
        /// Icons for expedition markers, keyed by display name.
        /// </summary>
        public Dictionary<string, IconPicker> ExpeditionMarkerIcons = new();

        /// <summary>
        /// Icons for expedition remnants with specific mods.
        /// </summary>
        public Dictionary<string, IconPicker> ExpeditionRemnantIcons = new();

        /// <summary>
        /// The group number used for expedition markers in SpecialMiscObjPaths.
        /// </summary>
        [JsonIgnore]
        public const int ExpeditionMarkerGroup = 100;

        /// <summary>
        /// The group number used for expedition remnants in SpecialMiscObjPaths.
        /// </summary>
        [JsonIgnore]
        public const int ExpeditionRemnantGroup = 101;

        /// <summary>
        /// The group number used for Expedition2 encounter objects in SpecialMiscObjPaths.
        /// </summary>
        [JsonIgnore]
        public const int Expedition2EncounterGroup = 102;

        /// <summary>
        /// The group number used for Brequel initiator objects in SpecialMiscObjPaths.
        /// </summary>
        [JsonIgnore]
        public const int BrequelInitiatorGroup = 103;

        /// <summary>
        /// The group number used for ritual rune interactables in SpecialMiscObjPaths.
        /// </summary>
        [JsonIgnore]
        public const int RitualRuneGroup = 104;

        /// <summary>
        /// The group number used for delirium initiator object in SpecialMiscObjPaths.
        /// </summary>
        [JsonIgnore]
        public const int DeliriumInitiatorGroup = 105;

        /// <summary>
        /// The group number used for delirium loathsome mire offering object in SpecialMiscObjPaths.
        /// </summary>
        [JsonIgnore]
        public const int DeliriumMireOfferingGroup = 106;

        /// <summary>
        /// The group number used for delirium loathsome mire portal object in SpecialMiscObjPaths.
        /// </summary>
        [JsonIgnore]
        public const int DeliriumMirePortalGroup = 107;

        /// <summary>
        /// Maps mod name substrings to display names used as keys in ExpeditionRemnantIcons.
        /// </summary>
        [JsonIgnore]
        public static readonly Dictionary<string, string> ExpeditionRemnantModMap = new()
        {
            { "ItemQuantityChest", "Chest Item Quantity Remnant" },
        };

        /// <summary>
        /// Maps MinimapIcon.IconName to display name used as key in ExpeditionMarkerIcons.
        /// </summary>
        [JsonIgnore]
        public static readonly Dictionary<string, string> ExpeditionMarkerIconNameMap = new()
        {
            { "RewardChestExpedition", "Splinter Chest" },
            { "RewardChestArmour", "Armour Chest" },
            { "RewardChestWeapon", "Weapon Chest" },
            { "RewardChestTrinkets", "Trinkets Chest" },
            { "RewardChestCurrency", "Currency Chest" },
            { "RewardChestMaps", "Maps Chest" },
            { "ExpeditionCavernEntrance", "Cavern Entrance" },
        };

        /// <summary>
        /// Icons to display on the map. This list includes icons for
        /// OtherImportantObjects that are in custom category created by user
        /// </summary>
        public Dictionary<int, IconPicker> OtherImportantObjects = new();

        /// <summary>Custom display labels for POI monster icon groups, keyed by group number.</summary>
        public Dictionary<int, string> POIMonsterGroupLabels = new();

        /// <summary>Custom display labels for Special Object icon groups, keyed by group number.</summary>
        public Dictionary<int, string> SpecialObjectGroupLabels = new();

        /// <summary>
        /// Enable/disable flags for entire icon groups, keyed by group heading text.
        /// Missing entries default to enabled.
        /// </summary>
        public Dictionary<string, bool> IconGroupEnabled = new();

        /// <summary>
        /// Enable/disable flags for individual icons within a group, keyed as "GroupName::ItemKey".
        /// Missing entries default to enabled.
        /// </summary>
        public Dictionary<string, bool> IconItemEnabled = new();

        /// <summary>
        /// Runtime-only memoization of composite "Group::Item" lookup keys. Without it,
        /// <see cref="IsItemEnabled"/> allocates a fresh interpolated string on every call, and that
        /// call runs once per drawn entity per frame in the icon loop — tens of thousands of throwaway
        /// strings per second on a busy map. The cache is a pure projection of its inputs, so it never
        /// needs invalidation when the enabled-state dictionaries change. Accessed only from the render
        /// thread (DrawUI / DrawSettings). Private, so it is not serialized.
        /// </summary>
        private readonly Dictionary<(string Group, string Item), string> compositeKeyCache = new();

        /// <summary>Runtime-only memoization of custom-group item keys ("Group N"); see <see cref="GroupItemKey"/>.</summary>
        private readonly Dictionary<int, string> groupItemKeyCache = new();

        /// <summary>Returns true if the icon group is enabled (defaults to true when absent).</summary>
        public bool IsGroupEnabled(string groupName) =>
            !this.IconGroupEnabled.TryGetValue(groupName, out var v) || v;

        /// <summary>Returns true if the individual icon is enabled (defaults to true when absent).</summary>
        public bool IsItemEnabled(string groupName, string itemKey)
        {
            // Fast path: when nothing has been individually disabled the dictionary is empty,
            // so skip building/looking up the composite key entirely.
            if (this.IconItemEnabled.Count == 0)
            {
                return true;
            }

            var tupleKey = (groupName, itemKey);
            if (!this.compositeKeyCache.TryGetValue(tupleKey, out var composite))
            {
                composite = $"{groupName}::{itemKey}";
                this.compositeKeyCache[tupleKey] = composite;
            }

            return !this.IconItemEnabled.TryGetValue(composite, out var v) || v;
        }

        /// <summary>
        /// Returns the per-frame item key for a custom group ("Default Group" for -1, else "Group N"),
        /// memoized to avoid a string allocation per matching entity each frame.
        /// </summary>
        public string GroupItemKey(int customGroup)
        {
            if (customGroup == -1)
            {
                return "Default Group";
            }

            if (!this.groupItemKeyCache.TryGetValue(customGroup, out var key))
            {
                key = $"Group {customGroup}";
                this.groupItemKeyCache[customGroup] = key;
            }

            return key;
        }

        private static string GetPoiGroupDisplayLabel(int key, Dictionary<int, string> labels) =>
            key == -1 ? "Default Group" :
            labels.TryGetValue(key, out var lbl) && !string.IsNullOrWhiteSpace(lbl) ? lbl : $"Group {key}";

        /// <summary>
        /// Draws the icons setting via the ImGui widgets.
        /// </summary>
        /// <param name="headingText">Text to display as heading.</param>
        /// <param name="icons">Icons settings to draw.</param>
        /// <param name="helpingText">helping text to display at the top.</param>
        public void DrawIconsSettingToImGui(
            string headingText,
            Dictionary<string, IconPicker> icons,
            string helpingText)
        {
            bool groupEnabled = this.IsGroupEnabled(headingText);
            if (ImGui.Checkbox($"##group_{headingText}", ref groupEnabled))
                this.IconGroupEnabled[headingText] = groupEnabled;

            ImGui.SameLine();
            var isOpened = ImGui.TreeNode($"{headingText}##treeNode");
            if (!string.IsNullOrEmpty(helpingText))
                ImGuiHelper.ToolTip(helpingText);

            if (isOpened)
            {
                iconGroupSortOrder.TryGetValue(headingText, out int sortMode);
                ImGui.TextDisabled("Sort:");
                ImGui.SameLine();
                if (ImGui.SmallButton(sortMode == 1 ? "[A-Z ^]" : "A-Z ^"))
                    iconGroupSortOrder[headingText] = sortMode == 1 ? 0 : 1;
                ImGui.SameLine();
                if (ImGui.SmallButton(sortMode == -1 ? "[Z-A v]" : "Z-A v"))
                    iconGroupSortOrder[headingText] = sortMode == -1 ? 0 : -1;

                IEnumerable<KeyValuePair<string, IconPicker>> ordered = sortMode switch
                {
                    1 => icons.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase),
                    -1 => icons.OrderByDescending(k => k.Key, StringComparer.OrdinalIgnoreCase),
                    _ => icons,
                };

                ImGui.Columns(2, $"icons columns##{headingText}", false);
                foreach (var icon in ordered)
                {
                    bool itemEnabled = this.IsItemEnabled(headingText, icon.Key);
                    if (ImGui.Checkbox($"##item_{headingText}_{icon.Key}", ref itemEnabled))
                        this.IconItemEnabled[$"{headingText}::{icon.Key}"] = itemEnabled;
                    ImGui.SameLine();
                    ImGui.Text(icon.Key);
                    ImGui.NextColumn();
                    icon.Value.ShowSettingWidget();
                    ImGui.NextColumn();
                }

                ImGui.Columns(1);
                ImGui.TreePop();
            }
        }

        /// <summary>
        ///     draws the POIMonster setting widget.
        /// </summary>
        /// <param name="dllDirectory">directory where the plugin dll is located.</param>
        public void DrawPOIMonsterSettingToImGui(string dllDirectory)
        {
            const string groupName = "Monster POI Icons";
            bool groupEnabled = this.IsGroupEnabled(groupName);
            if (ImGui.Checkbox($"##group_{groupName}", ref groupEnabled))
                this.IconGroupEnabled[groupName] = groupEnabled;

            ImGui.SameLine();
            if (ImGui.TreeNode(groupName))
            {
                iconGroupSortOrder.TryGetValue(groupName, out int sortMode);
                ImGui.TextDisabled("Sort by label:");
                ImGui.SameLine();
                if (ImGui.SmallButton(sortMode == 1 ? "[A-Z ^]##poi" : "A-Z ^##poi"))
                    iconGroupSortOrder[groupName] = sortMode == 1 ? 0 : 1;
                ImGui.SameLine();
                if (ImGui.SmallButton(sortMode == -1 ? "[Z-A v]##poi" : "Z-A v##poi"))
                    iconGroupSortOrder[groupName] = sortMode == -1 ? 0 : -1;

                IEnumerable<KeyValuePair<int, IconPicker>> ordered = sortMode switch
                {
                    1 => this.POIMonsters.OrderBy(k => GetPoiGroupDisplayLabel(k.Key, this.POIMonsterGroupLabels), StringComparer.OrdinalIgnoreCase),
                    -1 => this.POIMonsters.OrderByDescending(k => GetPoiGroupDisplayLabel(k.Key, this.POIMonsterGroupLabels), StringComparer.OrdinalIgnoreCase),
                    _ => this.POIMonsters,
                };

                ImGui.Columns(2, $"icons columns##POIMonsterCol", false);
                List<(int oldKey, int newKey)>? renames = null;
                foreach (var poimonster in ordered)
                {
                    var displayLabel = GetPoiGroupDisplayLabel(poimonster.Key, this.POIMonsterGroupLabels);
                    var itemKey = poimonster.Key == -1 ? "Default Group" : $"Group {poimonster.Key}";
                    bool itemEnabled = this.IsItemEnabled(groupName, itemKey);
                    if (ImGui.Checkbox($"##item_POI_{poimonster.Key}", ref itemEnabled))
                        this.IconItemEnabled[$"{groupName}::{itemKey}"] = itemEnabled;
                    ImGui.SameLine();
                    ImGui.Text(displayLabel);
                    if (poimonster.Key != -1)
                    {
                        if (!poiMonsterLabelStage.TryGetValue(poimonster.Key, out var labelDraft))
                            labelDraft = this.POIMonsterGroupLabels.TryGetValue(poimonster.Key, out var saved) ? saved : string.Empty;
                        ImGui.SetNextItemWidth(120f);
                        if (ImGui.InputTextWithHint($"##lbl_poi_{poimonster.Key}", "Custom label...", ref labelDraft, 64))
                        {
                            poiMonsterLabelStage[poimonster.Key] = labelDraft;
                            this.POIMonsterGroupLabels[poimonster.Key] = labelDraft;
                        }
                        ImGui.SameLine();
                        if (!poiMonsterNumStage.TryGetValue(poimonster.Key, out var pendingNum))
                            pendingNum = poimonster.Key;
                        ImGui.SetNextItemWidth(50f);
                        ImGui.InputInt($"##num_poi_{poimonster.Key}", ref pendingNum, 0, 0);
                        poiMonsterNumStage[poimonster.Key] = pendingNum;
                        ImGui.SameLine();
                        if (ImGui.SmallButton($"Rename##r_poi_{poimonster.Key}")
                            && pendingNum != poimonster.Key
                            && pendingNum >= 0
                            && !this.POIMonsters.ContainsKey(pendingNum))
                        {
                            renames ??= [];
                            renames.Add((poimonster.Key, pendingNum));
                            poiMonsterNumStage.Remove(poimonster.Key);
                        }
                    }

                    ImGui.NextColumn();
                    poimonster.Value.ShowSettingWidget();
                    ImGui.SameLine();
                    if (poimonster.Key != -1 && ImGui.Button($"Delete##{poimonster.Key}"))
                        _ = this.POIMonsters.Remove(poimonster.Key);
                    ImGui.NextColumn();
                }

                ImGui.Columns(1);
                if (renames != null)
                {
                    foreach (var (oldKey, newKey) in renames)
                    {
                        if (!this.POIMonsters.TryGetValue(oldKey, out var icon) || this.POIMonsters.ContainsKey(newKey)) continue;
                        this.POIMonsters.Remove(oldKey);
                        this.POIMonsters[newKey] = icon;
                        if (this.POIMonsterGroupLabels.TryGetValue(oldKey, out var lbl))
                        {
                            this.POIMonsterGroupLabels.Remove(oldKey);
                            this.POIMonsterGroupLabels[newKey] = lbl;
                        }

                        poiMonsterLabelStage.Remove(oldKey);
                        var oldEnableKey = $"{groupName}::Group {oldKey}";
                        if (this.IconItemEnabled.TryGetValue(oldEnableKey, out var en))
                        {
                            this.IconItemEnabled.Remove(oldEnableKey);
                            this.IconItemEnabled[$"{groupName}::Group {newKey}"] = en;
                        }
                    }
                }

                ImGui.Separator();
                ImGui.SetNextItemWidth(ImGui.GetFontSize() * 5);
                if (ImGui.InputInt("Group Number##poimonster", ref poiMonsterGroupNumber) && poiMonsterGroupNumber < 0)
                    poiMonsterGroupNumber = 0;
                ImGui.SameLine();
                if (ImGui.Button("Add##POIMonsterGroupAdd"))
                    this.POIMonsters.TryAdd(poiMonsterGroupNumber, new(Path.Join(dllDirectory, "icons.png"), 12, 44, 30, IconSize));
                ImGui.TreePop();
            }
        }

        /// <summary>
        ///     draws the OtherImportantObjects setting widget.
        /// </summary>
        /// <param name="dllDirectory">directory where the plugin dll is located.</param>
        public void OtherImportantObjectsSettingToImGui(string dllDirectory)
        {
            const string groupName = "Special Objects Icons";
            bool groupEnabled = this.IsGroupEnabled(groupName);
            if (ImGui.Checkbox($"##group_{groupName}", ref groupEnabled))
                this.IconGroupEnabled[groupName] = groupEnabled;

            ImGui.SameLine();
            if (ImGui.TreeNode(groupName))
            {
                iconGroupSortOrder.TryGetValue(groupName, out int sortMode);
                ImGui.TextDisabled("Sort by label:");
                ImGui.SameLine();
                if (ImGui.SmallButton(sortMode == 1 ? "[A-Z ^]##soi" : "A-Z ^##soi"))
                    iconGroupSortOrder[groupName] = sortMode == 1 ? 0 : 1;
                ImGui.SameLine();
                if (ImGui.SmallButton(sortMode == -1 ? "[Z-A v]##soi" : "Z-A v##soi"))
                    iconGroupSortOrder[groupName] = sortMode == -1 ? 0 : -1;

                IEnumerable<KeyValuePair<int, IconPicker>> ordered = sortMode switch
                {
                    1 => this.OtherImportantObjects.OrderBy(k => GetPoiGroupDisplayLabel(k.Key, this.SpecialObjectGroupLabels), StringComparer.OrdinalIgnoreCase),
                    -1 => this.OtherImportantObjects.OrderByDescending(k => GetPoiGroupDisplayLabel(k.Key, this.SpecialObjectGroupLabels), StringComparer.OrdinalIgnoreCase),
                    _ => this.OtherImportantObjects,
                };

                ImGui.Columns(2, $"icons columns##SpecialObjects", false);
                List<(int oldKey, int newKey)>? renames = null;
                foreach (var obj in ordered)
                {
                    var displayLabel = GetPoiGroupDisplayLabel(obj.Key, this.SpecialObjectGroupLabels);
                    var itemKey = obj.Key == -1 ? "Default Group" : $"Group {obj.Key}";
                    bool itemEnabled = this.IsItemEnabled(groupName, itemKey);
                    if (ImGui.Checkbox($"##item_SOI_{obj.Key}", ref itemEnabled))
                        this.IconItemEnabled[$"{groupName}::{itemKey}"] = itemEnabled;
                    ImGui.SameLine();
                    ImGui.Text(displayLabel);
                    // Only user-created groups (key != -1 and key < built-in range) are editable.
                    bool isUserGroup = obj.Key != -1 && obj.Key < ExpeditionMarkerGroup;
                    if (isUserGroup)
                    {
                        if (!specialObjLabelStage.TryGetValue(obj.Key, out var labelDraft))
                            labelDraft = this.SpecialObjectGroupLabels.TryGetValue(obj.Key, out var saved) ? saved : string.Empty;
                        ImGui.SetNextItemWidth(120f);
                        if (ImGui.InputTextWithHint($"##lbl_soi_{obj.Key}", "Custom label...", ref labelDraft, 64))
                        {
                            specialObjLabelStage[obj.Key] = labelDraft;
                            this.SpecialObjectGroupLabels[obj.Key] = labelDraft;
                        }

                        ImGui.SameLine();
                        if (!specialObjNumStage.TryGetValue(obj.Key, out var pendingNum))
                            pendingNum = obj.Key;
                        ImGui.SetNextItemWidth(50f);
                        ImGui.InputInt($"##num_soi_{obj.Key}", ref pendingNum, 0, 0);
                        specialObjNumStage[obj.Key] = pendingNum;
                        ImGui.SameLine();
                        if (ImGui.SmallButton($"Rename##r_soi_{obj.Key}")
                            && pendingNum != obj.Key
                            && pendingNum >= 0
                            && pendingNum < ExpeditionMarkerGroup
                            && !this.OtherImportantObjects.ContainsKey(pendingNum))
                        {
                            renames ??= [];
                            renames.Add((obj.Key, pendingNum));
                            specialObjNumStage.Remove(obj.Key);
                        }
                    }

                    ImGui.NextColumn();
                    obj.Value.ShowSettingWidget();
                    ImGui.SameLine();
                    if (isUserGroup && ImGui.Button($"Delete##{obj.Key}"))
                        _ = this.OtherImportantObjects.Remove(obj.Key);
                    ImGui.NextColumn();
                }

                ImGui.Columns(1);
                if (renames != null)
                {
                    foreach (var (oldKey, newKey) in renames)
                    {
                        if (!this.OtherImportantObjects.TryGetValue(oldKey, out var icon) || this.OtherImportantObjects.ContainsKey(newKey)) continue;
                        this.OtherImportantObjects.Remove(oldKey);
                        this.OtherImportantObjects[newKey] = icon;
                        if (this.SpecialObjectGroupLabels.TryGetValue(oldKey, out var lbl))
                        {
                            this.SpecialObjectGroupLabels.Remove(oldKey);
                            this.SpecialObjectGroupLabels[newKey] = lbl;
                        }

                        specialObjLabelStage.Remove(oldKey);
                        var oldEnableKey = $"{groupName}::Group {oldKey}";
                        if (this.IconItemEnabled.TryGetValue(oldEnableKey, out var en))
                        {
                            this.IconItemEnabled.Remove(oldEnableKey);
                            this.IconItemEnabled[$"{groupName}::Group {newKey}"] = en;
                        }
                    }
                }

                ImGui.Separator();
                ImGui.SetNextItemWidth(ImGui.GetFontSize() * 5);
                if (ImGui.InputInt("Group Number##SpecialObjects", ref poiSpecialObjectGroupNumber) && poiSpecialObjectGroupNumber < 0)
                    poiSpecialObjectGroupNumber = 0;
                ImGui.SameLine();
                if (ImGui.Button("Add##SpecialObjects"))
                    this.OtherImportantObjects.TryAdd(poiSpecialObjectGroupNumber, new(Path.Join(dllDirectory, "icons.png"), 1, 37, 30, IconSize));
                ImGui.TreePop();
            }
        }

        /// <summary>
        /// Re-resolves all serialized icon paths against <paramref name="dllDirectory"/>.
        /// Call this immediately after JSON deserialization so that icons remain valid
        /// when the installation folder has been renamed or moved.
        /// </summary>
        /// <param name="dllDirectory">The current plugin DLL directory.</param>
        public void ReinitializeIconPaths(string dllDirectory)
        {
            foreach (var icon in this.BaseIcons.Values) icon.ReinitializeFromDirectory(dllDirectory);
            foreach (var icon in this.POIMonsters.Values) icon.ReinitializeFromDirectory(dllDirectory);
            foreach (var icon in this.BreachIcons.Values) icon.ReinitializeFromDirectory(dllDirectory);
            foreach (var icon in this.DeliriumIcons.Values) icon.ReinitializeFromDirectory(dllDirectory);
            foreach (var icon in this.ExpeditionIcons.Values) icon.ReinitializeFromDirectory(dllDirectory);
            foreach (var icon in this.TempleIcons.Values) icon.ReinitializeFromDirectory(dllDirectory);
            foreach (var icon in this.BossIcons.Values) icon.ReinitializeFromDirectory(dllDirectory);
            foreach (var icon in this.ExpeditionMarkerIcons.Values) icon.ReinitializeFromDirectory(dllDirectory);
            foreach (var icon in this.ExpeditionRemnantIcons.Values) icon.ReinitializeFromDirectory(dllDirectory);
            foreach (var icon in this.OtherImportantObjects.Values) icon.ReinitializeFromDirectory(dllDirectory);
        }

        /// <summary>
        /// Adds the default icons if the setting file isn't available.
        /// </summary>
        /// <param name="dllDirectory">directory where the plugin dll is located.</param>
        public void AddDefaultIcons(string dllDirectory)
        {
            var basicIconPathName = Path.Join(dllDirectory, "icons.png");
            this.AddDefaultBaseGameIcons(basicIconPathName);
            this.AddDefaultPOIMonsterIcons(basicIconPathName);
            this.AddDefaultOtherImportantObjectsIcons(basicIconPathName);
            this.AddDefaultBreachIcons(basicIconPathName);
            this.AddDefaultDeliriumIcons(basicIconPathName);
            this.AddDefaultExpeditionIcons(basicIconPathName);
            this.AddDefaultExpeditionMarkerIcons(basicIconPathName);
            this.AddDefaultExpeditionRemnantIcons(basicIconPathName);
            this.AddDefaultTempleIcons(basicIconPathName);
            this.AddDefaultBossIcons(basicIconPathName);
        }

        private void AddDefaultBaseGameIcons(string iconPathName)
        {
            this.BaseIcons.TryAdd("Self", new IconPicker(iconPathName, 0, 0, 20, IconSize));
            this.BaseIcons.TryAdd("Player", new IconPicker(iconPathName, 2, 0, 20, IconSize));
            this.BaseIcons.TryAdd("Leader", new IconPicker(iconPathName, 3, 1, 20, IconSize));
            this.BaseIcons.TryAdd("NPC", new IconPicker(iconPathName, 3, 0, 30, IconSize));
            this.BaseIcons.TryAdd("Special NPC", new IconPicker(iconPathName, 13, 42, 100, IconSize));
            this.BaseIcons.TryAdd("Strongbox", new IconPicker(iconPathName, 8, 38, 30, IconSize));
            this.BaseIcons.TryAdd("Magic Chests", new IconPicker(iconPathName, 1, 13, 20, IconSize));
            this.BaseIcons.TryAdd("Rare Chests", new IconPicker(iconPathName, 4, 48, 20, IconSize));
            this.BaseIcons.TryAdd("All Other Chest", new IconPicker(iconPathName, 6, 9, 20, IconSize));

            this.BaseIcons.TryAdd("Shrine", new IconPicker(iconPathName, 7, 0, 30, IconSize));

            this.BaseIcons.TryAdd("Friendly", new IconPicker(iconPathName, 1, 0, 10, IconSize));
            this.BaseIcons.TryAdd("Normal Monster", new IconPicker(iconPathName, 0, 14, 20, IconSize));
            this.BaseIcons.TryAdd("Magic Monster", new IconPicker(iconPathName, 6, 3, 20, IconSize));
            this.BaseIcons.TryAdd("Rare Monster", new IconPicker(iconPathName, 4, 57, 30, IconSize));
            this.BaseIcons.TryAdd("Unique Monster", new IconPicker(iconPathName, 6, 57, 30, IconSize));
            this.BaseIcons.TryAdd("Pinnacle Boss Not Attackable", new IconPicker(iconPathName, 5, 15, 30, IconSize));

            this.BaseIcons.TryAdd("Yellow Bestiary Monster", new IconPicker(iconPathName, 6, 2, 35, IconSize));
            this.BaseIcons.TryAdd("Red Bestiary Monster", new IconPicker(iconPathName, 7, 2, 35, IconSize));

            this.BaseIcons.TryAdd("Stairs", new IconPicker(iconPathName, 4, 1, 40, IconSize));
        }

        private void AddDefaultPOIMonsterIcons(string iconPathName)
        {
            this.POIMonsters.TryAdd(-1, new IconPicker(iconPathName, 12, 44, 30, IconSize));
        }

        private void AddDefaultOtherImportantObjectsIcons(string iconPathName)
        {
            this.OtherImportantObjects.TryAdd(-1, new IconPicker(iconPathName, 1, 37, 30, IconSize));
            this.OtherImportantObjects.TryAdd(Expedition2EncounterGroup, new IconPicker(iconPathName, 4, 71, 30, IconSize));
            this.OtherImportantObjects.TryAdd(BrequelInitiatorGroup, new IconPicker(iconPathName, 1, 2, 30, IconSize));
            this.OtherImportantObjects.TryAdd(RitualRuneGroup, new IconPicker(iconPathName, 8, 40, 30, IconSize));
            this.OtherImportantObjects.TryAdd(DeliriumInitiatorGroup, new IconPicker(iconPathName, 1, 15, 30, IconSize));
            this.OtherImportantObjects.TryAdd(DeliriumMireOfferingGroup, new IconPicker(iconPathName, 0, 12, 30, IconSize));
            this.OtherImportantObjects.TryAdd(DeliriumMirePortalGroup, new IconPicker(iconPathName, 5, 14, 30, IconSize));
        }

        private void AddDefaultBreachIcons(string iconPathName)
        {
            this.BreachIcons.TryAdd("Breach Chest", new IconPicker(iconPathName, 6, 41, 30, IconSize));
        }

        private void AddDefaultDeliriumIcons(string iconPathName)
        {
            this.DeliriumIcons.TryAdd("Delirium Bomb", new IconPicker(iconPathName, 5, 0, 30, IconSize));
            this.DeliriumIcons.TryAdd("Delirium Spawner", new IconPicker(iconPathName, 6, 0, 30, IconSize));
        }

        private void AddDefaultExpeditionIcons(string iconPathName)
        {
            this.ExpeditionIcons.TryAdd("Generic Expedition Chests", new IconPicker(iconPathName, 5, 41, 30, IconSize));
        }

        private void AddDefaultTempleIcons(string iconPathName)
        {
            this.TempleIcons.TryAdd("Vaal Ruins", new IconPicker(iconPathName, 9, 2, 75, IconSize));
        }

        private void AddDefaultExpeditionMarkerIcons(string iconPathName)
        {
            this.ExpeditionMarkerIcons.TryAdd("Splinter Chest", new IconPicker(iconPathName, 4, 40, 90, IconSize));
            this.ExpeditionMarkerIcons.TryAdd("Armour Chest", new IconPicker(iconPathName, 1, 39, 0, IconSize));
            this.ExpeditionMarkerIcons.TryAdd("Weapon Chest", new IconPicker(iconPathName, 2, 39, 0, IconSize));
            this.ExpeditionMarkerIcons.TryAdd("Trinkets Chest", new IconPicker(iconPathName, 0, 39, 0, IconSize));
            this.ExpeditionMarkerIcons.TryAdd("Currency Chest", new IconPicker(iconPathName, 10, 38, 0, IconSize));
            this.ExpeditionMarkerIcons.TryAdd("Maps Chest", new IconPicker(iconPathName, 13, 38, 0, IconSize));
            this.ExpeditionMarkerIcons.TryAdd("Cavern Entrance", new IconPicker(iconPathName, 0, 2, 90, IconSize));
            this.ExpeditionMarkerIcons.TryAdd("Logbook", new IconPicker(iconPathName, 4, 40, 90, IconSize));
        }

        private void AddDefaultExpeditionRemnantIcons(string iconPathName)
        {
            this.ExpeditionRemnantIcons.TryAdd("Chest Item Quantity Remnant", new IconPicker(iconPathName, 11, 40, 100, IconSize));
        }

        private void AddDefaultBossIcons(string iconPathName)
        {
            this.BossIcons.TryAdd("Boss Arena", new IconPicker(iconPathName, 6, 57, 50, IconSize));
        }

    }
}
