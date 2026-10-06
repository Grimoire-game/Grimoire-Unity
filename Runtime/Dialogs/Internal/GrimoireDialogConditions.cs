using System;
using System.Collections;
using System.Globalization;

namespace Grimoire.PluginV2.Internal
{
    /// <summary>
    /// Loose value comparison matching the Grimoire dialog editor and the
    /// generated export DialogRuntime: ==, !=, &gt;, &lt;, &gt;=, &lt;=, contains,
    /// startsWith, endsWith, includes, not_includes, in_range.
    /// </summary>
    public static class GrimoireDialogConditions
    {
        public static bool Compare(object left, object right, string op)
        {
            switch (string.IsNullOrEmpty(op) ? "==" : op)
            {
                case "==": return LooseEqual(left, right);
                case "!=": return !LooseEqual(left, right);
                case ">": return ToNumber(left) > ToNumber(right);
                case "<": return ToNumber(left) < ToNumber(right);
                case ">=": return ToNumber(left) >= ToNumber(right);
                case "<=": return ToNumber(left) <= ToNumber(right);
                case "contains": return AsString(left).IndexOf(AsString(right), StringComparison.OrdinalIgnoreCase) >= 0;
                case "startsWith": return AsString(left).StartsWith(AsString(right), StringComparison.OrdinalIgnoreCase);
                case "endsWith": return AsString(left).EndsWith(AsString(right), StringComparison.OrdinalIgnoreCase);
                case "includes": return CollectionContains(left, right);
                case "not_includes": return !CollectionContains(left, right);
                case "in_range": return InRange(left, right);
                default: return LooseEqual(left, right);
            }
        }

        public static object ComputeNext(object current, object incoming, string op)
        {
            switch (string.IsNullOrEmpty(op) ? "set" : op)
            {
                case "increment": return ToNumber(current) + ToNumber(incoming);
                case "decrement": return ToNumber(current) - ToNumber(incoming);
                default: return incoming;
            }
        }

        public static bool LooseEqual(object a, object b)
        {
            if (ReferenceEquals(a, b))
            {
                return true;
            }

            if (a == null || b == null)
            {
                return false;
            }

            if (a is bool ab && b is bool bb)
            {
                return ab == bb;
            }

            if (a is bool || b is bool)
            {
                return ToBool(a) == ToBool(b);
            }

            if (IsNumberLike(a) && IsNumberLike(b))
            {
                return Math.Abs(ToNumber(a) - ToNumber(b)) < 1e-9;
            }

            return string.Equals(AsString(a), AsString(b), StringComparison.Ordinal);
        }

        public static bool IsNumberLike(object value)
        {
            return value is double || value is float || value is int || value is long ||
                   value is short || value is byte ||
                   (value is string s && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out _));
        }

        public static double ToNumber(object value)
        {
            switch (value)
            {
                case null: return 0d;
                case double d: return d;
                case float f: return f;
                case int i: return i;
                case long l: return l;
                case bool b: return b ? 1d : 0d;
            }

            return double.TryParse(AsString(value), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : 0d;
        }

        public static bool ToBool(object value)
        {
            switch (value)
            {
                case null: return false;
                case bool b: return b;
                case string s:
                    return string.Equals(s, "true", StringComparison.OrdinalIgnoreCase) || s == "1";
            }

            return IsNumberLike(value) && Math.Abs(ToNumber(value)) > double.Epsilon;
        }

        public static string AsString(object value)
        {
            switch (value)
            {
                case null: return "";
                case bool b: return b ? "true" : "false";
                case IFormattable formattable: return formattable.ToString(null, CultureInfo.InvariantCulture);
                default: return value.ToString();
            }
        }

        private static bool CollectionContains(object collection, object item)
        {
            if (collection == null || item == null)
            {
                return false;
            }

            if (collection is IEnumerable enumerable && !(collection is string))
            {
                var needle = AsString(item);
                foreach (var entry in enumerable)
                {
                    if (AsString(entry) == needle)
                    {
                        return true;
                    }
                }

                return false;
            }

            return AsString(collection).IndexOf(AsString(item), StringComparison.Ordinal) >= 0;
        }

        // Range-variable element ids need the Grimoire types store, which is not
        // exported here; only the "min..max" literal form is evaluated.
        private static bool InRange(object subject, object range)
        {
            var text = AsString(range);
            var separator = text.IndexOf("..", StringComparison.Ordinal);
            if (separator > 0 &&
                double.TryParse(text.Substring(0, separator), NumberStyles.Float, CultureInfo.InvariantCulture, out var min) &&
                double.TryParse(text.Substring(separator + 2), NumberStyles.Float, CultureInfo.InvariantCulture, out var max))
            {
                var n = ToNumber(subject);
                return n >= min && n <= max;
            }

            return LooseEqual(subject, range);
        }
    }
}
