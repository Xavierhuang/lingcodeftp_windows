using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace LingCodeFtp
{
    static class Diag
    {
        public static void LogCrash(string where, Exception ex)
        {
            try
            {
                File.AppendAllText(Path.Combine(AppPaths.DataDir(), "crash.log"),
                    "==== " + where + " " + DateTime.Now + " ====\n"
                    + (ex != null ? ex.ToString() : "(none)") + "\n\n");
            }
            catch { }
        }
    }

    public class ProcResult
    {
        public byte[] StdOut;
        public string StdErr;
        public int ExitCode;
        public bool TimedOut;
    }

    // Subprocess runner + Windows-style argv quoting. On Linux the quoting is
    // harmless (arguments are passed via an argv array by the runtime), and the
    // same curl/sftp/claude/python tools are invoked.
    static class Proc
    {
        public static string Quote(string arg)
        {
            if (arg == null) arg = "";
            if (arg.Length > 0 && arg.IndexOfAny(new char[] { ' ', '\t', '\n', '\v', '"' }) < 0)
                return arg;
            StringBuilder sb = new StringBuilder();
            sb.Append('"');
            for (int i = 0; ; i++)
            {
                int bs = 0;
                while (i < arg.Length && arg[i] == '\\') { bs++; i++; }
                if (i == arg.Length) { sb.Append('\\', bs * 2); break; }
                if (arg[i] == '"') { sb.Append('\\', bs * 2 + 1); sb.Append('"'); }
                else { sb.Append('\\', bs); sb.Append(arg[i]); }
            }
            sb.Append('"');
            return sb.ToString();
        }

        public static ProcResult Run(string exe, IList<string> args, byte[] stdin, int timeoutSec)
        {
            ProcResult res = new ProcResult();
            Process p = new Process();
            p.StartInfo.FileName = exe;
            foreach (string a in args) p.StartInfo.ArgumentList.Add(a);
            p.StartInfo.UseShellExecute = false;
            p.StartInfo.CreateNoWindow = true;
            p.StartInfo.RedirectStandardOutput = true;
            p.StartInfo.RedirectStandardError = true;
            p.StartInfo.RedirectStandardInput = true;

            MemoryStream outBuf = new MemoryStream();
            StringBuilder errBuf = new StringBuilder();

            try { p.Start(); }
            catch (Exception ex) { res.StdErr = ex.Message; res.ExitCode = -1; res.StdOut = new byte[0]; return res; }

            Thread outT = new Thread(delegate()
            {
                try
                {
                    Stream s = p.StandardOutput.BaseStream;
                    byte[] chunk = new byte[65536]; int n;
                    while ((n = s.Read(chunk, 0, chunk.Length)) > 0) outBuf.Write(chunk, 0, n);
                }
                catch { }
            });
            outT.IsBackground = true; outT.Start();

            Thread errT = new Thread(delegate()
            {
                try { string s = p.StandardError.ReadToEnd(); if (s != null) errBuf.Append(s); }
                catch { }
            });
            errT.IsBackground = true; errT.Start();

            try
            {
                if (stdin != null && stdin.Length > 0) p.StandardInput.BaseStream.Write(stdin, 0, stdin.Length);
                p.StandardInput.Close();
            }
            catch { }

            if (!p.WaitForExit(timeoutSec * 1000)) { res.TimedOut = true; try { p.Kill(); } catch { } }
            try { p.WaitForExit(2000); } catch { }
            outT.Join(2000); errT.Join(2000);

            try { res.ExitCode = p.ExitCode; } catch { res.ExitCode = -1; }
            res.StdOut = outBuf.ToArray();
            res.StdErr = errBuf.ToString();
            try { p.Dispose(); } catch { }
            return res;
        }
    }
}
