namespace OriathHub.Plugins.Radar
{
    using System;

    /// <summary>
    /// Helper for LargeMap final scaling correction.
    /// Observed behavior: scale is primarily a function of aspect ratio (H/W).
    /// </summary>
    internal static class LargeMapScaleFix
    {
        // Fitted from empirical data:
        // 16:9  -> ~0.174
        // 16:10 -> ~0.188
        // 4:3   -> ~0.213
        //
        // ScaleFix ~= A + B * (H/W)
        private const float A = 0.05556058f;
        private const float B = 0.21062898f;

        /// <summary>
        /// Computes the LargeMapScaleFix using viewport dimensions (client area / backbuffer size).
        /// Use the true rendered viewport if possible (DX viewport/backbuffer) for best accuracy.
        /// </summary>
        /// <param name="viewportWidth">Viewport width in pixels.</param>
        /// <param name="viewportHeight">Viewport height in pixels.</param>
        /// <param name="uiScale">
        /// Optional extra multiplier if a UI scale factor (DPI/UI scaling) is later identified.
        /// Keep 1.0f if unknown.
        /// </param>
        /// <returns>The scale-fix multiplier to apply to the large map icons.</returns>
        public static float Compute(int viewportWidth, int viewportHeight, float uiScale = 1.0f)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(viewportWidth);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(viewportHeight);
            if (!float.IsFinite(uiScale) || uiScale <= 0f) throw new ArgumentOutOfRangeException(nameof(uiScale));

            // Aspect ratio driver (H/W), not (W/H).
            float hw = (float)viewportHeight / viewportWidth;

            // Base fix from empirical fit.
            float fix = A + (B * hw);

            // Optional external scaling (UI scale / DPI / viewport-to-backbuffer ratio)
            // if it is later identified as a separate multiplicative factor.
            fix *= uiScale;

            return fix;
        }
    }
}
