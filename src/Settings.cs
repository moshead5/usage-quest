using System;
using System.Collections.Generic;
using System.IO;

namespace UsageQuest
{
    // %APPDATA%\UsageQuest\settings.json
    static class Settings
    {
        static Dictionary<string, object> data;

        public static string Dir
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "UsageQuest"); }
        }
        static string FilePath { get { return Path.Combine(Dir, "settings.json"); } }

        static Dictionary<string, object> D
        {
            get
            {
                if (data == null)
                {
                    try { data = Json.ParseObject(File.ReadAllText(FilePath)); } catch { }
                    if (data == null) data = new Dictionary<string, object>();
                }
                return data;
            }
        }

        static void Save()
        {
            try
            {
                Directory.CreateDirectory(Dir);
                string tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, Json.Serialize(D));
                if (File.Exists(FilePath)) File.Delete(FilePath);
                File.Move(tmp, FilePath);
            }
            catch { }
        }

        public static bool GetBool(string k, bool def)
        {
            bool? b = Json.Bool(D, k);
            return b.HasValue ? b.Value : def;
        }
        public static void SetBool(string k, bool v) { D[k] = v; Save(); }

        public static double? GetNum(string k) { return Json.Num(D, k); }
        public static void SetNum(string k, double? v)
        {
            if (v.HasValue) D[k] = v.Value; else D.Remove(k);
            Save();
        }

        public static string GetStr(string k) { return Json.Str(D, k); }
        public static void SetStr(string k, string v)
        {
            if (v != null) D[k] = v; else D.Remove(k);
            Save();
        }
    }
}
