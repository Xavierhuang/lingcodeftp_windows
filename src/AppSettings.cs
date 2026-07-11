using System.Collections.Generic;
using System.IO;
using System.Web.Script.Serialization;

namespace LingCodeFTP
{
    // Tiny key/value settings store (Claude model alias, sounds toggle).
    static class AppSettings
    {
        static Dictionary<string, object> _cache;

        static Dictionary<string, object> Load()
        {
            if (_cache != null) return _cache;
            try
            {
                string p = AppPaths.SettingsFile();
                if (File.Exists(p))
                {
                    JavaScriptSerializer ser = new JavaScriptSerializer();
                    _cache = ser.Deserialize<Dictionary<string, object>>(File.ReadAllText(p))
                             ?? new Dictionary<string, object>();
                }
                else _cache = new Dictionary<string, object>();
            }
            catch { _cache = new Dictionary<string, object>(); }
            return _cache;
        }

        public static string GetString(string key, string def)
        {
            Dictionary<string, object> d = Load();
            object v;
            if (d.TryGetValue(key, out v) && v != null) return v.ToString();
            return def;
        }

        public static void SetString(string key, string val)
        {
            Load()[key] = val;
            Save();
        }

        public static bool GetBool(string key, bool def)
        {
            string s = GetString(key, def ? "true" : "false");
            return s == "true" || s == "True" || s == "1";
        }

        public static void SetBool(string key, bool val)
        {
            SetString(key, val ? "true" : "false");
        }

        static void Save()
        {
            try
            {
                JavaScriptSerializer ser = new JavaScriptSerializer();
                File.WriteAllText(AppPaths.SettingsFile(), ser.Serialize(_cache));
            }
            catch { }
        }
    }
}
