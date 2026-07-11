using System;
using System.Collections.Generic;
using System.IO;
using System.Web.Script.Serialization;

namespace LingCodeFTP
{
    // Saved server, persisted as JSON in %APPDATA%\LingCodeFTP\accounts.json.
    // Port of the Obj-C ServerAccount (NSSecureCoding -> plain JSON here).
    public class ServerAccount
    {
        public string AccountID { get; set; }
        public string Name { get; set; }
        public string Host { get; set; }
        public int Port { get; set; }
        public string Username { get; set; }
        public string Password { get; set; }
        public string RemotePath { get; set; }   // initial remote path
        public string Protocol { get; set; }      // "ftp" | "ftps" | "sftp"
        public string IdentityFile { get; set; }  // optional SSH key path (sftp)

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

        // e.g. ftp://host:21  — used to build curl URLs.
        public string UrlBase()
        {
            string scheme = string.IsNullOrEmpty(Protocol) ? "ftp" : Protocol;
            int port = Port > 0 ? Port : 21;
            return scheme + "://" + Host + ":" + port;
        }

        public static List<ServerAccount> LoadAll()
        {
            try
            {
                string path = AppPaths.AccountsFile();
                if (!File.Exists(path)) return new List<ServerAccount>();
                JavaScriptSerializer ser = new JavaScriptSerializer();
                ser.MaxJsonLength = int.MaxValue;
                List<ServerAccount> list = ser.Deserialize<List<ServerAccount>>(File.ReadAllText(path));
                return list ?? new List<ServerAccount>();
            }
            catch { return new List<ServerAccount>(); }
        }

        public static void SaveAll(List<ServerAccount> accounts)
        {
            try
            {
                JavaScriptSerializer ser = new JavaScriptSerializer();
                ser.MaxJsonLength = int.MaxValue;
                File.WriteAllText(AppPaths.AccountsFile(), ser.Serialize(accounts));
            }
            catch { }
        }
    }
}
