using System;
using System.Globalization;

namespace ModuleManager
{
    public enum BumpKind { Patch, Minor, Major }

    /// <summary>
    /// Version dạng MAJOR.MINOR.PATCH, có thể kèm -pre (vd 1.2.0-beta).
    /// Bản -pre nhỏ hơn bản chính cùng số.
    /// </summary>
    public readonly struct SemVer : IComparable<SemVer>
    {
        public readonly int Major, Minor, Patch;
        public readonly string Pre;

        public SemVer(int major, int minor, int patch, string pre = "")
        {
            Major = major;
            Minor = minor;
            Patch = patch;
            Pre = pre ?? "";
        }

        public static bool TryParse(string s, out SemVer v)
        {
            v = default;
            if (string.IsNullOrWhiteSpace(s)) return false;
            s = s.Trim();
            if (s[0] == 'v' || s[0] == 'V') s = s.Substring(1);

            var pre = "";
            var dash = s.IndexOf('-');
            if (dash >= 0)
            {
                pre = s.Substring(dash + 1);
                s = s.Substring(0, dash);
                if (pre.Length == 0) return false;
            }

            var parts = s.Split('.');
            if (parts.Length != 3) return false;
            if (!Num(parts[0], out var a) || !Num(parts[1], out var b) || !Num(parts[2], out var c)) return false;
            v = new SemVer(a, b, c, pre);
            return true;
        }

        public static SemVer Parse(string s) =>
            TryParse(s, out var v) ? v : throw new FormatException($"Version không hợp lệ: '{s}' (cần dạng 1.2.3)");

        /// <summary>So sánh hai chuỗi version. Ném FormatException nếu chuỗi sai dạng.</summary>
        public static int Compare(string a, string b) => Parse(a).CompareTo(Parse(b));

        public int CompareTo(SemVer o)
        {
            if (Major != o.Major) return Major.CompareTo(o.Major);
            if (Minor != o.Minor) return Minor.CompareTo(o.Minor);
            if (Patch != o.Patch) return Patch.CompareTo(o.Patch);
            if (Pre.Length == 0 || o.Pre.Length == 0) return o.Pre.Length.CompareTo(Pre.Length);
            return string.CompareOrdinal(Pre, o.Pre);
        }

        public SemVer Bump(BumpKind kind)
        {
            switch (kind)
            {
                case BumpKind.Major: return new SemVer(Major + 1, 0, 0);
                case BumpKind.Minor: return new SemVer(Major, Minor + 1, 0);
                default: return Pre.Length > 0 ? new SemVer(Major, Minor, Patch) : new SemVer(Major, Minor, Patch + 1);
            }
        }

        public override string ToString() => Pre.Length == 0 ? $"{Major}.{Minor}.{Patch}" : $"{Major}.{Minor}.{Patch}-{Pre}";

        private static bool Num(string s, out int n) =>
            int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out n);
    }
}
