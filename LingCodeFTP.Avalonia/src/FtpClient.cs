using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using Avalonia.Threading;

namespace LingCodeFtp
{
    // Port of FTPClient: shells out to curl (ftp/ftps) and sftp (sftp), both
    // present on Linux and Windows. Blocking *Sync methods do the work; the
    // callback wrappers run on a background thread and marshal completion onto
    // the Avalonia UI thread.
    public class FtpClient
    {
        readonly ServerAccount _account;

        public FtpClient(ServerAccount account) { _account = account; }

        bool IsSFTP { get { return (_account.Protocol ?? "ftp") == "sftp"; } }

        static string CurlExe { get { return "curl"; } }
        static string SftpExe { get { return "sftp"; } }

        static void Post(Action a)
        {
            try { Dispatcher.UIThread.Post(a); } catch { }
        }

        static void RunBg(ThreadStart work)
        {
            Thread t = new Thread(delegate()
            {
                try { work(); }
                catch (Exception ex) { Diag.LogCrash("ftp-bg", ex); }
            });
            t.IsBackground = true;
            t.Start();
        }

        string CurlUrlForPath(string path)
        {
            string b = _account.UrlBase();
            if (string.IsNullOrEmpty(path) || path == "/") return b + "/";
            if (path.StartsWith("/")) return b + path;
            return b + "/" + path;
        }

        List<string> CurlBaseArgs()
        {
            string creds = (_account.Username ?? "") + ":" + (_account.Password ?? "");
            return new List<string> { "-s", "--insecure", "--connect-timeout", "12", "-u", creds };
        }

        string UserHost()
        {
            string u = string.IsNullOrEmpty(_account.Username) ? "root" : _account.Username;
            return u + "@" + (_account.Host ?? "");
        }

        List<string> SshCommonOptions()
        {
            List<string> a = new List<string> {
                "-o", "BatchMode=yes",
                "-o", "ConnectTimeout=12",
                "-o", "StrictHostKeyChecking=accept-new",
                "-o", "NumberOfPasswordPrompts=0"
            };
            if (!string.IsNullOrEmpty(_account.IdentityFile))
            {
                a.Add("-o"); a.Add("IdentitiesOnly=yes");
                a.Add("-i"); a.Add(_account.IdentityFile);
            }
            return a;
        }

        ProcResult RunSftpBatch(string commands, int timeoutSec)
        {
            List<string> args = new List<string> { "-b", "-" };
            args.AddRange(SshCommonOptions());
            args.Add("-P"); args.Add((_account.Port > 0 ? _account.Port : 22).ToString());
            args.Add(UserHost());
            return Proc.Run(SftpExe, args, Encoding.UTF8.GetBytes(commands), timeoutSec);
        }

        static string SftpQuote(string path)
        {
            return "\"" + (path ?? "").Replace("\"", "\\\"") + "\"";
        }

        public List<RemoteFileNode> ListSync(string path, out string error)
        {
            error = null;
            if (string.IsNullOrEmpty(path)) path = "/";
            ProcResult r;
            if (IsSFTP) r = RunSftpBatch("ls -la " + SftpQuote(path) + "\n", 25);
            else
            {
                string url = CurlUrlForPath(path);
                if (!url.EndsWith("/")) url += "/";
                List<string> args = CurlBaseArgs(); args.Add(url);
                r = Proc.Run(CurlExe, args, null, 25);
            }
            string listing = Encoding.UTF8.GetString(r.StdOut ?? new byte[0]);
            if (r.ExitCode != 0) { error = FirstLine(r.StdErr) ?? ("connection failed (exit " + r.ExitCode + ")"); return null; }
            return ParseDirectoryListing(listing, path);
        }

        public void ListDirectory(string path, Action<List<RemoteFileNode>, string> cb)
        {
            RunBg(delegate() { string err; var n = ListSync(path, out err); Post(delegate() { cb(n, err); }); });
        }

        static string FirstLine(string s)
        {
            if (string.IsNullOrEmpty(s)) return null;
            foreach (string l in s.Split('\n', '\r')) { string t = l.Trim(); if (t.Length > 0) return t; }
            return s;
        }

        List<RemoteFileNode> ParseDirectoryListing(string listing, string basePath)
        {
            List<RemoteFileNode> nodes = new List<RemoteFileNode>();
            if (listing == null) return nodes;
            foreach (string rawLine in listing.Split('\n'))
            {
                string line = rawLine.Trim();
                if (line.Length < 10) continue;
                if (line.StartsWith("sftp>")) continue;
                char first = line[0];
                if (first != 'd' && first != '-' && first != 'l') continue;
                bool isDir = (first == 'd');

                List<string> fields = new List<string>();
                foreach (string p in line.Split(' ')) if (p.Length > 0) fields.Add(p);
                if (fields.Count < 9) continue;

                long size; long.TryParse(fields[4], out size);
                string date = fields[5] + " " + fields[6] + " " + fields[7];

                StringBuilder nameB = new StringBuilder();
                for (int i = 8; i < fields.Count; i++) { if (i > 8) nameB.Append(' '); nameB.Append(fields[i]); }
                string name = nameB.ToString();

                int arrow = name.IndexOf(" -> ");
                if (arrow >= 0) name = name.Substring(0, arrow);
                if (name == "." || name == ".." || name.Length == 0) continue;

                string dir = basePath.EndsWith("/") ? basePath : basePath + "/";
                string fullPath = dir + name;
                if (isDir) fullPath += "/";
                nodes.Add(new RemoteFileNode(name, fullPath, isDir, size, date));
            }
            nodes.Sort(delegate(RemoteFileNode a, RemoteFileNode b)
            {
                if (a.IsDirectory && !b.IsDirectory) return -1;
                if (!a.IsDirectory && b.IsDirectory) return 1;
                return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            });
            return nodes;
        }

        public byte[] DownloadSync(string remotePath, out string error)
        {
            error = null;
            ProcResult r; byte[] data = null;
            if (IsSFTP)
            {
                string tmp = Path.Combine(Path.GetTempPath(), "ftpdl_" + Guid.NewGuid().ToString("N"));
                r = RunSftpBatch("get " + SftpQuote(remotePath) + " " + SftpQuote(tmp) + "\n", 60);
                if (r.ExitCode == 0 && File.Exists(tmp)) data = File.ReadAllBytes(tmp);
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            }
            else
            {
                List<string> args = CurlBaseArgs(); args.Add(CurlUrlForPath(remotePath));
                r = Proc.Run(CurlExe, args, null, 60);
                data = r.StdOut;
            }
            if (r.ExitCode != 0 || data == null) { error = FirstLine(r.StdErr) ?? ("download failed (exit " + r.ExitCode + ")"); return null; }
            return data;
        }

        public void DownloadFile(string remotePath, Action<byte[], string> cb)
        {
            RunBg(delegate() { string err; var d = DownloadSync(remotePath, out err); Post(delegate() { cb(d, err); }); });
        }

        public string UploadSync(byte[] data, string remotePath)
        {
            string tmp = Path.Combine(Path.GetTempPath(), "ftpup_" + Guid.NewGuid().ToString("N"));
            try { File.WriteAllBytes(tmp, data ?? new byte[0]); } catch (Exception ex) { return ex.Message; }
            ProcResult r;
            if (IsSFTP) r = RunSftpBatch("put " + SftpQuote(tmp) + " " + SftpQuote(remotePath) + "\n", 60);
            else
            {
                List<string> args = CurlBaseArgs(); args.Add("-T"); args.Add(tmp); args.Add(CurlUrlForPath(remotePath));
                r = Proc.Run(CurlExe, args, null, 60);
            }
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            return r.ExitCode != 0 ? (FirstLine(r.StdErr) ?? ("upload failed (exit " + r.ExitCode + ")")) : null;
        }

        public void UploadData(byte[] data, string remotePath, Action<string> cb)
        {
            RunBg(delegate() { string err = UploadSync(data, remotePath); Post(delegate() { cb(err); }); });
        }

        public void DeleteFile(string remotePath, Action<string> cb)
        {
            RunBg(delegate()
            {
                ProcResult r;
                if (IsSFTP)
                {
                    string verb = remotePath.EndsWith("/") ? "rmdir" : "rm";
                    r = RunSftpBatch(verb + " " + SftpQuote(remotePath) + "\n", 25);
                }
                else
                {
                    List<string> args = CurlBaseArgs(); args.Add("-Q"); args.Add("DELE " + remotePath); args.Add(CurlUrlForPath("/"));
                    r = Proc.Run(CurlExe, args, null, 25);
                }
                string err = r.ExitCode != 0 ? (FirstLine(r.StdErr) ?? ("delete failed (exit " + r.ExitCode + ")")) : null;
                Post(delegate() { cb(err); });
            });
        }

        public string CreateDirectorySync(string remotePath)
        {
            ProcResult r;
            if (IsSFTP) r = RunSftpBatch("mkdir " + SftpQuote(remotePath) + "\n", 25);
            else
            {
                List<string> args = CurlBaseArgs(); args.Add("-Q"); args.Add("MKD " + remotePath); args.Add(CurlUrlForPath("/"));
                r = Proc.Run(CurlExe, args, null, 25);
            }
            return r.ExitCode != 0 ? (FirstLine(r.StdErr) ?? ("mkdir failed (exit " + r.ExitCode + ")")) : null;
        }

        public void CreateDirectory(string remotePath, Action<string> cb)
        {
            RunBg(delegate() { string err = CreateDirectorySync(remotePath); Post(delegate() { cb(err); }); });
        }

        public void RenameFile(string remotePath, string newPath, Action<string> cb)
        {
            RunBg(delegate()
            {
                ProcResult r;
                if (IsSFTP) r = RunSftpBatch("rename " + SftpQuote(remotePath) + " " + SftpQuote(newPath) + "\n", 25);
                else
                {
                    List<string> args = CurlBaseArgs();
                    args.Add("-Q"); args.Add("RNFR " + remotePath);
                    args.Add("-Q"); args.Add("RNTO " + newPath);
                    args.Add(CurlUrlForPath("/"));
                    r = Proc.Run(CurlExe, args, null, 25);
                }
                string err = r.ExitCode != 0 ? (FirstLine(r.StdErr) ?? ("rename failed (exit " + r.ExitCode + ")")) : null;
                Post(delegate() { cb(err); });
            });
        }
    }
}
