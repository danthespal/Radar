namespace OriathHub.Plugins.Radar
{
    using Newtonsoft.Json;
    using Newtonsoft.Json.Converters;

    /// <summary>
    ///     Whether a <see cref="TargetDescription"/> is matched against terrain tile names or live entity paths.
    /// </summary>
    [JsonConverter(typeof(StringEnumConverter))]
    public enum TargetType
    {
        /// <summary>
        ///     Matched against <see cref="OriathHub.RemoteObjects.States.InGameStateObjects.AreaInstance.TgtTilesLocations"/> keys.
        /// </summary>
        Tile,

        /// <summary>
        ///     Matched against newly-seen <see cref="OriathHub.RemoteObjects.States.InGameStateObjects.Entity.Path"/> values.
        /// </summary>
        Entity,
    }
}
