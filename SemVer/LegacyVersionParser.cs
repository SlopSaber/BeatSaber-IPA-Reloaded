using System;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Hive.Versioning;

namespace SemVer
{
    /// <summary>Converts historical loose version syntax into semantic versions.</summary>
    public static class LegacyVersionParser
    {
        private static readonly Regex LooseVersion = new(@"^[vV=]?\s*(?<major>\d+)(?:\.(?<minor>\d+))?(?:\.(?<patch>\d+))?(?<suffix>[-.]?[0-9A-Za-z][0-9A-Za-z.-]*)?(?<build>\+[0-9A-Za-z.-]+)?$", RegexOptions.CultureInvariant);
        private static readonly Regex RangeVersion = new(@"(?<![\w.])[vV]?\d+\.\d+\.\d+(?:[.-][0-9A-Za-z][0-9A-Za-z.-]*|[A-Za-z][0-9A-Za-z.-]*)?(?:\+[0-9A-Za-z.-]+)?", RegexOptions.CultureInvariant);

        /// <summary>Parses a strict version or, when requested, historical loose syntax.</summary>
        public static Hive.Versioning.Version Parse(string text, bool loose)
        {
            if (text == null) throw new ArgumentNullException(nameof(text));
            if (Hive.Versioning.Version.TryParse(text, out var strict)) return strict;
            if (!loose) return new Hive.Versioning.Version(text);
            var match = LooseVersion.Match(text.Trim());
            if (!match.Success) return new Hive.Versioning.Version(text);
            string Number(string name) => match.Groups[name].Success
                ? ulong.Parse(match.Groups[name].Value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture) : "0";
            string suffix = match.Groups["suffix"].Value.TrimStart('-', '.');
            if (suffix.Length > 0)
                suffix = "-" + string.Join(".", suffix.Split('.').Select(identifier =>
                    ulong.TryParse(identifier, NumberStyles.None, CultureInfo.InvariantCulture, out var numeric)
                        ? numeric.ToString(CultureInfo.InvariantCulture) : identifier));
            return new Hive.Versioning.Version($"{Number("major")}.{Number("minor")}.{Number("patch")}{suffix}{match.Groups["build"].Value}");
        }

        /// <summary>Parses a range while retaining wildcard and partial-version semantics.</summary>
        public static VersionRange ParseRange(string text, bool loose)
        {
            if (text == null) throw new ArgumentNullException(nameof(text));
            if (!loose) return new VersionRange(text);
            return new VersionRange(RangeVersion.Replace(text, match => Parse(match.Value, true).ToString()));
        }
    }
}
