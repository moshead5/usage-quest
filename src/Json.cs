using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Web.Script.Serialization;

namespace UsageQuest
{
    // JavaScriptSerializer(.NET Framework 기본 포함)를 감싼 작은 도우미
    static class Json
    {
        static readonly JavaScriptSerializer S = new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024 };

        public static Dictionary<string, object> ParseObject(string text)
        {
            try { return S.DeserializeObject(text) as Dictionary<string, object>; }
            catch { return null; }
        }

        public static string Serialize(object o) { return S.Serialize(o); }

        public static Dictionary<string, object> Obj(Dictionary<string, object> d, string key)
        {
            object v;
            if (d != null && d.TryGetValue(key, out v)) return v as Dictionary<string, object>;
            return null;
        }

        public static string Str(Dictionary<string, object> d, string key)
        {
            object v;
            if (d != null && d.TryGetValue(key, out v) && v != null) return v as string ?? Convert.ToString(v, CultureInfo.InvariantCulture);
            return null;
        }

        public static double? Num(Dictionary<string, object> d, string key)
        {
            object v;
            if (d == null || !d.TryGetValue(key, out v) || v == null) return null;
            if (v is int) return (int)v;
            if (v is long) return (long)v;
            if (v is double) return (double)v;
            if (v is decimal) return (double)(decimal)v;
            if (v is float) return (float)v;
            return null;
        }

        public static bool? Bool(Dictionary<string, object> d, string key)
        {
            object v;
            if (d != null && d.TryGetValue(key, out v) && v is bool) return (bool)v;
            return null;
        }

        public static DateTime? Date(Dictionary<string, object> d, string key)
        {
            string s = Str(d, key);
            DateTime t;
            if (s != null && DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out t))
                return DateTime.SpecifyKind(t, DateTimeKind.Utc);
            return null;
        }
    }
}
