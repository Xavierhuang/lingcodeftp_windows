using System;
using System.IO;

namespace LingCodeFtp
{
    // Shared app-data locations: %APPDATA%\LingCodeFTP on Windows,
    // ~/.config/LingCodeFTP (XDG) on Linux via SpecialFolder.ApplicationData.
    static class AppPaths
    {
        public static string DataDir()
        {
            string root = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (string.IsNullOrEmpty(root))
                root = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string dir = Path.Combine(root, "LingCodeFTP");
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            return dir;
        }

        public static string AccountsFile() { return Path.Combine(DataDir(), "accounts.json"); }
        public static string SettingsFile() { return Path.Combine(DataDir(), "settings.json"); }

        public static string AppDir() { return AppContext.BaseDirectory; }

        public static string McpServerScript() { return Path.Combine(AppDir(), "ftp_mcp_server.py"); }

        public static string ClaudeWorkspace(string host)
        {
            string safe = "";
            foreach (char c in (host ?? ""))
                safe += char.IsLetterOrDigit(c) ? c : '_';
            string dir = Path.Combine(Path.GetTempPath(), "ftp_claude_" + safe);
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            return dir;
        }
    }
}
