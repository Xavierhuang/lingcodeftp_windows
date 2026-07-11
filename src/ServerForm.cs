using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace LingCodeFTP
{
    // Port of FTPWindowController: the per-server window. Three panes —
    // file browser | code editor | Claude chat — with a clickable breadcrumb
    // on top and a status bar at the bottom. Selecting a file downloads and
    // shows it (syntax-highlighted); Ctrl+S uploads it back. On first
    // successful connection it wires up Claude's MCP workspace so the chat pane
    // can operate on the remote server.
    public class ServerForm : Form
    {
        readonly ServerAccount _account;
        readonly FtpClient _client;
        FileBrowser _browser;
        RichTextBox _editor;
        ClaudeChat _claude;
        FlowLayoutPanel _breadcrumb;
        ToolStripStatusLabel _status, _lang;
        Timer _highlightTimer;

        RemoteFileNode _currentNode;
        bool _dirty;
        bool _suppressDirty;
        string _claudeWorkspaceDir;

        public ServerForm(ServerAccount account)
        {
            _account = account;
            _client = new FtpClient(account, this);

            Text = (account.Name ?? "Server") + " — FTP";
            Width = 1280;
            Height = 760;
            MinimumSize = new Size(820, 460);
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Segoe UI", 9f);

            BuildUI();
            BuildMenu();   // added last so the menu strip owns the very top

            Load += delegate { InitSplitters(); Connect(); };
            FormClosing += delegate { OnClosingCleanup(); };
        }

        // ---------------------------------------------------------------- Menu
        void BuildMenu()
        {
            MenuStrip menu = new MenuStrip();

            ToolStripMenuItem file = new ToolStripMenuItem("File");
            ToolStripMenuItem save = new ToolStripMenuItem("Save", null, delegate { UploadCurrentFile(); });
            save.ShortcutKeys = Keys.Control | Keys.S;
            ToolStripMenuItem close = new ToolStripMenuItem("Close Window", null, delegate { Close(); });
            close.ShortcutKeys = Keys.Control | Keys.W;
            file.DropDownItems.Add(save);
            file.DropDownItems.Add(close);
            menu.Items.Add(file);

            ToolStripMenuItem claude = new ToolStripMenuItem("Claude");
            ToolStripMenuItem think = new ToolStripMenuItem("Show/Hide Thinking Steps", null,
                delegate { _claude.ToggleThinking(); });
            think.ShortcutKeys = Keys.Control | Keys.Shift | Keys.T;
            ToolStripMenuItem stop = new ToolStripMenuItem("Stop", null, delegate { _claude.Abort(); });
            stop.ShortcutKeys = Keys.Control | Keys.OemPeriod;
            claude.DropDownItems.Add(think);
            claude.DropDownItems.Add(stop);
            menu.Items.Add(claude);

            MainMenuStrip = menu;
            Controls.Add(menu);
        }

        // ------------------------------------------------------------------ UI
        void BuildUI()
        {
            // Breadcrumb (top)
            _breadcrumb = new FlowLayoutPanel();
            _breadcrumb.Dock = DockStyle.Top;
            _breadcrumb.Height = 30;
            _breadcrumb.WrapContents = false;
            _breadcrumb.AutoScroll = true;
            _breadcrumb.BackColor = Color.FromArgb(238, 238, 238);
            _breadcrumb.Padding = new Padding(6, 4, 6, 0);

            // Status bar (bottom)
            StatusStrip strip = new StatusStrip();
            _status = new ToolStripStatusLabel("Connecting…");
            _status.Spring = true;
            _status.TextAlign = ContentAlignment.MiddleLeft;
            _lang = new ToolStripStatusLabel("");
            strip.Items.Add(_status);
            strip.Items.Add(_lang);

            // Main split: browser | (editor | claude)
            SplitContainer main = new SplitContainer();
            main.Dock = DockStyle.Fill;
            main.SplitterWidth = 5;
            main.Name = "main";

            string rootPath = string.IsNullOrEmpty(_account.RemotePath) ? "/" : _account.RemotePath;
            _browser = new FileBrowser(_client, rootPath);
            _browser.Dock = DockStyle.Fill;
            _browser.FileSelected += OnFileSelected;
            _browser.Navigated += OnNavigated;
            _browser.RootLoaded += OnRootLoaded;
            _browser.NodeDeleted += OnNodeDeleted;
            _browser.NodeRenamed += OnNodeRenamed;
            _browser.Status += OnBrowserStatus;
            main.Panel1.Controls.Add(_browser);

            SplitContainer right = new SplitContainer();
            right.Dock = DockStyle.Fill;
            right.SplitterWidth = 5;
            right.Name = "right";

            _editor = new RichTextBox();
            _editor.Dock = DockStyle.Fill;
            _editor.Font = new Font("Consolas", 10.5f);
            _editor.BorderStyle = BorderStyle.None;
            _editor.AcceptsTab = true;
            _editor.HideSelection = false;
            _editor.WordWrap = false;
            _editor.TextChanged += OnEditorChanged;
            right.Panel1.Controls.Add(_editor);

            _claude = new ClaudeChat();
            _claude.Dock = DockStyle.Fill;
            _claude.FilesModified += delegate { _browser.ReloadRoot(); };
            right.Panel2.Controls.Add(_claude);

            main.Panel2.Controls.Add(right);

            // Add in order so docking resolves: fill center, then bottom, then top.
            Controls.Add(main);        // index 0 -> docked last -> fills remainder
            Controls.Add(strip);       // bottom
            Controls.Add(_breadcrumb); // top

            SetEditorPlaceholder();

            _highlightTimer = new Timer();
            _highlightTimer.Interval = 350;
            _highlightTimer.Tick += delegate
            {
                _highlightTimer.Stop();
                if (_currentNode != null) SyntaxHighlighter.Highlight(_editor, _currentNode.FullPath);
            };

            SetPath(rootPath);
        }

        void InitSplitters()
        {
            SplitContainer main = (SplitContainer)Controls["main"];
            try { main.SplitterDistance = 240; } catch { }
            SplitContainer right = (SplitContainer)main.Panel2.Controls[0];
            try { right.SplitterDistance = Math.Max(300, right.Width - 360); } catch { }
        }

        void Connect()
        {
            _status.Text = "Connecting to " + _account.Host + " ("
                + (_account.Protocol ?? "ftp").ToUpperInvariant() + ")…";
            _browser.ReloadRoot();
        }

        // ------------------------------------------------------------ Breadcrumb
        void SetPath(string rawPath)
        {
            _breadcrumb.Controls.Clear();
            string path = string.IsNullOrEmpty(rawPath) ? "/" : rawPath;

            List<string> labels = new List<string>();
            List<string> paths = new List<string>();
            labels.Add("/"); paths.Add("/");

            string stripped = path;
            while (stripped.EndsWith("/") && stripped.Length > 1)
                stripped = stripped.Substring(0, stripped.Length - 1);

            string acc = "";
            foreach (string c in stripped.Split('/'))
            {
                if (c.Length == 0) continue;
                acc = acc + "/" + c;
                labels.Add(c);
                paths.Add(acc + "/");
            }

            for (int i = 0; i < labels.Count; i++)
            {
                bool isLast = (i == labels.Count - 1);
                LinkLabel link = new LinkLabel();
                link.Text = labels[i];
                link.AutoSize = true;
                link.Margin = new Padding(0, 4, 0, 0);
                link.LinkBehavior = LinkBehavior.HoverUnderline;
                link.Font = isLast ? new Font("Segoe UI", 9f, FontStyle.Bold) : new Font("Segoe UI", 9f);
                link.LinkColor = isLast ? Color.Black : Color.FromArgb(80, 80, 80);
                string target = paths[i];
                link.Click += delegate { _browser.NavigateToPath(target); };
                _breadcrumb.Controls.Add(link);

                if (!isLast)
                {
                    Label sep = new Label();
                    sep.Text = "›";
                    sep.AutoSize = true;
                    sep.ForeColor = Color.Silver;
                    sep.Margin = new Padding(2, 4, 2, 0);
                    _breadcrumb.Controls.Add(sep);
                }
            }
        }

        // --------------------------------------------------------- Browser events
        void OnRootLoaded(int count, string error)
        {
            SetPath(_browser.CurrentPath);
            if (error != null)
            {
                _status.Text = "Connection failed: " + error;
                _status.ForeColor = Color.Firebrick;
            }
            else
            {
                _status.Text = _account.Host + "  ·  " + count + " items";
                _status.ForeColor = SystemColors.ControlText;
                if (_claudeWorkspaceDir == null) SetupClaudeWorkspace();
            }
        }

        void OnNavigated(string path)
        {
            SetPath(path);
            _status.Text = "Loading " + path + "…";
            _status.ForeColor = SystemColors.ControlText;
            if (_claudeWorkspaceDir != null)
                _claude.PendingContextNote = "Current folder: " + path;
        }

        void OnBrowserStatus(string msg, bool isError)
        {
            _status.Text = msg;
            _status.ForeColor = isError ? Color.Firebrick : SystemColors.ControlText;
        }

        void OnNodeDeleted(RemoteFileNode node)
        {
            if (_currentNode == node)
            {
                _currentNode = null;
                _dirty = false;
                SetEditorPlaceholder();
                _lang.Text = "";
                Text = (_account.Name ?? "FTP");
            }
        }

        void OnNodeRenamed(RemoteFileNode node, string oldPath)
        {
            if (_currentNode != null && _currentNode.FullPath == oldPath)
                _currentNode = node;
        }

        void OnFileSelected(RemoteFileNode node)
        {
            if (_dirty)
            {
                if (MessageBox.Show("Discard changes to " + (_currentNode != null ? _currentNode.Name : "")
                    + " and open the new file?", "Unsaved Changes",
                    MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;
                _dirty = false;
            }
            OpenNode(node);
            _claude.PostNote("User opened: " + node.FullPath);
        }

        void OpenNode(RemoteFileNode node)
        {
            _status.Text = "Downloading " + node.Name + "…";
            _status.ForeColor = SystemColors.ControlText;

            _client.DownloadFile(node.FullPath, delegate(byte[] data, string error)
            {
                if (error != null || data == null)
                {
                    _status.Text = "Error: " + error;
                    _status.ForeColor = Color.Firebrick;
                    return;
                }
                string text = DecodeText(data);
                _suppressDirty = true;   // stays true through highlighting below
                _editor.ReadOnly = false;
                _editor.Text = text;

                _currentNode = node;
                _dirty = false;
                Text = node.Name + " — " + _account.Name;

                string ext = Path.GetExtension(node.FullPath);
                _lang.Text = ext.Length > 1 ? ext.Substring(1).ToUpperInvariant() : "";
                _status.Text = node.Name + "  ·  " + data.Length + " bytes";
                _status.ForeColor = SystemColors.ControlText;

                SyntaxHighlighter.Highlight(_editor, node.FullPath);
                _suppressDirty = false;
            });
        }

        static string DecodeText(byte[] data)
        {
            try
            {
                // Reject if it looks binary (lots of NULs).
                int nul = 0;
                int scan = Math.Min(data.Length, 4000);
                for (int i = 0; i < scan; i++) if (data[i] == 0) nul++;
                if (nul > scan / 20) return "(Binary file — cannot display)";
                return new UTF8Encoding(false, false).GetString(data);
            }
            catch { return "(Binary file — cannot display)"; }
        }

        void SetEditorPlaceholder()
        {
            _suppressDirty = true;
            _editor.Text = "Select a file in the sidebar to view and edit it.\r\n\r\n"
                + "Double-click a folder to navigate into it.";
            _editor.SelectAll();
            _editor.SelectionColor = Color.Gray;
            _editor.Select(0, 0);
            _editor.ReadOnly = true;
            _suppressDirty = false;
        }

        void OnEditorChanged(object sender, EventArgs e)
        {
            if (_suppressDirty) return;
            if (!_dirty && _currentNode != null)
            {
                _dirty = true;
                Text = _currentNode.Name + " — " + _account.Name + " ●";
            }
            if (_currentNode != null)
            {
                _highlightTimer.Stop();
                _highlightTimer.Start();
            }
        }

        // ------------------------------------------------------------------ Save
        void UploadCurrentFile()
        {
            if (_currentNode == null) return;
            byte[] data = new UTF8Encoding(false).GetBytes(_editor.Text.Replace("\r\n", "\n"));
            _status.Text = "Saving " + _currentNode.Name + "…";
            _status.ForeColor = SystemColors.ControlText;

            RemoteFileNode node = _currentNode;
            _client.UploadData(data, node.FullPath, delegate(string err)
            {
                if (err != null)
                {
                    _status.Text = "Save failed: " + err;
                    _status.ForeColor = Color.Firebrick;
                }
                else
                {
                    _dirty = false;
                    Text = node.Name + " — " + _account.Name;
                    _status.Text = "Saved: " + node.Name;
                    _status.ForeColor = SystemColors.ControlText;
                }
            });
        }

        // ----------------------------------------------------- Claude workspace
        void SetupClaudeWorkspace()
        {
            string script = AppPaths.McpServerScript();
            if (!File.Exists(script))
            {
                _claude.PostNote("⚠️ ftp_mcp_server.py not found next to the app — Claude cannot "
                    + "access the server. (Expected at " + script + ")");
                // Still enable chat with the current folder as cwd.
                _claudeWorkspaceDir = AppPaths.ClaudeWorkspace(_account.Host ?? "server");
                _claude.SetRootDir(_claudeWorkspaceDir);
                return;
            }

            string dir = AppPaths.ClaudeWorkspace(_account.Host ?? "server");
            _claudeWorkspaceDir = dir;

            string protocol = (string.IsNullOrEmpty(_account.Protocol) ? "ftp" : _account.Protocol).ToLowerInvariant();
            int port = _account.Port > 0 ? _account.Port : (protocol == "sftp" ? 22 : 21);

            Dictionary<string, object> env = new Dictionary<string, object>();
            env["FTP_HOST"] = _account.Host ?? "";
            env["FTP_PORT"] = port.ToString();
            env["FTP_USER"] = _account.Username ?? "";
            env["FTP_PASS"] = _account.Password ?? "";
            env["FTP_PROTOCOL"] = protocol;
            if (!string.IsNullOrEmpty(_account.IdentityFile))
                env["FTP_IDENTITY"] = _account.IdentityFile;

            Dictionary<string, object> ftpServer = new Dictionary<string, object>();
            ftpServer["type"] = "stdio";
            ftpServer["command"] = "python";
            ftpServer["args"] = new object[] { script };
            ftpServer["env"] = env;

            Dictionary<string, object> servers = new Dictionary<string, object>();
            servers["ftp"] = ftpServer;
            Dictionary<string, object> mcp = new Dictionary<string, object>();
            mcp["mcpServers"] = servers;

            try
            {
                JavaScriptSerializer ser = new JavaScriptSerializer();
                File.WriteAllText(Path.Combine(dir, ".mcp.json"), ser.Serialize(mcp));
            }
            catch (Exception ex)
            {
                _claude.PostNote("⚠️ Could not write .mcp.json: " + ex.Message);
            }

            _claude.SetRootDir(dir);

            string proto = protocol.ToUpperInvariant();
            _claude.CustomSystemPrompt =
                "You are an AI assistant embedded in an FTP/SFTP file manager.\n"
                + "You are connected to: " + _account.Host + " via " + proto + ".\n\n"
                + "You have MCP tools that operate directly on the remote server:\n"
                + "  • ftp_list(path)                  — list a directory\n"
                + "  • ftp_read(path)                  — read a file's content\n"
                + "  • ftp_write(path, content)        — write/create a file\n"
                + "  • ftp_delete(path)                — delete a file\n"
                + "  • ftp_rename(from_path, to_path)  — rename or move\n"
                + "  • ftp_mkdir(path)                 — create a directory\n"
                + "  • ssh_exec(command)               — run a shell command (SFTP only)\n\n"
                + "Use these tools to help the user inspect and modify remote files. Be careful "
                + "with destructive operations (delete, overwrite) — confirm with the user when "
                + "uncertain. Briefly explain what you did after each action.\n\n"
                + "When you need the user to make a real decision, reply with ONLY a fenced code "
                + "block labeled ask_user containing JSON:\n```ask_user\n{\"question\": \"Which "
                + "file?\", \"options\": [\"config.php\", \"index.html\"]}\n```\nThe app renders each "
                + "option as a clickable button.";

            _claude.PostNote("Connected to " + _account.Host + " (" + proto + "). I can list, read, "
                + "write, delete, rename, and create directories on the server using MCP tools. "
                + "What would you like to do?");
        }

        void OnClosingCleanup()
        {
            try { _claude.Abort(); } catch { }
            if (_claudeWorkspaceDir != null)
            {
                try { if (Directory.Exists(_claudeWorkspaceDir)) Directory.Delete(_claudeWorkspaceDir, true); }
                catch { }
                _claudeWorkspaceDir = null;
            }
        }
    }
}
