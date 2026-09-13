namespace OriathHub.Plugins.Radar
{
    using System;

    /// <summary>
    ///     One alternative name/room/count triple a <see cref="TargetDescription"/> can fall back to
    ///     when its primary pattern matches nothing in the current area.
    /// </summary>
    public record TargetDescriptionAlternative
    {
        /// <summary>
        ///     Wildcard ('*'/'?') pattern matched against a tile's composite key (for
        ///     <see cref="TargetType.Tile"/>) or an entity's <c>Path</c> (for <see cref="TargetType.Entity"/>).
        /// </summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>
        ///     Optional wildcard patterns matched against the room asset paths of
        ///     <see cref="OriathHub.RemoteObjects.States.InGameStateObjects.AreaInstance.Rooms"/>. When set,
        ///     only matches that fall inside one of those rooms count, which is how a broad pattern
        ///     (often just "*") is narrowed to a single named room such as "*campsite*".
        /// </summary>
        public string[]? Rooms { get; set; }

        /// <summary>How many distinct map markers this target's matches should be clustered into.</summary>
        public int ExpectedCount { get; set; } = 1;
    }

    /// <summary>
    ///     One named point-of-interest entry in <c>targets.json</c>, matched by wildcard pattern and
    ///     clustered into <see cref="TargetDescriptionAlternative.ExpectedCount"/> map markers.
    /// </summary>
    public record TargetDescription : TargetDescriptionAlternative
    {
        /// <summary>
        ///     Label drawn on the map and used for pathfinding/settings keys. Falls back to <see cref="TargetDescriptionAlternative.Name"/> when blank.
        /// </summary>
        public string? DisplayName { get; set; }

        /// <summary>
        ///     Whether this target is matched against terrain tiles or live entities.
        /// </summary>
        public TargetType TargetType { get; set; } = TargetType.Tile;

        /// <summary>
        ///     Optional ABGR hex color override for this target's path line, e.g. "FF3020FF".
        /// </summary>
        public string? Color { get; set; }

        /// <summary>
        ///     Fallback patterns tried, in order, when <see cref="TargetDescriptionAlternative.Name"/>
        ///     matches nothing in the current area (e.g. across patch renames).
        /// </summary>
        public TargetDescriptionAlternative[]? Alternatives { get; set; }

        /// <summary>
        ///     Identity used to dedupe/key this target within an area. Matches the source's own
        ///     <c>Name#Rooms</c> composite (deliberately excluding <see cref="DisplayName"/>): two entries
        ///     with the same pattern/rooms collapse to one, which is how an area-specific override (e.g. a
        ///     boss room's contextual label) takes precedence over a same-shaped all-areas fallback (e.g.
        ///     the generic "Boss Room" rule) instead of both rendering on top of each other.
        /// </summary>
        internal string EqualityId => $"{this.Name}#{string.Join(",", this.Rooms ?? Array.Empty<string>())}";
    }
}
