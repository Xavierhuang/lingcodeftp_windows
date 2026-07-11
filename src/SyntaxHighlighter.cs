using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace LingCodeFTP
{
    // Lightweight tokenizer-based highlighter for a RichTextBox. The Mac app
    // shipped a C highlighting engine (src/syntax/*.c); on Windows we do a
    // pragmatic single-pass tokenizer covering the common families — comments,
    // strings, numbers, and per-language keywords — with the same Xcode
    // "Default (Light)" palette. Redraw is suspended and the scroll/selection
    // preserved so re-highlighting on each edit doesn't flicker or jump.
    static class SyntaxHighlighter
    {
        // Palette (Xcode Default Light), matching SyntaxHighlighter.m.
        static readonly Color CKeyword = FromHex(0xAA0D91);
        static readonly Color CComment = FromHex(0x007400);
        static readonly Color CString = FromHex(0xC41A16);
        static readonly Color CNumber = FromHex(0x1C00CF);
        static readonly Color CPreproc = FromHex(0x643820);
        static readonly Color CDefault = Color.FromArgb(20, 20, 20);

        const int MaxChars = 200000;   // skip highlighting past this (perf)

        struct Run { public int Start; public int Len; public Color Color; }

        class Lang
        {
            public string LineComment;   // e.g. "//" or "#" or null
            public bool BlockComment;    // /* ... */
            public string[] Strings;     // delimiter chars as strings
            public HashSet<string> Keywords;
            public bool PreprocHash;     // '#' directive at line start (C/C++)
        }

        static Lang ForPath(string path)
        {
            string ext = "";
            int dot = path.LastIndexOf('.');
            if (dot >= 0) ext = path.Substring(dot + 1).ToLowerInvariant();
            string name = path;
            int slash = path.LastIndexOfAny(new char[] { '/', '\\' });
            if (slash >= 0) name = path.Substring(slash + 1);
            name = name.ToLowerInvariant();

            switch (ext)
            {
                case "c": case "h": case "cpp": case "cc": case "hpp": case "cs":
                case "java": case "js": case "jsx": case "ts": case "tsx":
                case "go": case "rs": case "swift": case "m": case "mm":
                case "php": case "kt": case "scala": case "dart":
                    return CStyle(ext);
                case "py": case "rb": case "sh": case "bash": case "zsh":
                case "pl": case "yaml": case "yml": case "toml": case "ini": case "conf":
                    return Scripty();
                case "json":
                    return Json();
                case "css": case "scss": case "less":
                    return Css();
                case "html": case "htm": case "xml": case "svg":
                    return Markup();
            }
            if (name == "makefile" || name == "dockerfile") return Scripty();
            return null;
        }

        static Lang CStyle(string ext)
        {
            Lang l = new Lang();
            l.LineComment = "//";
            l.BlockComment = true;
            l.Strings = new string[] { "\"", "'", "`" };
            l.PreprocHash = (ext == "c" || ext == "h" || ext == "cpp" || ext == "cc" || ext == "hpp" || ext == "m" || ext == "mm");
            l.Keywords = Words("if else for while do switch case default break continue return "
                + "class struct enum interface public private protected static final const void "
                + "int long short char float double bool boolean byte var let function func def "
                + "new delete this super null nil true false import export from package namespace "
                + "using try catch finally throw throws async await yield typeof instanceof extends "
                + "implements virtual override abstract sizeof typedef union unsigned signed auto "
                + "string String number object type continue in of as is where guard defer");
            return l;
        }

        static Lang Scripty()
        {
            Lang l = new Lang();
            l.LineComment = "#";
            l.BlockComment = false;
            l.Strings = new string[] { "\"", "'" };
            l.Keywords = Words("if elif else for while do done then fi case esac function def return "
                + "import from as class try except finally with lambda pass break continue in is not "
                + "and or none true false global nonlocal yield async await echo export local set "
                + "unset readonly declare");
            return l;
        }

        static Lang Json()
        {
            Lang l = new Lang();
            l.LineComment = null;
            l.BlockComment = false;
            l.Strings = new string[] { "\"" };
            l.Keywords = Words("true false null");
            return l;
        }

        static Lang Css()
        {
            Lang l = new Lang();
            l.LineComment = null;
            l.BlockComment = true;
            l.Strings = new string[] { "\"", "'" };
            l.Keywords = new HashSet<string>();
            return l;
        }

        static Lang Markup()
        {
            // Very light: comments + strings only (tags left default).
            Lang l = new Lang();
            l.LineComment = null;
            l.BlockComment = false;
            l.Strings = new string[] { "\"", "'" };
            l.Keywords = new HashSet<string>();
            return l;
        }

        static HashSet<string> Words(string s)
        {
            HashSet<string> h = new HashSet<string>();
            foreach (string w in s.Split(' '))
                if (w.Length > 0) h.Add(w);
            return h;
        }

        public static void Highlight(RichTextBox rtb, string path)
        {
            if (rtb == null || string.IsNullOrEmpty(path)) return;
            Lang lang = ForPath(path);
            string text = rtb.Text;
            if (text.Length > MaxChars) lang = null;   // too big; leave plain

            List<Run> runs = new List<Run>();
            if (lang != null) runs = Tokenize(text, lang);

            // Save scroll + selection so recoloring doesn't jump the view.
            Point scroll = new Point();
            SendMessage(rtb.Handle, EM_GETSCROLLPOS, IntPtr.Zero, ref scroll);
            int selStart = rtb.SelectionStart;
            int selLen = rtb.SelectionLength;

            SendMessage(rtb.Handle, WM_SETREDRAW, (IntPtr)0, IntPtr.Zero);
            try
            {
                rtb.SelectAll();
                rtb.SelectionColor = CDefault;
                foreach (Run r in runs)
                {
                    rtb.Select(r.Start, r.Len);
                    rtb.SelectionColor = r.Color;
                }
                rtb.Select(selStart, selLen);
            }
            finally
            {
                SendMessage(rtb.Handle, WM_SETREDRAW, (IntPtr)1, IntPtr.Zero);
                SendMessage(rtb.Handle, EM_SETSCROLLPOS, IntPtr.Zero, ref scroll);
                rtb.Invalidate();
            }
        }

        static List<Run> Tokenize(string s, Lang lang)
        {
            List<Run> runs = new List<Run>();
            int n = s.Length;
            int i = 0;
            bool lineStart = true;
            while (i < n)
            {
                char c = s[i];

                // preprocessor directive (#...) at start of a line
                if (lang.PreprocHash && lineStart && c == '#')
                {
                    int j = i;
                    while (j < n && s[j] != '\n') j++;
                    runs.Add(MakeRun(i, j - i, CPreproc));
                    i = j; lineStart = false; continue;
                }

                // line comment
                if (lang.LineComment != null && Match(s, i, lang.LineComment))
                {
                    int j = i;
                    while (j < n && s[j] != '\n') j++;
                    runs.Add(MakeRun(i, j - i, CComment));
                    i = j; continue;
                }

                // block comment
                if (lang.BlockComment && Match(s, i, "/*"))
                {
                    int j = i + 2;
                    while (j < n && !Match(s, j, "*/")) j++;
                    if (j < n) j += 2;
                    runs.Add(MakeRun(i, j - i, CComment));
                    i = j; continue;
                }

                // string
                bool isString = false;
                for (int k = 0; k < lang.Strings.Length; k++)
                {
                    if (Match(s, i, lang.Strings[k]))
                    {
                        char q = lang.Strings[k][0];
                        int j = i + 1;
                        while (j < n)
                        {
                            if (s[j] == '\\') { j += 2; continue; }
                            if (s[j] == q) { j++; break; }
                            if (s[j] == '\n') break;   // don't run strings across lines
                            j++;
                        }
                        runs.Add(MakeRun(i, j - i, CString));
                        i = j; isString = true; break;
                    }
                }
                if (isString) { lineStart = false; continue; }

                // number
                if ((c >= '0' && c <= '9') && (i == 0 || !IsIdent(s[i - 1])))
                {
                    int j = i;
                    while (j < n && (IsIdent(s[j]) || s[j] == '.')) j++;
                    runs.Add(MakeRun(i, j - i, CNumber));
                    i = j; lineStart = false; continue;
                }

                // identifier / keyword
                if (IsIdentStart(c))
                {
                    int j = i;
                    while (j < n && IsIdent(s[j])) j++;
                    string word = s.Substring(i, j - i);
                    if (lang.Keywords.Contains(word))
                        runs.Add(MakeRun(i, j - i, CKeyword));
                    i = j; lineStart = false; continue;
                }

                if (c == '\n') lineStart = true;
                else if (c != ' ' && c != '\t' && c != '\r') lineStart = false;
                i++;
            }
            return runs;
        }

        static Run MakeRun(int start, int len, Color color)
        {
            Run r = new Run();
            r.Start = start; r.Len = len; r.Color = color;
            return r;
        }

        static bool Match(string s, int i, string tok)
        {
            if (i + tok.Length > s.Length) return false;
            for (int k = 0; k < tok.Length; k++)
                if (s[i + k] != tok[k]) return false;
            return true;
        }

        static bool IsIdentStart(char c) { return char.IsLetter(c) || c == '_' || c == '$'; }
        static bool IsIdent(char c) { return char.IsLetterOrDigit(c) || c == '_' || c == '$'; }

        static Color FromHex(int rgb)
        {
            return Color.FromArgb((rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);
        }

        // ---- Win32 for flicker-free recolor + scroll preservation ----
        const int WM_SETREDRAW = 0x000B;
        const int EM_GETSCROLLPOS = 0x0400 + 221;
        const int EM_SETSCROLLPOS = 0x0400 + 222;

        [DllImport("user32.dll")]
        static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")]
        static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, ref Point lParam);
    }
}
