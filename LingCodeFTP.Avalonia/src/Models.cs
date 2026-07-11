using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace LingCodeFtp
{
    // Saved server (persisted as JSON in <config>/LingCodeFTP/accounts.json).
    public class ServerAccount
    {
        public string AccountID { get; set; }
        public string Name { get; set; }
        public string Host { get; set; }
        public int Port { get; set; }
        public string Username { get; set; }
        public string Password { get; set; }
        public string RemotePath { get; set; }
        public string Protocol { get; set; }      // "ftp" | "ftps" | "sftp"
        public string IdentityFile { get; set; }

        public ServerAccount()
        {
            AccountID = Guid.NewGuid().ToString();
            Name = "New Server";
            Host = "";
            Port = 21;
            Username = "anonymous";
            Password = "";
            RemotePath = "/";
            Protocol = "ftp";
            IdentityFile = "";
        }

        public string UrlBase()
        {
            string scheme = string.IsNullOrEmpty(Protocol) ? "ftp" : Protocol;
            int port = Port > 0 ? Port : 21;
            return scheme + "://" + Host + ":" + port;
        }

        static readonly JsonSerializerOptions Opts = new JsonSerializerOptions { WriteIndented = true };

        public static List<ServerAccount> LoadAll()
        {
            try
            {
                string path = AppPaths.AccountsFile();
                if (!File.Exists(path)) return new List<ServerAccount>();
                List<ServerAccount> list = JsonSerializer.Deserialize<List<ServerAccount>>(File.ReadAllText(path));
                return list ?? new List<ServerAccount>();
            }
            catch { return new List<ServerAccount>(); }
        }

        public static void SaveAll(List<ServerAccount> accounts)
        {
            try { File.WriteAllText(AppPaths.AccountsFile(), JsonSerializer.Serialize(accounts, Opts)); }
            catch { }
        }
    }

    // A node in the remote file tree.
    public class RemoteFileNode
    {
        public string Name;
        public string FullPath;
        public bool IsDirectory;
        public long FileSize;
        public string DateString;
        public List<RemoteFileNode> Children;
        public bool ChildrenLoaded;
        public bool IsLoading;

        public RemoteFileNode(string name, string path, bool isDir, long size, string date)
        {
            Name = name ?? "";
            FullPath = path ?? "/";
            IsDirectory = isDir;
            FileSize = size;
            DateString = date ?? "";
            Children = isDir ? new List<RemoteFileNode>() : null;
            ChildrenLoaded = false;
            IsLoading = false;
        }
    }

    // Tiny key/value settings store (Claude model alias, etc.).
    static class AppSettings
    {
        static Dictionary<string, string> _cache;

        static Dictionary<string, string> Load()
        {
            if (_cache != null) return _cache;
            try
            {
                string p = AppPaths.SettingsFile();
                _cache = File.Exists(p)
                    ? (JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(p))
                       ?? new Dictionary<string, string>())
                    : new Dictionary<string, string>();
            }
            catch { _cache = new Dictionary<string, string>(); }
            return _cache;
        }

        public static string GetString(string key, string def)
        {
            string v;
            return Load().TryGetValue(key, out v) && v != null ? v : def;
        }

        public static void SetString(string key, string val)
        {
            Load()[key] = val;
            try { File.WriteAllText(AppPaths.SettingsFile(), JsonSerializer.Serialize(_cache)); }
            catch { }
        }
    }
}
