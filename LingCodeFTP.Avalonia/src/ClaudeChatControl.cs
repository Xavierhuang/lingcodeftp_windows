using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace LingCodeFtp
{
    // Port of ClaudeChat: drives the `claude` CLI in print mode with
    // --output-format stream-json, parsing events live to render tool steps,
    // file-edit diffs, ask_user chips, and the final answer. Uses the user's
    // Claude subscription (no API key).
    public class ClaudeChatControl : UserControl
    {
        class Seg { public string Text; public IBrush Brush; public FontWeight Weight; public FontStyle Style; public bool Mono; }

        public string RootDir;
        public string CustomSystemPrompt;
        public string PendingContextNote;
        public event Action FilesModified;

        SelectableTextBlock _transcript;
        ScrollViewer _scroll;
        TextBox _input;
        Button _send;
        StackPanel _options;
        ComboBox _model;
        CheckBox _thinkingChk;

        static readonly FontFamily Mono = new FontFamily("Cascadia Code,Consolas,Menlo,DejaVu Sans Mono,monospace");

        readonly List<Seg> _fullLog = new List<Seg>();
        readonly List<Seg> _cleanLog = new List<Seg>();
        bool _showThinking;

        bool _busy;
        string _sessionID;
        Process _currentProc;
        int _turnSeq;
        bool _turnRenderedQuestion;
        bool _turnGotResult;
        string _lastThinkingBlock;
        string _lastAssistantText;
        HashSet<string> _turnSteps = new HashSet<string>();
        readonly StringBuilder _errBuf = new StringBuilder();
        bool _askPending;
        bool _showingInitialGreeting;

        DispatcherTimer _thinkTimer;
        Run _thinkRun;
        int _thinkTick;

        public ClaudeChatControl()
        {
            BuildView();
            AppendRole("Claude", "Open a folder, then ask me to read or change files in it. "
                + "I run on your Claude subscription via the claude CLI.");
            _showingInitialGreeting = true;
        }

        void BuildView()
        {
            Background = Brushes.White;
            DockPanel root = new DockPanel();

            // header
            StackPanel header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10,
                Height = 30, Margin = new Thickness(4, 2, 4, 2), Background = new SolidColorBrush(Color.FromRgb(245, 245, 245)) };
            DockPanel.SetDock(header, Dock.Top);
            _model = new ComboBox { Width = 96 };
            _model.ItemsSource = new[] { "default", "opus", "sonnet", "haiku" };
            _model.SelectedItem = AppSettings.GetString("ClaudeModel", "sonnet");
            if (_model.SelectedIndex < 0) _model.SelectedItem = "sonnet";
            _model.SelectionChanged += delegate { AppSettings.SetString("ClaudeModel", (string)_model.SelectedItem); };
            _thinkingChk = new CheckBox { Content = "Show thinking", VerticalAlignment = VerticalAlignment.Center };
            _thinkingChk.IsCheckedChanged += delegate { if ((_thinkingChk.IsChecked ?? false) != _showThinking) ToggleThinking(); };
            header.Children.Add(_model); header.Children.Add(_thinkingChk);
            root.Children.Add(header);

            // input row (bottom)
            Grid inputRow = new Grid { Height = 84, Margin = new Thickness(6) };
            inputRow.ColumnDefinitions = new ColumnDefinitions("*,Auto");
            _input = new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Watermark = "Ask Claude…" };
            _input.AddHandler(KeyDownEvent, OnInputKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
            Grid.SetColumn(_input, 0);
            _send = new Button { Content = "Send", Width = 64, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Stretch };
            _send.Click += delegate { OnSend(); };
            Grid.SetColumn(_send, 1);
            inputRow.Children.Add(_input); inputRow.Children.Add(_send);
            DockPanel.SetDock(inputRow, Dock.Bottom);
            root.Children.Add(inputRow);

            // options (chips) above input
            _options = new StackPanel { Margin = new Thickness(6, 0, 6, 0), Spacing = 6, IsVisible = false };
            DockPanel.SetDock(_options, Dock.Bottom);
            root.Children.Add(_options);

            // transcript (fill)
            _transcript = new SelectableTextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8),
                Foreground = Brushes.Black };
            _scroll = new ScrollViewer { Content = _transcript };
            root.Children.Add(_scroll);

            Content = root;
        }

        public void SetRootDir(string dir)
        {
            bool hadNone = string.IsNullOrEmpty(RootDir);
            RootDir = dir;
            if (!string.IsNullOrEmpty(dir) && hadNone && _showingInitialGreeting)
            {
                _fullLog.Clear(); _cleanLog.Clear();
                AppendRole("Claude", "Ask me to read or change files in the current folder. "
                    + "I run on your Claude subscription via the claude CLI.");
                RebuildTranscript();
                _showingInitialGreeting = false;
            }
        }

        public void PostNote(string text) { AppendNote(text); }

        // ---- logs ----
        List<Seg> ActiveLog() { return _showThinking ? _fullLog : _cleanLog; }

        void AppendSegToRtb(Seg s)
        {
            Run r = new Run(s.Text) { Foreground = s.Brush, FontWeight = s.Weight, FontStyle = s.Style };
            if (s.Mono) r.FontFamily = Mono;
            _transcript.Inlines.Add(r);
            ScrollToBottom();
        }

        void ScrollToBottom()
        {
            Dispatcher.UIThread.Post(delegate
            {
                try { _scroll.Offset = new Vector(0, _scroll.Extent.Height); } catch { }
            }, DispatcherPriority.Background);
        }

        void Commit(List<Seg> segs, bool toFull, bool toClean)
        {
            if (toFull) _fullLog.AddRange(segs);
            if (toClean) _cleanLog.AddRange(segs);
            bool onScreen = _showThinking ? toFull : toClean;
            if (onScreen)
            {
                bool restore = _thinkTimer != null;
                StopThinking();
                foreach (Seg s in segs) AppendSegToRtb(s);
                if (restore && _busy) StartThinking();
            }
            TrimIfHuge();
        }

        static Seg S(string text, IBrush brush, FontWeight w, FontStyle st, bool mono)
        {
            return new Seg { Text = text, Brush = brush, Weight = w, Style = st, Mono = mono };
        }

        void AppendRole(string role, string text)
        {
            List<Seg> segs = new List<Seg>();
            segs.Add(S(role + "\n", Brushes.Black, FontWeight.Bold, FontStyle.Normal, false));
            segs.Add(S(text + "\n\n", Brushes.Black, FontWeight.Normal, FontStyle.Normal, false));
            Commit(segs, true, true);
        }

        void AppendNote(string text)
        {
            List<Seg> segs = new List<Seg>();
            segs.Add(S(text + "\n\n", Brushes.Gray, FontWeight.Normal, FontStyle.Normal, false));
            Commit(segs, true, true);
        }

        void RebuildTranscript()
        {
            StopThinking();
            _transcript.Inlines.Clear();
            foreach (Seg s in ActiveLog()) AppendSegToRtb(s);
            if (_busy && !_showThinking) StartThinking();
        }

        void TrimIfHuge()
        {
            const int CAP = 400;
            if (_fullLog.Count > CAP) _fullLog.RemoveRange(0, _fullLog.Count - 300);
            if (_cleanLog.Count > CAP) _cleanLog.RemoveRange(0, _cleanLog.Count - 300);
        }

        public bool IsShowingThinking() { return _showThinking; }

        public void ToggleThinking()
        {
            _showThinking = !_showThinking;
            if ((_thinkingChk.IsChecked ?? false) != _showThinking) _thinkingChk.IsChecked = _showThinking;
            RebuildTranscript();
        }

        void IngestThinking(string t)
        {
            if (string.IsNullOrEmpty(t) || t == _lastThinkingBlock) return;
            _lastThinkingBlock = t;
            List<Seg> segs = new List<Seg>();
            segs.Add(S("🧠 Thinking\n", Brushes.Gray, FontWeight.Bold, FontStyle.Normal, false));
            segs.Add(S(t + "\n\n", Brushes.DimGray, FontWeight.Normal, FontStyle.Italic, false));
            Commit(segs, true, false);
        }

        void IngestAssistantText(string t)
        {
            if (string.IsNullOrEmpty(t) || t == _lastAssistantText) return;
            _lastAssistantText = t;
            string shown = TextWithoutAskUserBlock(t);
            if (string.IsNullOrEmpty(shown)) return;
            List<Seg> segs = new List<Seg>();
            segs.Add(S(shown + "\n\n", Brushes.Black, FontWeight.Normal, FontStyle.Normal, false));
            Commit(segs, true, false);
        }

        static bool IsFileEditTool(string name) { return name == "Edit" || name == "MultiEdit" || name == "Write"; }

        string DisplayPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            if (!string.IsNullOrEmpty(RootDir) && path.StartsWith(RootDir))
            {
                string rel = path.Substring(RootDir.Length).TrimStart('/', '\\');
                if (rel.Length > 0) return rel;
            }
            int slash = path.LastIndexOfAny(new char[] { '/', '\\' });
            return slash >= 0 ? path.Substring(slash + 1) : path;
        }

        void AppendDiffLines(List<Seg> block, string str, string prefix, IBrush color, ref int budget)
        {
            if (string.IsNullOrEmpty(str)) return;
            string[] lines = str.Split('\n');
            int n = lines.Length;
            if (n > 1 && lines[n - 1].Length == 0) n -= 1;
            for (int i = 0; i < n; i++)
            {
                if (budget <= 0) { block.Add(S("  ⋯\n", Brushes.Gray, FontWeight.Normal, FontStyle.Normal, true)); return; }
                budget--;
                block.Add(S(prefix + lines[i] + "\n", color, FontWeight.Normal, FontStyle.Normal, true));
            }
        }

        void IngestFileEdit(string name, JsonElement input)
        {
            string path = Str(input, "file_path") ?? Str(input, "path");
            string shown = DisplayPath(path);
            List<Seg> block = new List<Seg>();
            block.Add(S("✏️ " + name + "  " + (shown.Length > 0 ? shown : "(file)") + "\n", Brushes.Teal, FontWeight.Normal, FontStyle.Normal, true));
            int budget = 80;
            if (name == "Write") AppendDiffLines(block, Str(input, "content") ?? "", "+ ", Brushes.SeaGreen, ref budget);
            else if (name == "MultiEdit")
            {
                JsonElement edits;
                if (input.TryGetProperty("edits", out edits) && edits.ValueKind == JsonValueKind.Array)
                {
                    bool first = true;
                    foreach (JsonElement ed in edits.EnumerateArray())
                    {
                        if (!first) block.Add(S("\n", Brushes.Black, FontWeight.Normal, FontStyle.Normal, true));
                        first = false;
                        AppendDiffLines(block, Str(ed, "old_string") ?? "", "- ", Brushes.Firebrick, ref budget);
                        AppendDiffLines(block, Str(ed, "new_string") ?? "", "+ ", Brushes.SeaGreen, ref budget);
                    }
                }
            }
            else { AppendDiffLines(block, Str(input, "old_string") ?? "", "- ", Brushes.Firebrick, ref budget); AppendDiffLines(block, Str(input, "new_string") ?? "", "+ ", Brushes.SeaGreen, ref budget); }
            block.Add(S("\n", Brushes.Black, FontWeight.Normal, FontStyle.Normal, true));

            StringBuilder key = new StringBuilder("diff|");
            foreach (Seg s in block) key.Append(s.Text);
            if (_turnSteps.Contains(key.ToString())) return;
            _turnSteps.Add(key.ToString());
            Commit(block, true, true);
        }

        void IngestToolStep(string name, JsonElement input)
        {
            if (string.IsNullOrEmpty(name)) return;
            string detail = null;
            if (input.ValueKind == JsonValueKind.Object)
                foreach (string k in new[] { "command", "file_path", "path", "pattern", "url", "query", "prompt", "description" })
                {
                    string v = Str(input, k);
                    if (!string.IsNullOrEmpty(v)) { detail = v; break; }
                }
            if (detail == null) detail = "";
            int nl = detail.IndexOfAny(new[] { '\n', '\r' });
            if (nl >= 0) detail = detail.Substring(0, nl);
            if (detail.Length > 100) detail = detail.Substring(0, 99) + "…";
            string key = name + "|" + detail;
            if (_turnSteps.Contains(key)) return;
            _turnSteps.Add(key);
            string text = detail.Length > 0 ? "🔧 " + name + "  " + detail + "\n" : "🔧 " + name + "\n";
            List<Seg> segs = new List<Seg>();
            segs.Add(S(text, Brushes.Teal, FontWeight.Normal, FontStyle.Normal, true));
            Commit(segs, true, false);
        }

        // ---- thinking indicator (tracked Run) ----
        void StartThinking()
        {
            StopThinking();
            _thinkTick = 0;
            _thinkRun = new Run("Claude is thinking\n\n") { Foreground = Brushes.Gray };
            _transcript.Inlines.Add(_thinkRun);
            ScrollToBottom();
            _thinkTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            _thinkTimer.Tick += delegate
            {
                _thinkTick++;
                if (_thinkRun == null) return;
                string dots = new string('.', _thinkTick % 4);
                int seconds = (int)(_thinkTick * 0.4);
                _thinkRun.Text = "Claude is thinking" + dots + (seconds > 0 ? " (" + seconds + "s)" : "") + "\n\n";
                ScrollToBottom();
            };
            _thinkTimer.Start();
        }

        void StopThinking()
        {
            if (_thinkTimer != null) { _thinkTimer.Stop(); _thinkTimer = null; }
            if (_thinkRun != null) { try { _transcript.Inlines.Remove(_thinkRun); } catch { } _thinkRun = null; }
        }

        // ---- send ----
        void OnInputKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && !e.KeyModifiers.HasFlag(KeyModifiers.Shift))
            {
                e.Handled = true;
                OnSend();
            }
        }

        void OnSend()
        {
            string text = (_input.Text ?? "").Trim();
            if (_askPending)
            {
                bool hasChips = _options.Children.Count > 0;
                if (text.Length == 0 && hasChips) return;
                SubmitAnswer(text);
                return;
            }
            if (_busy || text.Length == 0) return;
            if (string.IsNullOrEmpty(RootDir)) { AppendNote("Open a folder first — that's where Claude will work."); return; }
            _input.Text = "";
            AppendRole("You", text);
            SetBusy(true);
            string prompt = text;
            if (!string.IsNullOrEmpty(PendingContextNote)) { prompt = "[" + PendingContextNote + "]\n\n" + text; PendingContextNote = null; }
            RunTurn(prompt);
        }

        void SetBusy(bool busy) { _busy = busy; _send.IsEnabled = !busy; _input.IsEnabled = !busy; }
        public bool IsBusy() { return _busy; }

        public void Abort()
        {
            if (!_busy) return;
            _turnSeq++;
            try { if (_currentProc != null && !_currentProc.HasExited) _currentProc.Kill(); } catch { }
            _currentProc = null;
            StopThinking();
            SetBusy(false);
            AppendNote("⏹ Stopped.");
        }

        // ---- CLI turn ----
        static string ClaudePath()
        {
            string appdata = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            List<string> candidates = new List<string>
            {
                Path.Combine(appdata, "npm", "node_modules", "@anthropic-ai", "claude-code", "bin", "claude.exe"),
                Path.Combine(home, ".claude", "local", "claude"),
                Path.Combine(home, ".local", "bin", "claude"),
                Path.Combine(home, ".npm-global", "bin", "claude"),
                Path.Combine(home, ".bun", "bin", "claude"),
                "/opt/homebrew/bin/claude",
                "/usr/local/bin/claude",
                "/usr/bin/claude",
            };
            foreach (string p in candidates) if (File.Exists(p)) return p;
            string path = Environment.GetEnvironmentVariable("PATH") ?? "";
            char sep = Path.PathSeparator;
            foreach (string dir in path.Split(sep))
            {
                if (dir.Length == 0) continue;
                try
                {
                    foreach (string exe in new[] { "claude", "claude.exe" })
                    {
                        string cand = Path.Combine(dir, exe);
                        if (File.Exists(cand)) return cand;
                    }
                }
                catch { }
            }
            return null;
        }

        void RunTurn(string prompt)
        {
            string cli = ClaudePath();
            if (cli == null)
            {
                AppendNote("Couldn't find the `claude` CLI. Install Claude Code and run `claude login`, then relaunch.");
                SetBusy(false);
                return;
            }

            string defaultPrompt =
                "You are embedded in a minimal IDE. Make focused changes to files in the working "
                + "directory and briefly explain what you did.\n\nWhen you need the user to make a real "
                + "decision, ask a multiple-choice question by replying with ONLY a fenced code block "
                + "labeled ask_user containing JSON:\n```ask_user\n{\"question\": \"Which database?\", "
                + "\"options\": [\"SQLite\", \"Postgres\"]}\n```\nThe IDE renders each option as a "
                + "clickable button. Use this only for genuine decisions.";
            string systemPrompt = CustomSystemPrompt ?? defaultPrompt;

            Process task = new Process();
            task.StartInfo.FileName = cli;
            var a = task.StartInfo.ArgumentList;
            a.Add("-p"); a.Add(prompt);
            a.Add("--output-format"); a.Add("stream-json");
            a.Add("--verbose");
            a.Add("--permission-mode"); a.Add("bypassPermissions");
            a.Add("--disallowedTools"); a.Add("AskUserQuestion");
            a.Add("--append-system-prompt"); a.Add(systemPrompt);
            string model = (string)_model.SelectedItem;
            if (!string.IsNullOrEmpty(model) && model != "default") { a.Add("--model"); a.Add(model); }
            if (!string.IsNullOrEmpty(_sessionID)) { a.Add("--resume"); a.Add(_sessionID); }

            task.StartInfo.UseShellExecute = false;
            task.StartInfo.CreateNoWindow = true;
            task.StartInfo.RedirectStandardOutput = true;
            task.StartInfo.RedirectStandardError = true;
            task.StartInfo.StandardOutputEncoding = Encoding.UTF8;
            task.StartInfo.StandardErrorEncoding = Encoding.UTF8;
            if (!string.IsNullOrEmpty(RootDir) && Directory.Exists(RootDir)) task.StartInfo.WorkingDirectory = RootDir;
            task.EnableRaisingEvents = true;

            try { if (_currentProc != null && !_currentProc.HasExited) _currentProc.Kill(); } catch { }
            _currentProc = task;
            int seq = ++_turnSeq;
            _turnRenderedQuestion = false; _turnGotResult = false;
            _lastThinkingBlock = null; _lastAssistantText = null;
            _turnSteps = new HashSet<string>(); _errBuf.Clear();

            task.OutputDataReceived += delegate(object s, DataReceivedEventArgs e)
            {
                if (e.Data == null) return;
                string line = e.Data;
                try { Dispatcher.UIThread.Post(delegate { HandleStreamLine(line, seq); }); } catch { }
            };
            task.ErrorDataReceived += delegate(object s, DataReceivedEventArgs e)
            {
                if (e.Data != null) lock (_errBuf) { _errBuf.Append(e.Data).Append('\n'); }
            };
            task.Exited += delegate
            {
                int status = 0; try { status = task.ExitCode; } catch { }
                string err; lock (_errBuf) { err = _errBuf.ToString(); }
                try { Dispatcher.UIThread.Post(delegate { FinishTurn(seq, status, err); }); } catch { }
            };

            StartThinking();
            try { task.Start(); task.BeginOutputReadLine(); task.BeginErrorReadLine(); }
            catch (Exception ex) { StopThinking(); AppendNote("Failed to launch claude: " + ex.Message); SetBusy(false); }
        }

        void HandleStreamLine(string line, int seq)
        {
            if (seq != _turnSeq) return;
            JsonDocument doc;
            try { doc = JsonDocument.Parse(line); } catch { return; }
            using (doc)
            {
                JsonElement o = doc.RootElement;
                if (o.ValueKind != JsonValueKind.Object) return;

                string sid = Str(o, "session_id");
                if (!string.IsNullOrEmpty(sid)) _sessionID = sid;

                string type = Str(o, "type");

                if (type == "assistant" && !_turnRenderedQuestion)
                {
                    JsonElement msg, content;
                    if (o.TryGetProperty("message", out msg) && msg.ValueKind == JsonValueKind.Object
                        && msg.TryGetProperty("content", out content) && content.ValueKind == JsonValueKind.Array)
                    {
                        foreach (JsonElement c in content.EnumerateArray())
                        {
                            if (c.ValueKind != JsonValueKind.Object) continue;
                            string ctype = Str(c, "type");
                            if (ctype == "thinking") { IngestThinking(Str(c, "thinking")); continue; }
                            if (ctype == "text") { IngestAssistantText(Str(c, "text")); continue; }
                            if (ctype == "tool_use")
                            {
                                string name = Str(c, "name");
                                JsonElement input;
                                bool hasInput = c.TryGetProperty("input", out input) && input.ValueKind == JsonValueKind.Object;
                                if (name == "AskUserQuestion" && hasInput)
                                {
                                    _turnRenderedQuestion = true;
                                    StopThinking();
                                    RenderAskUserToolInput(input);
                                    try { if (_currentProc != null && !_currentProc.HasExited) _currentProc.Kill(); } catch { }
                                    break;
                                }
                                if (IsFileEditTool(name) && hasInput) IngestFileEdit(name, input);
                                else IngestToolStep(name, hasInput ? input : default(JsonElement));
                                continue;
                            }
                        }
                    }
                    return;
                }

                if (type == "result")
                {
                    _turnGotResult = true;
                    if (_turnRenderedQuestion) return;
                    StopThinking();
                    if (FilesModified != null) FilesModified();

                    bool isError = o.TryGetProperty("is_error", out var ie) && ie.ValueKind == JsonValueKind.True;
                    string result = Str(o, "result") ?? "";
                    if (isError) { AppendNote("Claude error: " + (result.Length > 0 ? result : "unknown error")); SetBusy(false); return; }

                    string q; List<string> opts;
                    if (ParseAskUserFromText(result, out q, out opts)) { SetBusy(false); BeginAskUser(q, opts); return; }

                    string ans = TextWithoutAskUserBlock(result);
                    if (string.IsNullOrEmpty(ans)) ans = result.Length > 0 ? result : "(no text)";
                    bool alreadyInFull = !string.IsNullOrEmpty(_lastAssistantText) && ans.Trim() == _lastAssistantText.Trim();
                    List<Seg> segs = new List<Seg>();
                    segs.Add(S("Claude\n", Brushes.Black, FontWeight.Bold, FontStyle.Normal, false));
                    segs.Add(S(ans + "\n\n", Brushes.Black, FontWeight.Normal, FontStyle.Normal, false));
                    Commit(segs, !alreadyInFull, true);
                    SetBusy(false);
                }
            }
        }

        void FinishTurn(int seq, int status, string err)
        {
            if (seq != _turnSeq) return;
            if (_currentProc != null && _currentProc.HasExited) _currentProc = null;
            if (_turnRenderedQuestion || _turnGotResult) return;
            StopThinking();
            string msg = !string.IsNullOrEmpty(err) ? err.Trim() : "no output";
            AppendNote("claude CLI error (exit " + status + "): " + msg);
            SetBusy(false);
        }

        // ---- ask_user ----
        void RenderAskUserToolInput(JsonElement input)
        {
            JsonElement qd = input;
            JsonElement questions;
            if (input.TryGetProperty("questions", out questions) && questions.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement q0 in questions.EnumerateArray()) { if (q0.ValueKind == JsonValueKind.Object) { qd = q0; break; } }
            }
            string q = Str(qd, "question");
            if (string.IsNullOrEmpty(q)) return;
            List<string> opts = new List<string>();
            JsonElement optArr;
            if (qd.TryGetProperty("options", out optArr) && optArr.ValueKind == JsonValueKind.Array)
                foreach (JsonElement opt in optArr.EnumerateArray()) { string l = LabelFromOption(opt); if (!string.IsNullOrEmpty(l)) opts.Add(l); }
            SetBusy(false);
            BeginAskUser(q, opts);
        }

        void BeginAskUser(string question, List<string> options)
        {
            AppendRole("Claude", question);
            _askPending = true;
            _options.Children.Clear();
            if (options != null && options.Count > 0)
            {
                foreach (string opt in options)
                {
                    string captured = opt;
                    Button chip = new Button { Content = opt, HorizontalAlignment = HorizontalAlignment.Stretch,
                        HorizontalContentAlignment = HorizontalAlignment.Left };
                    chip.Click += delegate { if (_askPending) SubmitAnswer(captured); };
                    _options.Children.Add(chip);
                }
                _options.IsVisible = true;
            }
            else { _options.IsVisible = false; }
            _input.Focus();
        }

        void SubmitAnswer(string answer)
        {
            _askPending = false;
            _options.Children.Clear();
            _options.IsVisible = false;
            _input.Text = "";
            AppendNote("You answered: " + (answer.Length > 0 ? answer : "(empty)"));
            SetBusy(true);
            RunTurn(answer);
        }

        static string LabelFromOption(JsonElement o)
        {
            if (o.ValueKind == JsonValueKind.String) return o.GetString();
            if (o.ValueKind == JsonValueKind.Object)
                foreach (string k in new[] { "label", "text", "title", "value", "name", "option" })
                {
                    string v = Str(o, k);
                    if (!string.IsNullOrEmpty(v)) return v;
                }
            return null;
        }

        static string FirstJsonObject(string text)
        {
            int start = -1, depth = 0; bool inStr = false, esc = false;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (inStr) { if (esc) esc = false; else if (c == '\\') esc = true; else if (c == '"') inStr = false; continue; }
                if (c == '"') inStr = true;
                else if (c == '{') { if (depth == 0) start = i; depth++; }
                else if (c == '}') { if (depth > 0 && --depth == 0 && start >= 0) return text.Substring(start, i - start + 1); }
            }
            return null;
        }

        static string TextWithoutAskUserBlock(string text)
        {
            if (string.IsNullOrEmpty(text)) return text ?? "";
            int fence = text.IndexOf("```ask_user");
            if (fence < 0) return text;
            int bodyStart = fence + "```ask_user".Length;
            int close = text.IndexOf("```", bodyStart);
            int end = close >= 0 ? close + 3 : text.Length;
            string before = text.Substring(0, fence);
            string rest = end < text.Length ? text.Substring(end) : "";
            return (before + rest).Trim();
        }

        static bool ParseAskUserFromText(string text, out string question, out List<string> options)
        {
            question = null; options = null;
            if (string.IsNullOrEmpty(text)) return false;
            string jsonStr = null;
            int fence = text.IndexOf("```ask_user");
            if (fence >= 0)
            {
                int start = fence + "```ask_user".Length;
                int close = text.IndexOf("```", start);
                string body = close >= 0 ? text.Substring(start, close - start) : text.Substring(start);
                jsonStr = FirstJsonObject(body) ?? body;
            }
            else
            {
                string cand = FirstJsonObject(text);
                if (cand != null && (cand.Contains("\"question\"") || cand.Contains("\"questions\""))) jsonStr = cand;
            }
            if (jsonStr == null) return false;
            JsonDocument doc;
            try { doc = JsonDocument.Parse(jsonStr.Trim()); } catch { return false; }
            using (doc)
            {
                JsonElement obj = doc.RootElement;
                if (obj.ValueKind != JsonValueKind.Object) return false;
                JsonElement qd = obj, questions;
                if (obj.TryGetProperty("questions", out questions) && questions.ValueKind == JsonValueKind.Array)
                    foreach (JsonElement q0 in questions.EnumerateArray()) { if (q0.ValueKind == JsonValueKind.Object) { qd = q0; break; } }
                string q = Str(qd, "question");
                if (string.IsNullOrEmpty(q)) return false;
                List<string> opts = new List<string>();
                JsonElement optArr;
                if (qd.TryGetProperty("options", out optArr) && optArr.ValueKind == JsonValueKind.Array)
                    foreach (JsonElement o in optArr.EnumerateArray()) { string l = LabelFromOption(o); if (!string.IsNullOrEmpty(l)) opts.Add(l); }
                question = q; options = opts;
                return true;
            }
        }

        static string Str(JsonElement e, string key)
        {
            if (e.ValueKind != JsonValueKind.Object) return null;
            JsonElement v;
            if (e.TryGetProperty(key, out v) && v.ValueKind == JsonValueKind.String) return v.GetString();
            return null;
        }
    }
}
