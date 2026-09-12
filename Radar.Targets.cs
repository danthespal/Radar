namespace OriathHub.Plugins.Radar
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Numerics;
    using System.Text.RegularExpressions;
    using Coroutine;
    using Newtonsoft.Json;
    using OriathHub.CoroutineEvents;
    using OriathHub.RemoteEnums;
    using OriathHub.RemoteObjects.Components;
    using OriathHub.RemoteObjects.States.InGameStateObjects;
    using OriathHub.Utils;

    /// <summary>
    ///     The <c>targets.json</c>-driven POI engine: wildcard tile/entity matching, k-means clustering
    ///     into each target's <see cref="TargetDescriptionAlternative.ExpectedCount"/>, and incremental entity tracking.
    /// </summary>
    public sealed partial class Radar
    {
        /// <summary>
        ///     One target's resolved map marker locations for the current area.
        /// </summary>
        private sealed record ClusteredTarget(TargetDescription Target, Vector2[] Locations);

        private string TargetsJsonPathName => Path.Join(this.DllDirectory, "targets.json");

        /// <summary>
        ///     Everything loaded from targets.json, keyed by area id (plus a "common" bucket).
        /// </summary>
        private Dictionary<string, List<TargetDescription>> _targetDescriptions = new();

        /// <summary>
        ///     Targets applicable to the current area (own + "common"), keyed by <see cref="TargetDescription.EqualityId"/>.
        /// </summary>
        private Dictionary<string, TargetDescription> _targetDescriptionsInArea = new();

        /// <summary>
        ///     Compiled Name patterns for this area's entity-type targets, checked against newly-seen <see cref="Entity.Path"/> values.
        /// </summary>
        private List<(Regex Pattern, TargetDescription Target)> _currentZoneEntityPatterns = new();

        /// <summary>
        ///     Name/path -> matched grid locations for the current area. Seeded from
        ///     <see cref="AreaInstance.TgtTilesLocations"/> at area change; entity-type target sightings
        ///     are folded in incrementally under their own <see cref="Entity.Path"/> key, exactly like the
        ///     tile entries, so <see cref="ClusterTarget"/> matches both uniformly.
        /// </summary>
        private Dictionary<string, List<Vector2>> _allTargetLocations = new();

        /// <summary>
        ///     Resolved marker locations for the current area's targets, keyed by <see cref="TargetDescription.EqualityId"/>. Drives drawing and pathfinding.
        /// </summary>
        private Dictionary<string, ClusteredTarget> _clusteredTargets = new();

        /// <summary>
        ///     The current area's rooms, used to resolve a target's <see cref="TargetDescriptionAlternative.Rooms"/>
        ///     patterns into the rectangles its matches must fall inside.
        /// </summary>
        private IReadOnlyList<AreaRoom> _areaRooms = Array.Empty<AreaRoom>();

        /// <summary>
        ///     Rooms already claimed by a specifically-named target this update, so a generic
        ///     <c>Name: "*"</c> + <c>Rooms</c> rule (e.g. "Boss Room") does not also draw a redundant
        ///     label on top of a more specific one covering the same room (e.g. "Crop Circle").
        /// </summary>
        private HashSet<AreaRoom> _occupiedRooms = new();

        private void LoadTargets()
        {
            this._targetDescriptions = new();
            if (!File.Exists(this.TargetsJsonPathName))
            {
                return;
            }

            try
            {
                var json = File.ReadAllText(this.TargetsJsonPathName);
                this._targetDescriptions =
                    JsonConvert.DeserializeObject<Dictionary<string, List<TargetDescription>>>(json) ?? new();
            }
            catch (Exception ex) when (ex is IOException or JsonException)
            {
                Log.Error($"Unable to load Radar targets from {this.TargetsJsonPathName}: {ex.Message}", this.Name);
            }
        }

        private void SaveTargets()
        {
            var json = JsonConvert.SerializeObject(this._targetDescriptions, Formatting.Indented);
            File.WriteAllText(this.TargetsJsonPathName, json);
        }

        /// <summary>
        ///     Re-resolves targets for the current area. Call after <see cref="currentAreaName"/> changes.
        ///     An area key matches when it is the literal "common" bucket (OriathHub's own hand-added-POI
        ///     convention) or when it wildcard-matches <see cref="currentAreaName"/> (e.g. seeded "*"/"Sanctum*"
        ///     buckets, mirroring the source targets.json's own area-key semantics).
        /// </summary>
        private void UpdateCurrentAreaTargets()
        {
            var descriptions = new List<TargetDescription>();
            foreach (var (areaKey, areaTargets) in this._targetDescriptions)
            {
                if (areaKey == "common" || ToLikeRegex(areaKey).IsMatch(this.currentAreaName))
                {
                    descriptions.AddRange(areaTargets);
                }
            }

            this._targetDescriptionsInArea = descriptions.DistinctBy(x => x.EqualityId).ToDictionary(x => x.EqualityId);
            this._currentZoneEntityPatterns = this._targetDescriptionsInArea.Values
                .Where(x => x.TargetType == TargetType.Entity)
                .DistinctBy(x => x.Name)
                .Select(x => (ToLikeRegex(x.Name), x))
                .ToList();

            this._allTargetLocations = new Dictionary<string, List<Vector2>>(
                Core.States.InGameStateObject.CurrentAreaInstance.TgtTilesLocations);
            this._areaRooms = Core.States.InGameStateObject.CurrentAreaInstance.Rooms;

            var resolved = new List<(TargetDescription Target, Vector2[] Locations, bool IsGenericRoomMatch)>();
            foreach (var target in this._targetDescriptionsInArea.Values)
            {
                var (locations, isGenericRoomMatch) = this.ClusterTarget(target);
                if (locations is { Length: > 0 })
                {
                    resolved.Add((target, locations, isGenericRoomMatch));
                }
            }

            this._occupiedRooms = this.FindOccupiedRooms(resolved);

            var result = new Dictionary<string, ClusteredTarget>();
            foreach (var (target, locations, isGenericRoomMatch) in resolved)
            {
                var finalLocations = isGenericRoomMatch ? this.ExcludeOccupiedRooms(locations) : locations;
                if (finalLocations.Length > 0)
                {
                    result[target.EqualityId] = new ClusteredTarget(target, finalLocations);
                }
            }

            this._clusteredTargets = result;
        }

        /// <summary>
        ///     Rooms already covered by a specifically-named target's marker, for suppressing redundant
        ///     generic <c>Name: "*"</c> + <c>Rooms</c> markers in the same room. See <see cref="_occupiedRooms"/>.
        /// </summary>
        private HashSet<AreaRoom> FindOccupiedRooms(List<(TargetDescription Target, Vector2[] Locations, bool IsGenericRoomMatch)> resolved)
        {
            var occupied = new HashSet<AreaRoom>();
            foreach (var (_, locations, isGenericRoomMatch) in resolved)
            {
                if (isGenericRoomMatch)
                {
                    continue;
                }

                foreach (var location in locations)
                {
                    foreach (var room in this._areaRooms)
                    {
                        if (room.ContainsGridPosition(location))
                        {
                            occupied.Add(room);
                        }
                    }
                }
            }

            return occupied;
        }

        private Vector2[] ExcludeOccupiedRooms(Vector2[] locations) =>
            locations.Where(location => !this._occupiedRooms.Any(room => room.ContainsGridPosition(location))).ToArray();

        /// <summary>
        ///     Tries the target's primary pattern, then each <see cref="TargetDescription.Alternatives"/> in order; the first with any match wins.
        ///     <c>IsGenericRoomMatch</c> is <c>true</c> when the winning pattern was a bare <c>"*"</c> narrowed only by
        ///     <see cref="TargetDescriptionAlternative.Rooms"/> (e.g. "Boss Room"), so callers can defer it to a more
        ///     specifically-named target covering the same room.
        /// </summary>
        private (Vector2[]? Locations, bool IsGenericRoomMatch) ClusterTarget(TargetDescription target)
        {
            foreach (var alt in ((IEnumerable<TargetDescriptionAlternative>)(target.Alternatives ?? Array.Empty<TargetDescriptionAlternative>())).Prepend(target))
            {
                var matched = this.ClusterPattern(alt.Name, alt.Rooms, alt.ExpectedCount, target.TargetType);
                if (matched != null)
                {
                    return (matched, alt.Name == "*" && alt.Rooms is { Length: > 0 });
                }
            }

            return (null, false);
        }

        private Vector2[]? ClusterPattern(string namePattern, string[]? rooms, int expectedCount, TargetType targetType)
        {
            if (string.IsNullOrEmpty(namePattern))
            {
                return null;
            }

            // Tile keys carry a "x:N-y:N" coordinate suffix a Name pattern usually doesn't include, so
            // tile patterns get an implicit trailing wildcard; entity Path values have no such suffix.
            var regex = targetType == TargetType.Tile ? ToTileNameRegex(namePattern) : ToLikeRegex(namePattern);
            var points = this._allTargetLocations.Where(x => regex.IsMatch(x.Key)).SelectMany(x => x.Value).ToList();
            if (rooms is { Length: > 0 })
            {
                points = this.FilterToRooms(points, rooms);
            }

            return points.Count == 0 ? null : ClusterPoints(points, expectedCount);
        }

        /// <summary>
        ///     Narrows matched locations to those inside a room whose asset path matches one of
        ///     <paramref name="roomPatterns"/>. This is what makes a deliberately broad pattern
        ///     (often just "*") resolve to one specific named room, e.g. "*campsite*".
        /// </summary>
        private List<Vector2> FilterToRooms(List<Vector2> points, string[] roomPatterns)
        {
            var regexes = roomPatterns.Select(ToLikeRegex).ToArray();
            var matchedRooms = this._areaRooms.Where(room => regexes.Any(x => x.IsMatch(room.Name))).ToList();
            return matchedRooms.Count == 0
                ? new List<Vector2>()
                : points.Where(point => matchedRooms.Any(room => room.ContainsGridPosition(point))).ToList();
        }

        /// <summary>
        ///     Reduces raw terrain-tile matches to the configured number of map markers. A target's
        ///     <c>ExpectedCount</c> is a marker count, including when it is one; it is never a signal
        ///     to draw every raw tile match.
        /// </summary>
        private Vector2[] ClusterPoints(List<Vector2> points, int expectedCount)
        {
            var uniquePoints = points.Distinct().ToArray();
            if (uniquePoints.Length == 0)
            {
                return Array.Empty<Vector2>();
            }

            var markerCount = Math.Clamp(expectedCount, 1, uniquePoints.Length);
            var clusterIndexes = KMeans.Cluster(
                uniquePoints.Select(p => new Vector2d(p.X, p.Y)).ToArray(),
                markerCount);
            var result = new List<Vector2>();
            foreach (var group in uniquePoints.Zip(clusterIndexes).GroupBy(x => x.Second))
            {
                var avg = new Vector2d(group.Average(x => x.First.X), group.Average(x => x.First.Y));
                result.Add(this.SelectWalkableMarker(group.Select(x => x.First), avg));
            }

            return result.Distinct().ToArray();
        }

        /// <summary>
        ///     Selects a visible/pathable representative for a cluster. Target-file anchors often sit
        ///     on terrain edges, so prefer a walkable source point and otherwise snap the centroid to
        ///     the nearest walkable grid cell.
        /// </summary>
        private Vector2 SelectWalkableMarker(IEnumerable<Vector2> points, Vector2d centroid)
        {
            var ordered = points
                .OrderBy(point => (new Vector2d(point.X, point.Y) - centroid).Length)
                .ToArray();
            foreach (var point in ordered)
            {
                if (this.IsWalkableGridPoint(point))
                {
                    return point;
                }
            }

            return this.FindNearestWalkableGridPoint(centroid) ?? ordered[0];
        }

        private bool IsWalkableGridPoint(Vector2 point)
        {
            var instance = Core.States.InGameStateObject.CurrentAreaInstance;
            var bytesPerRow = instance.TerrainMetadata.BytesPerRow;
            var data = instance.GridWalkableData;
            var x = (int)point.X;
            var y = (int)point.Y;
            if (bytesPerRow <= 0 || x < 0 || y < 0 || x >= bytesPerRow * 2 || y >= data.Length / bytesPerRow)
            {
                return false;
            }

            var value = (data[(y * bytesPerRow) + (x / 2)] >> ((x & 1) * 4)) & 0xF;
            return value != 0;
        }

        private Vector2? FindNearestWalkableGridPoint(Vector2d centroid)
        {
            var instance = Core.States.InGameStateObject.CurrentAreaInstance;
            var bytesPerRow = instance.TerrainMetadata.BytesPerRow;
            var data = instance.GridWalkableData;
            if (bytesPerRow <= 0 || data.Length == 0)
            {
                return null;
            }

            var centerX = Math.Clamp((int)Math.Round(centroid.X), 0, (bytesPerRow * 2) - 1);
            var centerY = Math.Clamp((int)Math.Round(centroid.Y), 0, (data.Length / bytesPerRow) - 1);
            var center = new Vector2(centerX, centerY);
            if (this.IsWalkableGridPoint(center))
            {
                return center;
            }

            const int maximumSearchRadius = 64;
            for (var radius = 1; radius <= maximumSearchRadius; radius++)
            {
                for (var y = centerY - radius; y <= centerY + radius; y++)
                {
                    for (var x = centerX - radius; x <= centerX + radius; x++)
                    {
                        if (Math.Abs(x - centerX) != radius && Math.Abs(y - centerY) != radius)
                        {
                            continue;
                        }

                        var candidate = new Vector2(x, y);
                        if (this.IsWalkableGridPoint(candidate))
                        {
                            return candidate;
                        }
                    }
                }
            }

            return null;
        }

        /// <summary>
        ///     Wildcard ('*'/'?') pattern to regex, matching the source targets.json's own Like() semantics exactly (no implicit suffix). Used for area keys and entity Path patterns.
        /// </summary>
        private static Regex ToLikeRegex(string pattern)
        {
            var escaped = Regex.Escape(pattern).Replace(@"\*", ".*").Replace(@"\?", ".");
            return new Regex("^" + escaped + "$", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        }

        /// <summary>
        ///     Like <see cref="ToLikeRegex"/>, but a pattern with no trailing '*' gets one appended so it still matches a tile key's "x:N-y:N" coordinate suffix.
        /// </summary>
        private static Regex ToTileNameRegex(string pattern)
        {
            var escaped = Regex.Escape(pattern).Replace(@"\*", ".*").Replace(@"\?", ".");
            if (!pattern.EndsWith("*", StringComparison.Ordinal))
            {
                escaped += ".*";
            }

            return new Regex("^" + escaped + "$", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        }

        /// <summary>
        ///     Folds a newly-seen entity into <see cref="_allTargetLocations"/> and re-clusters only the entity-type targets it matches.
        /// </summary>
        private void OnEntityAdded(Entity entity)
        {
            if (this._currentZoneEntityPatterns.Count == 0)
            {
                return;
            }

            var path = entity.Path;
            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            var matchedTargets = this._currentZoneEntityPatterns.Where(x => x.Pattern.IsMatch(path)).Select(x => x.Target).ToList();
            if (matchedTargets.Count == 0)
            {
                return;
            }

            if (!entity.TryGetComponent<Render>(out var render))
            {
                return;
            }

            var pos = new Vector2(render.GridPosition.X, render.GridPosition.Y);
            bool isNew;
            if (this._allTargetLocations.TryGetValue(path, out var existing))
            {
                isNew = !existing.Contains(pos);
                if (isNew)
                {
                    existing.Add(pos);
                }
            }
            else
            {
                this._allTargetLocations[path] = new List<Vector2> { pos };
                isNew = true;
            }

            if (!isNew)
            {
                return;
            }

            foreach (var target in matchedTargets)
            {
                var (locations, isGenericRoomMatch) = this.ClusterTarget(target);
                if (locations is { Length: > 0 })
                {
                    var finalLocations = isGenericRoomMatch ? this.ExcludeOccupiedRooms(locations) : locations;
                    if (finalLocations.Length > 0)
                    {
                        this._clusteredTargets[target.EqualityId] = new ClusteredTarget(target, finalLocations);
                    }
                }
            }
        }

        /// <summary>
        ///     Whether a target is a simple hand-added single-tile entry (as opposed to a wildcard/clustered/alternatives entry from a seeded targets.json).
        /// </summary>
        private static bool IsLiteralTarget(TargetDescription target) =>
            target.TargetType == TargetType.Tile
            && target.Rooms == null
            && target.ExpectedCount <= 1
            && (target.Alternatives == null || target.Alternatives.Length == 0)
            && !target.Name.Contains('*') && !target.Name.Contains('?');

        /// <summary>
        ///     Literal (hand-added) targets for one area/"common" bucket, for the "Add/Modify POI" curation UI.
        /// </summary>
        private IEnumerable<TargetDescription> GetLiteralTargets(string area) =>
            this._targetDescriptions.TryGetValue(area, out var list) ? list.Where(IsLiteralTarget) : Enumerable.Empty<TargetDescription>();

        private bool HasLiteralTarget(string area, string tileKey) =>
            this.GetLiteralTargets(area).Any(x => x.Name == tileKey);

        private string? GetLiteralTargetDisplayName(string area, string tileKey) =>
            this.GetLiteralTargets(area).FirstOrDefault(x => x.Name == tileKey)?.DisplayName;

        /// <summary>
        ///     Adds or renames a hand-added single-tile POI, refreshing the live target store if it affects the current area.
        /// </summary>
        private void SetLiteralTarget(string area, string tileKey, string displayName)
        {
            if (!this._targetDescriptions.TryGetValue(area, out var list))
            {
                list = new List<TargetDescription>();
                this._targetDescriptions[area] = list;
            }

            var existingIndex = list.FindIndex(x => x.Name == tileKey && IsLiteralTarget(x));
            var updated = new TargetDescription { Name = tileKey, DisplayName = displayName, ExpectedCount = 1 };
            if (existingIndex >= 0)
            {
                list[existingIndex] = updated;
            }
            else
            {
                list.Add(updated);
            }

            if (area == this.currentAreaName || area == "common")
            {
                this.UpdateCurrentAreaTargets();
            }
        }

        /// <summary>
        ///     Removes a hand-added single-tile POI, refreshing the live target store if it affects the current area.
        /// </summary>
        private void RemoveLiteralTarget(string area, string tileKey)
        {
            if (this._targetDescriptions.TryGetValue(area, out var list) &&
                list.RemoveAll(x => x.Name == tileKey && IsLiteralTarget(x)) > 0 &&
                (area == this.currentAreaName || area == "common"))
            {
                this.UpdateCurrentAreaTargets();
            }
        }

        /// <summary>
        ///     Polls newly-spawned entities each frame for entity-type target matches.
        /// </summary>
        private IEnumerator<Wait> CheckEntityTargets()
        {
            while (true)
            {
                yield return new Wait(OriathEvents.PerFrameDataUpdate);
                if (Core.States.GameCurrentState is not (GameStateTypes.InGameState or GameStateTypes.EscapeState))
                {
                    continue;
                }

                foreach (var entity in Core.States.InGameStateObject.CurrentAreaInstance.EntitiesAddedThisFrame)
                {
                    this.OnEntityAdded(entity);
                }
            }
        }
    }
}
