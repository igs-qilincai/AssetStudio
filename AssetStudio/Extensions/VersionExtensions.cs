namespace AssetStudio
{
    public static class VersionExtensions
    {
        /// <summary>
        /// Compares a parsed Unity version (e.g. "6000.3.24f1" -> {6000, 3, 24, 1}) against major.minor.patch.
        /// </summary>
        public static bool IsAtLeast(this int[] version, int major, int minor = 0, int patch = 0)
        {
            if (version[0] != major)
                return version[0] > major;
            var v1 = version.Length > 1 ? version[1] : 0;
            if (v1 != minor)
                return v1 > minor;
            var v2 = version.Length > 2 ? version[2] : 0;
            return v2 >= patch;
        }
    }
}
