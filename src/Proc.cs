using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace LingCodeFTP
{
    // Result of running an external tool.
    public class ProcResult
    {
        public byte[] StdOut;
        public string StdErr;
        public int ExitCode;
        public bool TimedOut;
    }

    // Subprocess runner + Windows argv quoting. Replaces the Obj-C NSTask
    // runner: launches curl.exe / sftp.exe / claude.exe with a watchdog timeout
    // so the UI never hangs forever, draining stdout (bytes) and stderr (text)
    // on separate threads to avoid pipe deadlock.
    static class Proc
    {
        // Quote one argument per the CommandLineToArgvW rules so a real .exe
        // (UseShellExecute=false, no cmd.exe) receives it intact — including
        // spaces, embedded quotes, and newlines.
        public static string Quote(string arg)
        {
            if (arg == null) arg = "";
            if (arg.Length > 0 && arg.IndexOfAny(new char[] { ' ', '\t', '\n', '\v', '"' }) < 0)
                return arg;

            StringBuilder sb = new StringBuilder();
            sb.Append('"');
            for (int i = 0; ; i++)
            {
                int backslashes = 0;
                while (i < arg.Length && arg[i] == '\\') { backslashes++; i++; }
                if (i == arg.Length)
                {
                    sb.Append('\\', backslashes * 2);
                    break;
                }
                if (arg[i] == '"')
                {
                    sb.Append('\\', backslashes * 2 + 1);
                    sb.Append('"');
                }
                else
                {
                    sb.Append('\\', backslashes);
                    sb.Append(arg[i]);
                }
            }
            sb.Append('"');
            return sb.ToString();
        }

        public static string BuildArgs(IList<string> args)
        {
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < args.Count; i++)
            {
                if (i > 0) sb.Append(' ');
                sb.Append(Quote(args[i]));
            }
            return sb.ToString();
        }

        public static ProcResult Run(string exe, IList<string> args, byte[] stdin,
                                     int timeoutSec, string workingDir,
                                     IDictionary<string, string> extraEnv)
        {
            ProcResult res = new ProcResult();
            Process p = new Process();
            p.StartInfo.FileName = exe;
            p.StartInfo.Arguments = BuildArgs(args);
            p.StartInfo.UseShellExecute = false;
            p.StartInfo.CreateNoWindow = true;
            p.StartInfo.RedirectStandardOutput = true;
            p.StartInfo.RedirectStandardError = true;
            p.StartInfo.RedirectStandardInput = true;
            if (!string.IsNullOrEmpty(workingDir) && Directory.Exists(workingDir))
                p.StartInfo.WorkingDirectory = workingDir;
            if (extraEnv != null)
                foreach (KeyValuePair<string, string> kv in extraEnv)
                    p.StartInfo.EnvironmentVariables[kv.Key] = kv.Value;

            MemoryStream outBuf = new MemoryStream();
            StringBuilder errBuf = new StringBuilder();

            try { p.Start(); }
            catch (Exception ex)
            {
                res.StdErr = ex.Message;
                res.ExitCode = -1;
                res.StdOut = new byte[0];
                return res;
            }

            // Drain stdout (binary) on its own thread.
            Thread outT = new Thread(delegate()
            {
                try
                {
                    Stream s = p.StandardOutput.BaseStream;
                    byte[] chunk = new byte[65536];
                    int n;
                    while ((n = s.Read(chunk, 0, chunk.Length)) > 0)
                        outBuf.Write(chunk, 0, n);
                }
                catch { }
            });
            outT.IsBackground = true;
            outT.Start();

            // Drain stderr (text) on its own thread.
            Thread errT = new Thread(delegate()
            {
                try
                {
                    string s = p.StandardError.ReadToEnd();
                    if (s != null) errBuf.Append(s);
                }
                catch { }
            });
            errT.IsBackground = true;
            errT.Start();

            // Feed stdin then close.
            try
            {
                if (stdin != null && stdin.Length > 0)
                    p.StandardInput.BaseStream.Write(stdin, 0, stdin.Length);
                p.StandardInput.Close();
            }
            catch { }

            if (!p.WaitForExit(timeoutSec * 1000))
            {
                res.TimedOut = true;
                try { p.Kill(); } catch { }
            }
            try { p.WaitForExit(2000); } catch { }
            outT.Join(2000);
            errT.Join(2000);

            try { res.ExitCode = p.ExitCode; } catch { res.ExitCode = -1; }
            res.StdOut = outBuf.ToArray();
            res.StdErr = errBuf.ToString();
            try { p.Close(); } catch { }
            return res;
        }
    }
}
