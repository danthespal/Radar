namespace OriathHub.Plugins.Radar
{
    using System;
    using System.Reflection;

    /// <summary>
    ///     Reads this plugin's version from its own assembly metadata, so the version lives in exactly
    ///     one place — <c>&lt;Version&gt;</c> in the .csproj, which the build turns into
    ///     <see cref="AssemblyInformationalVersionAttribute" />.
    /// </summary>
    internal static class PluginVersion
    {
        private static readonly Lazy<string> CachedVersion = new(GetAssemblyVersion);

        /// <summary>Gets the version of this plugin build.</summary>
        internal static string Value => CachedVersion.Value;

        private static string GetAssemblyVersion()
        {
            var assembly = typeof(PluginVersion).Assembly;

            var informationalVersion = assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                .InformationalVersion;

            if (!string.IsNullOrWhiteSpace(informationalVersion))
            {
                return NormalizeVersion(informationalVersion);
            }

            var assemblyNameVersion = assembly.GetName().Version;

            if (assemblyNameVersion is not null)
            {
                return assemblyNameVersion.ToString();
            }

            return "unknown";
        }

        /// <summary>
        ///     Strips the <c>+buildmetadata</c> suffix SourceLink appends (the commit sha), leaving the
        ///     SemVer core plus any prerelease tag.
        /// </summary>
        private static string NormalizeVersion(string version)
        {
            var metadataIndex = version.IndexOf('+');

            if (metadataIndex >= 0)
            {
                return version[..metadataIndex];
            }

            return version;
        }
    }
}
