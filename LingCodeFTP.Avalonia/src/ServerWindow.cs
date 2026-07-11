using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace LingCodeFtp
{
    // Port of FTPWindowController: browser | editor | Claude, with a clickable
    // breadcrumb and a status bar. Selecting a file downloads + shows it;
    // Ctrl+S uploads it back. First successful connect wires up Claude's MCP
    // workspace so the chat pane can operate on the remote server.
    public class ServerWindow : Window
    {
        readonly ServerAccount _account;
        readonly FtpClient _client;
        FileBrowserControl _browser;
        TextBox _editor;
        ClaudeChatControl _claude;
        WrapPanel _breadcrumb;
        TextBlock _status, _lang;

        RemoteFileNode _currentNode;
        bool _dirty;
        bool _suppressDirty;
        string _claudeWorkspaceDir;

        static readonly IBrush Light = new SolidColorBrush(Color.FromRgb(238, 238, 238));

        public ServerWindow(ServerAccount account)
        {
            _account = account;
            _client = new FtpClient(account);

            Title = (account.Name ?? "Server") + " — FTP";
            Width = 1280; Height = 760;
            MinWidth = 820; MinHeight = 460;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;

            BuildUI();
            KeyDown += OnKeyDown;
            Opened += delegate { Connect(); };
            Closing += delegate { OnClosingCleanup(); };
        }

        void BuildUI()
        {
            DockPanel root = new DockPanel();

            // menu
            Menu menu = new Menu();
            MenuItem file = new MenuItem { Header = "File" };
            MenuItem save = new MenuItem { Header = "Save", InputGesture = new KeyGesture(Key.S, KeyModifiers.Control) };
            save.Click += delegate { UploadCurrentFile(); };
            MenuItem close = new MenuItem { Header = "Close Window", InputGesture = new KeyGesture(Key.W, KeyModifiers.Control) };
            close.Click += delegate { Close(); };
            file.Items.Add(save); file.Items.Add(close);
            MenuItem claudeM = new MenuItem { Header = "Claude" };
            MenuItem think = new MenuItem { Header = "Show/Hide Thinking Steps", InputGesture = new KeyGesture(Key.T, KeyModifiers.Control | KeyModifiers.Shift) };
            think.Click += delegate { _claude.ToggleThinking(); };
            MenuItem stop = new MenuItem { Header = "Stop", InputGesture = new KeyGesture(Key.OemPeriod, KeyModifiers.Control) };
            stop.Click += delegate { _claude.Abort(); };
            claudeM.Items.Add(think); claudeM.Items.Add(stop);
            menu.Items.Add(file); menu.Items.Add(claudeM);
            DockPanel.SetDock(menu, Dock.Top);
            root.Children.Add(menu);

            // breadcrumb
            _breadcrumb = new WrapPanel { Background = Light, Margin = new Thickness(6, 3, 6, 3), MinHeight = 26 };
            DockPanel.SetDock(_breadcrumb, Dock.Top);
            root.Children.Add(_breadcrumb);

            // status bar
            Grid statusBar = new Grid { Height = 24, Background = new SolidColorBrush(Color.FromRgb(240, 240, 240)) };
            statusBar.ColumnDefinitions = new ColumnDefinitions("*,Auto");
            _status = new TextBlock { Text = "Connecting…", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0), Foreground = Brushes.DimGray };
            _lang = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0), Foreground = Brushes.DimGray };
            Grid.SetColumn(_status, 0); Grid.SetColumn(_lang, 1);
            statusBar.Children.Add(_status); statusBar.Children.Add(_lang);
            DockPanel.SetDock(statusBar, Dock.Bottom);
            root.Children.Add(statusBar);

            // 3-pane grid
            Grid main = new Grid();
            main.ColumnDefinitions = new ColumnDefinitions("240,4,*,4,380");

            string rootPath = string.IsNullOrEmpty(_account.RemotePath) ? "/" : _account.RemotePath;
            _browser = new FileBrowserControl(_client, rootPath);
            _browser.FileSelected += OnFileSelected;
            _browser.Navigated += OnNavigated;
            _browser.RootLoaded += OnRootLoaded;
            _browser.NodeDeleted += OnNodeDeleted;
            _browser.NodeRenamed += OnNodeRenamed;
            _browser.StatusUpdate += OnBrowserStatus;
            Grid.SetColumn(_browser, 0);

            GridSplitter s1 = new GridSplitter { Background = Light };
            Grid.SetColumn(s1, 1);

            _editor = new TextBox
            {
                AcceptsReturn = true, AcceptsTab = true, TextWrapping = TextWrapping.NoWrap,
                FontFamily = new FontFamily("Cascadia Code,Consolas,Menlo,DejaVu Sans Mono,monospace"),
                FontSize = 13, BorderThickness = new Thickness(0), IsReadOnly = true
            };
            _editor.TextChanged += OnEditorChanged;
            Grid.SetColumn(_editor, 2);

            GridSplitter s2 = new GridSplitter { Background = Light };
            Grid.SetColumn(s2, 3);

            _claude = new ClaudeChatControl();
            _claude.FilesModified += delegate { _browser.ReloadRoot(); };
            Grid.SetColumn(_claude, 4);

            main.Children.Add(_browser);
            main.Children.Add(s1);
            main.Children.Add(_editor);
            main.Children.Add(s2);
            main.Children.Add(_claude);
            root.Children.Add(main);

            Content = root;

            SetEditorPlaceholder();
            SetPath(rootPath);
        }

        void OnKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.Key == Key.S) { UploadCurrentFile(); e.Handled = true; }
            else if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.Key == Key.W) { Close(); e.Handled = true; }
            else if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.KeyModifiers.HasFlag(KeyModifiers.Shift) && e.Key == Key.T) { _claude.ToggleThinking(); e.Handled = true; }
            else if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.Key == Key.OemPeriod) { _claude.Abort(); e.Handled = true; }
        }

        void Connect()
        {
            _status.Text = "Connecting to " + _account.Host + " (" + (_account.Protocol ?? "ftp").ToUpperInvariant() + ")…";
            _browser.ReloadRoot();
        }

        void SetPath(string rawPath)
        {
            _breadcrumb.Children.Clear();
            string path = string.IsNullOrEmpty(rawPath) ? "/" : rawPath;
            List<string> labels = new List<string> { "/" };
            List<string> paths = new List<string> { "/" };
            string stripped = path;
            while (stripped.EndsWith("/") && stripped.Length > 1) stripped = stripped.Substring(0, stripped.Length - 1);
            string acc = "";
            foreach (string c in stripped.Split('/'))
            {
                if (c.Length == 0) continue;
                acc = acc + "/" + c;
                labels.Add(c); paths.Add(acc + "/");
            }
            for (int i = 0; i < labels.Count; i++)
            {
                bool isLast = i == labels.Count - 1;
                string target = paths[i];
                Button link = new Button
                {
                    Content = labels[i], Background = Brushes.Transparent, BorderThickness = new Thickness(0),
                    Padding = new Thickness(3, 1, 3, 1), FontWeight = isLast ? FontWeight.Bold : FontWeight.Normal,
                    Foreground = isLast ? Brushes.Black : new SolidColorBrush(Color.FromRgb(80, 80, 80))
                };
                link.Click += delegate { _browser.NavigateToPath(target); };
                _breadcrumb.Children.Add(link);
                if (!isLast) _breadcrumb.Children.Add(new TextBlock { Text = "›", Foreground = Brushes.Silver, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 2, 0) });
            }
        }

        void OnRootLoaded(int count, string error)
        {
            SetPath(_browser.CurrentPath);
            if (error != null) { _status.Text = "Connection failed: " + error; _status.Foreground = Brushes.Firebrick; }
            else
            {
                _status.Text = _account.Host + "  ·  " + count + " items";
                _status.Foreground = Brushes.DimGray;
                if (_claudeWorkspaceDir == null) SetupClaudeWorkspace();
            }
        }

        void OnNavigated(string path)
        {
            SetPath(path);
            _status.Text = "Loading " + path + "…";
            _status.Foreground = Brushes.DimGray;
            if (_claudeWorkspaceDir != null) _claude.PendingContextNote = "Current folder: " + path;
        }

        void OnBrowserStatus(string msg, bool isError) { _status.Text = msg; _status.Foreground = isError ? Brushes.Firebrick : Brushes.DimGray; }

        void OnNodeDeleted(RemoteFileNode node)
        {
            if (_currentNode == node) { _currentNode = null; _dirty = false; SetEditorPlaceholder(); _lang.Text = ""; Title = _account.Name ?? "FTP"; }
        }

        void OnNodeRenamed(RemoteFileNode node, string oldPath)
        {
            if (_currentNode != null && _currentNode.FullPath == oldPath) _currentNode = node;
        }

        async void OnFileSelected(RemoteFileNode node)
        {
            if (_dirty)
            {
                bool ok = await Dialogs.Confirm(this, "Unsaved Changes", "Discard changes to " + (_currentNode != null ? _currentNode.Name : "") + " and open the new file?");
                if (!ok) return;
                _dirty = false;
            }
            OpenNode(node);
            _claude.PostNote("User opened: " + node.FullPath);
        }

        void OpenNode(RemoteFileNode node)
        {
            _status.Text = "Downloading " + node.Name + "…";
            _status.Foreground = Brushes.DimGray;
            _client.DownloadFile(node.FullPath, delegate(byte[] data, string error)
            {
                if (error != null || data == null) { _status.Text = "Error: " + error; _status.Foreground = Brushes.Firebrick; return; }
                _suppressDirty = true;   // stays true past any deferred TextChanged
                _editor.IsReadOnly = false;
                _editor.Text = DecodeText(data);
                _currentNode = node;
                _dirty = false;
                Title = node.Name + " — " + _account.Name;
                string ext = Path.GetExtension(node.FullPath);
                _lang.Text = ext.Length > 1 ? ext.Substring(1).ToUpperInvariant() : "";
                _status.Text = node.Name + "  ·  " + data.Length + " bytes";
                _status.Foreground = Brushes.DimGray;
                RemoteFileNode loaded = node;
                Dispatcher.UIThread.Post(delegate
                {
                    _suppressDirty = false;
                    _dirty = false;
                    if (_currentNode == loaded) Title = loaded.Name + " — " + _account.Name;
                }, DispatcherPriority.Background);
            });
        }

        static string DecodeText(byte[] data)
        {
            try
            {
                int nul = 0, scan = Math.Min(data.Length, 4000);
                for (int i = 0; i < scan; i++) if (data[i] == 0) nul++;
                if (nul > scan / 20) return "(Binary file — cannot display)";
                return new UTF8Encoding(false, false).GetString(data);
            }
            catch { return "(Binary file — cannot display)"; }
        }

        void SetEditorPlaceholder()
        {
            _suppressDirty = true;
            _editor.Text = "Select a file in the sidebar to view and edit it.\n\nDouble-click a folder to navigate into it.";
            _editor.IsReadOnly = true;
            _suppressDirty = false;
        }

        void OnEditorChanged(object sender, EventArgs e)
        {
            if (_suppressDirty) return;
            if (!_dirty && _currentNode != null) { _dirty = true; Title = _currentNode.Name + " — " + _account.Name + " ●"; }
        }

        void UploadCurrentFile()
        {
            if (_currentNode == null) return;
            byte[] data = new UTF8Encoding(false).GetBytes((_editor.Text ?? "").Replace("\r\n", "\n"));
            _status.Text = "Saving " + _currentNode.Name + "…";
            _status.Foreground = Brushes.DimGray;
            RemoteFileNode node = _currentNode;
            _client.UploadData(data, node.FullPath, delegate(string err)
            {
                if (err != null) { _status.Text = "Save failed: " + err; _status.Foreground = Brushes.Firebrick; }
                else { _dirty = false; Title = node.Name + " — " + _account.Name; _status.Text = "Saved: " + node.Name; _status.Foreground = Brushes.DimGray; }
            });
        }

        void SetupClaudeWorkspace()
        {
            string script = AppPaths.McpServerScript();
            string dir = AppPaths.ClaudeWorkspace(_account.Host ?? "server");
            _claudeWorkspaceDir = dir;

            if (!File.Exists(script))
            {
                _claude.PostNote("⚠️ ftp_mcp_server.py not found next to the app — Claude can chat but has no server tools. (Expected at " + script + ")");
                _claude.SetRootDir(dir);
                return;
            }

            string protocol = (string.IsNullOrEmpty(_account.Protocol) ? "ftp" : _account.Protocol).ToLowerInvariant();
            int port = _account.Port > 0 ? _account.Port : (protocol == "sftp" ? 22 : 21);

            Dictionary<string, object> env = new Dictionary<string, object>
            {
                { "FTP_HOST", _account.Host ?? "" },
                { "FTP_PORT", port.ToString() },
                { "FTP_USER", _account.Username ?? "" },
                { "FTP_PASS", _account.Password ?? "" },
                { "FTP_PROTOCOL", protocol },
            };
            if (!string.IsNullOrEmpty(_account.IdentityFile)) env["FTP_IDENTITY"] = _account.IdentityFile;

            string pythonCmd = OperatingSystem.IsWindows() ? "python" : "python3";
            Dictionary<string, object> ftpServer = new Dictionary<string, object>
            {
                { "type", "stdio" }, { "command", pythonCmd }, { "args", new[] { script } }, { "env", env }
            };
            Dictionary<string, object> mcp = new Dictionary<string, object>
            {
                { "mcpServers", new Dictionary<string, object> { { "ftp", ftpServer } } }
            };
            try { File.WriteAllText(Path.Combine(dir, ".mcp.json"), JsonSerializer.Serialize(mcp, new JsonSerializerOptions { WriteIndented = true })); }
            catch (Exception ex) { _claude.PostNote("⚠️ Could not write .mcp.json: " + ex.Message); }

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
                + "Use these tools to help the user inspect and modify remote files. Be careful with "
                + "destructive operations — confirm when uncertain. Briefly explain what you did.\n\n"
                + "When you need the user to make a real decision, reply with ONLY a fenced code block "
                + "labeled ask_user containing JSON:\n```ask_user\n{\"question\": \"Which file?\", "
                + "\"options\": [\"config.php\", \"index.html\"]}\n```\nThe app renders each option as a button.";
            _claude.PostNote("Connected to " + _account.Host + " (" + proto + "). I can list, read, write, delete, "
                + "rename, and create directories on the server using MCP tools. What would you like to do?");
        }

        void OnClosingCleanup()
        {
            try { _claude.Abort(); } catch { }
            if (_claudeWorkspaceDir != null)
            {
                try { if (Directory.Exists(_claudeWorkspaceDir)) Directory.Delete(_claudeWorkspaceDir, true); } catch { }
                _claudeWorkspaceDir = null;
            }
        }
    }
}
