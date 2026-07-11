using System;
using System.IO;

namespace LingCodeFTP
{
    // Shared app-data locations. Mirrors where the Mac app kept things in
    // NSUserDefaults / NSTemporaryDirectory, but using %APPDATA%\LingCodeFTP.
    static class AppPaths
    {
        public static string DataDir()
        {
            string root = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string dir = Path.Combine(root, "LingCodeFTP");
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            return dir;
        }

        public static string AccountsFile() { return Path.Combine(DataDir(), "accounts.json"); }
        public static string SettingsFile() { return Path.Combine(DataDir(), "settings.json"); }

        // Directory next to the running .exe (where ftp_mcp_server.py is shipped).
        public static string AppDir()
        {
            return AppDomain.CurrentDomain.BaseDirectory;
        }

        public static string McpServerScript()
        {
            return Path.Combine(AppDir(), "ftp_mcp_server.py");
        }

        // A per-host temp workspace for Claude's CLI cwd + .mcp.json.
        public static string ClaudeWorkspace(string host)
        {
            string safe = "";
            foreach (char c in (host ?? ""))
                safe += (char.IsLetterOrDigit(c)) ? c : '_';
            string dir = Path.Combine(Path.GetTempPath(), "ftp_claude_" + safe);
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            return dir;
        }
    }
}
