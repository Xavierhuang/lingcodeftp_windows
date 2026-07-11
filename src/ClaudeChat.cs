using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace LingCodeFTP
{
    // Port of ClaudeChat.m (+ ChatControls). A self-contained chat panel that
    // shells out to the `claude` CLI in print mode with --output-format
    // stream-json, so it uses the user's Claude subscription (their `claude
    // login` session) rather than an API key. It parses the stream live to
    // intercept the native AskUserQuestion tool (rendered as clickable chips),
    // show tool steps + file-edit diffs, and stream the final answer. A model
    // selector pins the CLI --model alias for cost control.
    public class ClaudeChat : UserControl
    {
        class Seg { public string Text; public Color Color; public Font Font; }

        // ---- public surface (set by ServerForm) ----
        public string RootDir;               // CLI working directory (workspace)
        public string CustomSystemPrompt;    // --append-system-prompt override
        public string PendingContextNote;    // injected once into next prompt
        public event Action FilesModified;   // CLI may have changed remote files

        // ---- UI ----
        RichTextBox _transcript;
        TextBox _input;
        Button _send;
        FlowLayoutPanel _options;
        ComboBox _model;
        CheckBox _thinkingChk;

        // ---- fonts ----
        Font _fBody, _fBold, _fSmall, _fItalic, _fMono;

        // ---- logs ----
        readonly List<Seg> _fullLog = new List<Seg>();
        readonly List<Seg> _cleanLog = new List<Seg>();
        bool _showThinking;

        // ---- turn state ----
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

        // ---- thinking indicator ----
        Timer _thinkTimer;
        int _thinkStart;
        int _thinkTick;

        public ClaudeChat()
        {
            _fBody = new Font("Segoe UI", 9.5f);
            _fBold = new Font("Segoe UI", 9.5f, FontStyle.Bold);
            _fSmall = new Font("Segoe UI", 8.5f);
            _fItalic = new Font("Segoe UI", 8.5f, FontStyle.Italic);
            _fMono = new Font("Consolas", 9f);

            BuildView();

            AppendRole("Claude", "Open a folder, then ask me to read or change files in it. "
                + "I run on your Claude subscription via the claude CLI.");
            _showingInitialGreeting = true;
        }

        void BuildView()
        {
            BackColor = Color.White;

            _transcript = new RichTextBox();
            _transcript.Dock = DockStyle.Fill;
            _transcript.ReadOnly = true;
            _transcript.BorderStyle = BorderStyle.None;
            _transcript.BackColor = Color.White;
            _transcript.Font = _fBody;
            _transcript.HideSelection = false;

            _options = new FlowLayoutPanel();
            _options.Dock = DockStyle.Bottom;
            _options.FlowDirection = FlowDirection.TopDown;
            _options.WrapContents = false;
            _options.AutoScroll = true;
            _options.AutoSize = false;
            _options.Height = 0;
            _options.Visible = false;
            _options.BackColor = Color.FromArgb(247, 247, 247);

            Panel inputPanel = new Panel();
            inputPanel.Dock = DockStyle.Bottom;
            inputPanel.Height = 84;
            inputPanel.Padding = new Padding(6);

            _input = new TextBox();
            _input.Multiline = true;
            _input.Dock = DockStyle.Fill;
            _input.Font = _fBody;
            _input.ScrollBars = ScrollBars.Vertical;
            _input.KeyDown += OnInputKeyDown;

            _send = new Button();
            _send.Text = "Send";
            _send.Dock = DockStyle.Right;
            _send.Width = 64;
            _send.Click += delegate { OnSend(); };

            inputPanel.Controls.Add(_input);
            inputPanel.Controls.Add(_send);

            Panel header = new Panel();
            header.Dock = DockStyle.Top;
            header.Height = 28;
            header.BackColor = Color.FromArgb(245, 245, 245);

            _model = new ComboBox();
            _model.DropDownStyle = ComboBoxStyle.DropDownList;
            _model.Items.AddRange(new object[] { "default", "opus", "sonnet", "haiku" });
            _model.Width = 90;
            _model.Location = new Point(4, 3);
            _model.SelectedItem = AppSettings.GetString("ClaudeModel", "sonnet");
            if (_model.SelectedIndex < 0) _model.SelectedItem = "sonnet";
            _model.SelectedIndexChanged += delegate
            {
                AppSettings.SetString("ClaudeModel", (string)_model.SelectedItem);
            };
            header.Controls.Add(_model);

            _thinkingChk = new CheckBox();
            _thinkingChk.Text = "Show thinking";
            _thinkingChk.AutoSize = true;
            _thinkingChk.Location = new Point(104, 5);
            _thinkingChk.CheckedChanged += delegate
            {
                if (_thinkingChk.Checked != _showThinking) ToggleThinking();
            };
            header.Controls.Add(_thinkingChk);

            // Dock order (added first = innermost/Fill): transcript, options,
            // input, header -> top-to-bottom: header, transcript, options, input.
            Controls.Add(_transcript);
            Controls.Add(_options);
            Controls.Add(inputPanel);
            Controls.Add(header);
        }

        // ============================================================ Greeting
        public void SetRootDir(string dir)
        {
            bool hadNone = string.IsNullOrEmpty(RootDir);
            RootDir = dir;
            if (!string.IsNullOrEmpty(dir) && hadNone && _showingInitialGreeting)
            {
                _fullLog.Clear();
                _cleanLog.Clear();
                AppendRole("Claude", "Ask me to read or change files in the current folder. "
                    + "I run on your Claude subscription via the claude CLI.");
                RebuildTranscript();
                _showingInitialGreeting = false;
            }
        }

        public void PostNote(string text) { AppendNote(text); }

        // ============================================================ Logs
        List<Seg> ActiveLog() { return _showThinking ? _fullLog : _cleanLog; }

        void AppendSegToRtb(Seg s)
        {
            _transcript.SelectionStart = _transcript.TextLength;
            _transcript.SelectionLength = 0;
            _transcript.SelectionColor = s.Color;
            _transcript.SelectionFont = s.Font;
            _transcript.AppendText(s.Text);
            ScrollToBottom();
        }

        void ScrollToBottom()
        {
            _transcript.SelectionStart = _transcript.TextLength;
            _transcript.ScrollToCaret();
        }

        // Commit a group of segments to the chosen log(s), mirroring to the
        // transcript only if the active log received it. The thinking indicator
        // is pulled off the end first and restored after, so appends land
        // cleanly and the dots stay at the bottom.
        void Commit(List<Seg> segs, bool toFull, bool toClean)
        {
            if (toFull) _fullLog.AddRange(segs);
            if (toClean) _cleanLog.AddRange(segs);
            bool onScreen = _showThinking ? toFull : toClean;
            if (onScreen)
            {
                bool restore = (_thinkTimer != null);
                StopThinking();
                foreach (Seg s in segs) AppendSegToRtb(s);
                if (restore && _busy) StartThinking();
            }
            TrimIfHuge();
        }

        Seg S(string text, Color color, Font font)
        {
            Seg s = new Seg();
            s.Text = text; s.Color = color; s.Font = font;
            return s;
        }

        void AppendRole(string role, string text)
        {
            List<Seg> segs = new List<Seg>();
            segs.Add(S(role + "\n", Color.Black, _fBold));
            segs.Add(S(text + "\n\n", Color.Black, _fBody));
            Commit(segs, true, true);
        }

        void AppendNote(string text)
        {
            List<Seg> segs = new List<Seg>();
            segs.Add(S(text + "\n\n", Color.Gray, _fSmall));
            Commit(segs, true, true);
        }

        void RebuildTranscript()
        {
            StopThinking();
            _transcript.Clear();
            foreach (Seg s in ActiveLog()) AppendSegToRtb(s);
            if (_busy && !_showThinking) StartThinking();
        }

        void TrimIfHuge()
        {
            const int CAP = 400;   // segments, not bytes — simpler bound
            if (_fullLog.Count > CAP) _fullLog.RemoveRange(0, _fullLog.Count - 300);
            if (_cleanLog.Count > CAP) _cleanLog.RemoveRange(0, _cleanLog.Count - 300);
        }

        // ============================================================ Thinking toggle
        public bool IsShowingThinking() { return _showThinking; }

        public void ToggleThinking()
        {
            _showThinking = !_showThinking;
            if (_thinkingChk.Checked != _showThinking) _thinkingChk.Checked = _showThinking;
            RebuildTranscript();
        }

        void IngestThinking(string t)
        {
            if (string.IsNullOrEmpty(t)) return;
            if (t == _lastThinkingBlock) return;
            _lastThinkingBlock = t;
            List<Seg> segs = new List<Seg>();
            segs.Add(S("🧠 Thinking\n", Color.Gray, _fBold));
            segs.Add(S(t + "\n\n", Color.DimGray, _fItalic));
            Commit(segs, true, false);
        }

        void IngestAssistantText(string t)
        {
            if (string.IsNullOrEmpty(t)) return;
            if (t == _lastAssistantText) return;
            _lastAssistantText = t;
            string shown = TextWithoutAskUserBlock(t);
            if (string.IsNullOrEmpty(shown)) return;
            List<Seg> segs = new List<Seg>();
            segs.Add(S(shown + "\n\n", Color.Black, _fBody));
            Commit(segs, true, false);
        }

        static bool IsFileEditTool(string name)
        {
            return name == "Edit" || name == "MultiEdit" || name == "Write";
        }

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

        void AppendDiffLines(List<Seg> block, string str, string prefix, Color color, ref int budget)
        {
            if (string.IsNullOrEmpty(str)) return;
            string[] lines = str.Split('\n');
            int n = lines.Length;
            if (n > 1 && lines[n - 1].Length == 0) n -= 1;
            for (int i = 0; i < n; i++)
            {
                if (budget <= 0) { block.Add(S("  ⋯\n", Color.Gray, _fMono)); return; }
                budget--;
                block.Add(S(prefix + lines[i] + "\n", color, _fMono));
            }
        }

        void IngestFileEdit(string name, Dictionary<string, object> input)
        {
            string path = Str(input, "file_path") ?? Str(input, "path");
            string shown = DisplayPath(path);
            List<Seg> block = new List<Seg>();
            block.Add(S("✏️ " + name + "  " + (shown.Length > 0 ? shown : "(file)") + "\n",
                Color.Teal, _fMono));

            int budget = 80;
            if (name == "Write")
            {
                AppendDiffLines(block, Str(input, "content") ?? "", "+ ", Color.SeaGreen, ref budget);
            }
            else if (name == "MultiEdit")
            {
                object[] edits = AsArr(GetVal(input, "edits"));
                if (edits != null)
                {
                    bool first = true;
                    foreach (object e in edits)
                    {
                        Dictionary<string, object> ed = AsDict(e);
                        if (ed == null) continue;
                        if (!first) block.Add(S("\n", Color.Black, _fMono));
                        first = false;
                        AppendDiffLines(block, Str(ed, "old_string") ?? "", "- ", Color.Firebrick, ref budget);
                        AppendDiffLines(block, Str(ed, "new_string") ?? "", "+ ", Color.SeaGreen, ref budget);
                    }
                }
            }
            else // Edit
            {
                AppendDiffLines(block, Str(input, "old_string") ?? "", "- ", Color.Firebrick, ref budget);
                AppendDiffLines(block, Str(input, "new_string") ?? "", "+ ", Color.SeaGreen, ref budget);
            }
            block.Add(S("\n", Color.Black, _fMono));

            StringBuilder key = new StringBuilder("diff|");
            foreach (Seg s in block) key.Append(s.Text);
            if (_turnSteps.Contains(key.ToString())) return;
            _turnSteps.Add(key.ToString());

            Commit(block, true, true);
        }

        void IngestToolStep(string name, object input)
        {
            if (string.IsNullOrEmpty(name)) return;
            string detail = null;
            Dictionary<string, object> d = AsDict(input);
            if (d != null)
            {
                foreach (string k in new string[] { "command", "file_path", "path", "pattern",
                    "url", "query", "prompt", "description" })
                {
                    string v = Str(d, k);
                    if (!string.IsNullOrEmpty(v)) { detail = v; break; }
                }
            }
            if (detail == null) detail = "";
            int nl = detail.IndexOfAny(new char[] { '\n', '\r' });
            if (nl >= 0) detail = detail.Substring(0, nl);
            if (detail.Length > 100) detail = detail.Substring(0, 99) + "…";

            string key = name + "|" + detail;
            if (_turnSteps.Contains(key)) return;
            _turnSteps.Add(key);

            string text = detail.Length > 0 ? "🔧 " + name + "  " + detail + "\n" : "🔧 " + name + "\n";
            List<Seg> segs = new List<Seg>();
            segs.Add(S(text, Color.Teal, _fMono));
            Commit(segs, true, false);
        }

        // ============================================================ Thinking dots
        void StartThinking()
        {
            StopThinking();
            _thinkStart = _transcript.TextLength;
            _thinkTick = 0;
            RenderThinking();
            _thinkTimer = new Timer();
            _thinkTimer.Interval = 400;
            _thinkTimer.Tick += delegate { _thinkTick++; RenderThinking(); };
            _thinkTimer.Start();
        }

        void RenderThinking()
        {
            int total = _transcript.TextLength;
            if (_thinkStart > total) return;
            string dots = new string('.', _thinkTick % 4);
            int seconds = (int)(_thinkTick * 0.4);
            string elapsed = seconds > 0 ? " (" + seconds + "s)" : "";
            string text = "Claude is thinking" + dots + elapsed + "\n\n";
            _transcript.Select(_thinkStart, total - _thinkStart);
            _transcript.SelectionColor = Color.Gray;
            _transcript.SelectionFont = _fSmall;
            _transcript.SelectedText = text;
            ScrollToBottom();
        }

        void StopThinking()
        {
            if (_thinkTimer == null) return;
            _thinkTimer.Stop();
            _thinkTimer.Dispose();
            _thinkTimer = null;
            // Remove the indicator by locating its marker text from _thinkStart
            // onward — robust even if the offset drifted, and it never eats real
            // content before the marker.
            string all = _transcript.Text;
            int from = Math.Min(Math.Max(0, _thinkStart), all.Length);
            int idx = all.IndexOf("Claude is thinking", from);
            if (idx < 0) return;   // already gone
            int total = _transcript.TextLength;
            _transcript.Select(idx, total - idx);
            _transcript.SelectedText = "";
        }

        // ============================================================ Send
        void OnInputKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter && !e.Shift)
            {
                e.SuppressKeyPress = true;
                OnSend();
            }
        }

        void OnSend()
        {
            string text = (_input.Text ?? "").Trim();

            if (_askPending)
            {
                bool hasChips = _options.Controls.Count > 0;
                if (text.Length == 0 && hasChips) return;
                SubmitAnswer(text);
                return;
            }
            if (_busy) return;
            if (text.Length == 0) return;
            if (string.IsNullOrEmpty(RootDir))
            {
                AppendNote("Open a folder first — that's where Claude will work.");
                return;
            }
            _input.Text = "";
            AppendRole("You", text);
            SetBusy(true);

            string prompt = text;
            if (!string.IsNullOrEmpty(PendingContextNote))
            {
                prompt = "[" + PendingContextNote + "]\n\n" + text;
                PendingContextNote = null;
            }
            RunTurn(prompt);
        }

        void SetBusy(bool busy)
        {
            _busy = busy;
            _send.Enabled = !busy;
            _input.Enabled = !busy;
        }

        public bool IsBusy() { return _busy; }

        public void Abort()
        {
            if (!_busy) return;
            _turnSeq++;
            try { if (_currentProc != null && !_currentProc.HasExited) _currentProc.Kill(); }
            catch { }
            _currentProc = null;
            StopThinking();
            SetBusy(false);
            AppendNote("⏹ Stopped.");
        }

        // ============================================================ CLI turn
        static string ClaudePath()
        {
            string appdata = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string[] candidates = new string[] {
                Path.Combine(appdata, @"npm\node_modules\@anthropic-ai\claude-code\bin\claude.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    @".local\bin\claude.exe"),
            };
            foreach (string p in candidates)
                if (File.Exists(p)) return p;

            // PATH search for claude.exe
            string path = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (string dir in path.Split(';'))
            {
                try
                {
                    if (dir.Length == 0) continue;
                    string cand = Path.Combine(dir, "claude.exe");
                    if (File.Exists(cand)) return cand;
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
                AppendNote("Couldn't find the `claude` CLI. Install Claude Code and run "
                    + "`claude login` to sign in with your subscription, then relaunch.");
                SetBusy(false);
                return;
            }

            string defaultPrompt =
                "You are embedded in a minimal IDE. Make focused changes to files in the "
                + "working directory and briefly explain what you did.\n\n"
                + "When you need the user to make a real decision or resolve an ambiguity, ask a "
                + "multiple-choice question instead of guessing. To do that, reply with ONLY a "
                + "fenced code block labeled ask_user containing JSON, and nothing else in that "
                + "turn:\n```ask_user\n{\"question\": \"Which database?\", \"options\": [\"SQLite\", "
                + "\"Postgres\"]}\n```\nThe IDE renders each option as a clickable button and sends "
                + "the user's choice back as the next message. The user may also type a custom "
                + "answer. Use this only for genuine decisions — don't over-ask.";
            string systemPrompt = CustomSystemPrompt ?? defaultPrompt;

            List<string> args = new List<string> {
                "-p", prompt,
                "--output-format", "stream-json",
                "--verbose",
                "--permission-mode", "bypassPermissions",
                "--disallowedTools", "AskUserQuestion",
                "--append-system-prompt", systemPrompt
            };
            string model = (string)_model.SelectedItem;
            if (!string.IsNullOrEmpty(model) && model != "default")
            {
                args.Add("--model"); args.Add(model);
            }
            if (!string.IsNullOrEmpty(_sessionID))
            {
                args.Add("--resume"); args.Add(_sessionID);
            }

            Process task = new Process();
            task.StartInfo.FileName = cli;
            task.StartInfo.Arguments = Proc.BuildArgs(args);
            task.StartInfo.UseShellExecute = false;
            task.StartInfo.CreateNoWindow = true;
            task.StartInfo.RedirectStandardOutput = true;
            task.StartInfo.RedirectStandardError = true;
            task.StartInfo.StandardOutputEncoding = Encoding.UTF8;
            task.StartInfo.StandardErrorEncoding = Encoding.UTF8;
            if (!string.IsNullOrEmpty(RootDir) && Directory.Exists(RootDir))
                task.StartInfo.WorkingDirectory = RootDir;
            task.EnableRaisingEvents = true;

            // Terminate any still-running previous turn.
            try { if (_currentProc != null && !_currentProc.HasExited) _currentProc.Kill(); }
            catch { }
            _currentProc = task;
            int seq = ++_turnSeq;
            _turnRenderedQuestion = false;
            _turnGotResult = false;
            _lastThinkingBlock = null;
            _lastAssistantText = null;
            _turnSteps = new HashSet<string>();
            _errBuf.Length = 0;

            task.OutputDataReceived += delegate(object s, DataReceivedEventArgs e)
            {
                if (e.Data == null) return;
                string line = e.Data;
                try { BeginInvoke((Action)delegate { HandleStreamLine(line, seq); }); }
                catch { }
            };
            task.ErrorDataReceived += delegate(object s, DataReceivedEventArgs e)
            {
                if (e.Data != null) lock (_errBuf) { _errBuf.Append(e.Data).Append('\n'); }
            };
            task.Exited += delegate
            {
                int status = 0;
                try { status = task.ExitCode; } catch { }
                string err;
                lock (_errBuf) { err = _errBuf.ToString(); }
                try { BeginInvoke((Action)delegate { FinishTurn(seq, status, err); }); }
                catch { }
            };

            StartThinking();
            try
            {
                task.Start();
                task.BeginOutputReadLine();
                task.BeginErrorReadLine();
            }
            catch (Exception ex)
            {
                StopThinking();
                AppendNote("Failed to launch claude: " + ex.Message);
                SetBusy(false);
            }
        }

        void HandleStreamLine(string line, int seq)
        {
            if (seq != _turnSeq) return;
            Dictionary<string, object> o = ParseJson(line);
            if (o == null) return;

            string sid = Str(o, "session_id");
            if (!string.IsNullOrEmpty(sid)) _sessionID = sid;

            string type = Str(o, "type");

            if (type == "assistant" && !_turnRenderedQuestion)
            {
                Dictionary<string, object> msg = AsDict(GetVal(o, "message"));
                object[] content = msg != null ? AsArr(GetVal(msg, "content")) : null;
                if (content != null)
                {
                    foreach (object ci in content)
                    {
                        Dictionary<string, object> c = AsDict(ci);
                        if (c == null) continue;
                        string ctype = Str(c, "type");
                        if (ctype == "thinking") { IngestThinking(Str(c, "thinking")); continue; }
                        if (ctype == "text") { IngestAssistantText(Str(c, "text")); continue; }
                        if (ctype == "tool_use")
                        {
                            string name = Str(c, "name");
                            Dictionary<string, object> input = AsDict(GetVal(c, "input"));
                            if (name == "AskUserQuestion" && input != null)
                            {
                                _turnRenderedQuestion = true;
                                StopThinking();
                                RenderAskUserToolInput(input);
                                try { if (_currentProc != null && !_currentProc.HasExited) _currentProc.Kill(); }
                                catch { }
                                break;
                            }
                            if (IsFileEditTool(name) && input != null) IngestFileEdit(name, input);
                            else IngestToolStep(name, GetVal(c, "input"));
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

                bool isError = BoolVal(o, "is_error");
                string result = Str(o, "result") ?? "";
                if (isError)
                {
                    AppendNote("Claude error: " + (result.Length > 0 ? result : "unknown error"));
                    SetBusy(false);
                    return;
                }

                Dictionary<string, object> ask = ParseAskUserFromText(result);
                if (ask != null)
                {
                    SetBusy(false);
                    BeginAskUser(Str(ask, "question"), (List<string>)ask["options"]);
                    return;
                }

                string ans = TextWithoutAskUserBlock(result);
                if (string.IsNullOrEmpty(ans)) ans = result.Length > 0 ? result : "(no text)";
                bool alreadyInFull = !string.IsNullOrEmpty(_lastAssistantText)
                    && ans.Trim() == _lastAssistantText.Trim();
                List<Seg> segs = new List<Seg>();
                segs.Add(S("Claude\n", Color.Black, _fBold));
                segs.Add(S(ans + "\n\n", Color.Black, _fBody));
                Commit(segs, !alreadyInFull, true);
                SetBusy(false);
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

        // ============================================================ ask_user
        void RenderAskUserToolInput(Dictionary<string, object> input)
        {
            Dictionary<string, object> qd = input;
            object[] questions = AsArr(GetVal(input, "questions"));
            if (questions != null && questions.Length > 0 && AsDict(questions[0]) != null)
                qd = AsDict(questions[0]);

            string q = Str(qd, "question");
            if (string.IsNullOrEmpty(q)) return;
            List<string> opts = new List<string>();
            object[] optArr = AsArr(GetVal(qd, "options"));
            if (optArr != null)
                foreach (object opt in optArr)
                {
                    string label = LabelFromOption(opt);
                    if (!string.IsNullOrEmpty(label)) opts.Add(label);
                }
            SetBusy(false);
            BeginAskUser(q, opts);
        }

        void BeginAskUser(string question, List<string> options)
        {
            AppendRole("Claude", question);
            _askPending = true;

            _options.Controls.Clear();
            if (options != null && options.Count > 0)
            {
                foreach (string opt in options)
                {
                    Button chip = new Button();
                    chip.Text = opt;
                    chip.AutoSize = false;
                    chip.Width = Math.Max(120, _options.ClientSize.Width - 26);
                    chip.Height = 26;
                    chip.TextAlign = ContentAlignment.MiddleLeft;
                    chip.FlatStyle = FlatStyle.System;
                    string captured = opt;
                    chip.Click += delegate { if (_askPending) SubmitAnswer(captured); };
                    _options.Controls.Add(chip);
                }
                int h = Math.Min(160, options.Count * 32 + 8);
                _options.Height = h;
                _options.Visible = true;
                _input.Text = "";
            }
            else
            {
                _options.Visible = false;
                _options.Height = 0;
            }
            _input.Focus();
        }

        void SubmitAnswer(string answer)
        {
            _askPending = false;
            _options.Controls.Clear();
            _options.Visible = false;
            _options.Height = 0;
            _input.Text = "";
            AppendNote("You answered: " + (answer.Length > 0 ? answer : "(empty)"));
            SetBusy(true);
            RunTurn(answer);
        }

        string LabelFromOption(object o)
        {
            if (o is string) return (string)o;
            Dictionary<string, object> d = AsDict(o);
            if (d != null)
                foreach (string k in new string[] { "label", "text", "title", "value", "name", "option" })
                {
                    string v = Str(d, k);
                    if (!string.IsNullOrEmpty(v)) return v;
                }
            return null;
        }

        string FirstJsonObject(string text)
        {
            int start = -1, depth = 0;
            bool inStr = false, esc = false;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (inStr)
                {
                    if (esc) esc = false;
                    else if (c == '\\') esc = true;
                    else if (c == '"') inStr = false;
                    continue;
                }
                if (c == '"') inStr = true;
                else if (c == '{') { if (depth == 0) start = i; depth++; }
                else if (c == '}')
                {
                    if (depth > 0 && --depth == 0 && start >= 0)
                        return text.Substring(start, i - start + 1);
                }
            }
            return null;
        }

        string TextWithoutAskUserBlock(string text)
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

        Dictionary<string, object> ParseAskUserFromText(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
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
                if (cand != null && (cand.Contains("\"question\"") || cand.Contains("\"questions\"")))
                    jsonStr = cand;
            }
            if (jsonStr == null) return null;
            jsonStr = jsonStr.Trim();
            Dictionary<string, object> obj = ParseJson(jsonStr);
            if (obj == null) return null;

            Dictionary<string, object> qd = obj;
            object[] questions = AsArr(GetVal(obj, "questions"));
            if (questions != null && questions.Length > 0 && AsDict(questions[0]) != null)
                qd = AsDict(questions[0]);

            string q = Str(qd, "question");
            if (string.IsNullOrEmpty(q)) return null;
            List<string> opts = new List<string>();
            object[] optArr = AsArr(GetVal(qd, "options"));
            if (optArr != null)
                foreach (object o in optArr)
                {
                    string label = LabelFromOption(o);
                    if (!string.IsNullOrEmpty(label)) opts.Add(label);
                }
            Dictionary<string, object> result = new Dictionary<string, object>();
            result["question"] = q;
            result["options"] = opts;
            return result;
        }

        // ============================================================ JSON helpers
        static Dictionary<string, object> ParseJson(string line)
        {
            try
            {
                JavaScriptSerializer ser = new JavaScriptSerializer();
                ser.MaxJsonLength = int.MaxValue;
                return ser.DeserializeObject(line) as Dictionary<string, object>;
            }
            catch { return null; }
        }

        static Dictionary<string, object> AsDict(object o) { return o as Dictionary<string, object>; }
        static object[] AsArr(object o) { return o as object[]; }

        static object GetVal(Dictionary<string, object> d, string key)
        {
            object v;
            if (d != null && d.TryGetValue(key, out v)) return v;
            return null;
        }

        static string Str(Dictionary<string, object> d, string key)
        {
            object v = GetVal(d, key);
            return v as string;
        }

        static bool BoolVal(Dictionary<string, object> d, string key)
        {
            object v = GetVal(d, key);
            if (v is bool) return (bool)v;
            return false;
        }
    }
}
