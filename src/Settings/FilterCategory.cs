using System.Collections.Generic;
using System.Linq;

namespace trutility.Settings
{
    /// <summary>
    /// A fixed group of file types that can be hidden with one checkbox. Only
    /// types that are opened rather than run belong here: scripts, .reg, .msi
    /// and .url launch something, which is what the menu is for.
    /// </summary>
    public sealed class FilterCategory
    {
        public static readonly IReadOnlyList<FilterCategory> All = new[]
        {
            new FilterCategory("Images", ToggleSetting.HideImages,
                "png, jpg, jpeg, gif, bmp, ico, svg, webp, tif, tiff"),
            new FilterCategory("Web pages", ToggleSetting.HideWebPages,
                "html, htm, mht, css"),
            new FilterCategory("Config files", ToggleSetting.HideConfigFiles,
                "ini, cfg, conf, config, xml, json, yaml, yml, toml, inf"),
            new FilterCategory("Program files", ToggleSetting.HideProgramFiles,
                "dll, sys, ocx, pdb, manifest, dat, bin, mui"),
            new FilterCategory("Logs and misc", ToggleSetting.HideLogsAndMisc,
                "log, tmp, temp, bak, old, dmp, etl"),
            new FilterCategory("Media", ToggleSetting.HideMedia,
                "mp3, wav, mp4, avi, mkv, mov"),
            new FilterCategory("Fonts", ToggleSetting.HideFonts,
                "ttf, otf, fon"),
        };

        public string Name { get; }
        public ToggleSetting Setting { get; }

        /// In the same form as the custom list, so both go through one parser.
        public string Extensions { get; }

        private FilterCategory(string name, ToggleSetting setting, string extensions)
        {
            Name = name;
            Setting = setting;
            Extensions = extensions;
        }

        /// The extensions of every category that is switched on, as one list.
        public static string HiddenExtensions()
        {
            return string.Join(", ", All.Where(c => c.Setting.IsOn()).Select(c => c.Extensions));
        }
    }
}
