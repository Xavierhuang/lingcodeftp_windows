using System.Collections.Generic;
using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;

namespace LingCodeFtp
{
    // Settings: left list of servers with +/-, right editable form that
    // live-saves every change, plus Test Connection.
    public class SettingsWindow : Window
    {
        List<ServerAccount> _accounts;
        ServerAccount _editing;
        bool _loading;

        ListBox _list;
        ObservableCollection<string> _items = new ObservableCollection<string>();
        TextBox _fName, _fHost, _fPort, _fUser, _fPass, _fPath, _fKey;
        ComboBox _fProto;
        Button _keyBrowse;
        TextBlock _keyLabel, _testResult;
        Button _testBtn;
        StackPanel _form;
        Grid _keyRow;

        public SettingsWindow()
        {
            Title = "Servers";
            Width = 720; Height = 560;
            MinWidth = 640; MinHeight = 460;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = Brushes.White;

            _accounts = ServerAccount.LoadAll();
            BuildUI();
            RefreshList();
            if (_accounts.Count > 0) _list.SelectedIndex = 0;
            else UpdateFormVisibility();
        }

        void BuildUI()
        {
            DockPanel root = new DockPanel();

            // bottom bar
            Border bar = new Border { Height = 48, Background = new SolidColorBrush(Color.FromRgb(245, 245, 245)) };
            DockPanel.SetDock(bar, Dock.Bottom);
            Button done = new Button { Content = "Done", MinWidth = 80, Margin = new Thickness(0, 0, 16, 0),
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            done.Click += delegate { Persist(); Close(); };
            bar.Child = done;
            root.Children.Add(bar);

            Grid grid = new Grid();
            grid.ColumnDefinitions = new ColumnDefinitions("240,*");

            // left
            DockPanel left = new DockPanel { Background = new SolidColorBrush(Color.FromRgb(248, 248, 248)) };
            StackPanel footer = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6,
                Margin = new Thickness(6), Height = 34 };
            DockPanel.SetDock(footer, Dock.Bottom);
            Button add = new Button { Content = "+", Width = 32 };
            Button remove = new Button { Content = "−", Width = 32 };
            add.Click += delegate { AddAccount(); };
            remove.Click += delegate { RemoveAccount(); };
            footer.Children.Add(add); footer.Children.Add(remove);
            left.Children.Add(footer);
            _list = new ListBox { ItemsSource = _items };
            _list.SelectionChanged += delegate { OnSelectionChanged(); };
            left.Children.Add(_list);
            Grid.SetColumn(left, 0);
            grid.Children.Add(left);

            // right form
            _form = new StackPanel { Margin = new Thickness(20, 16, 20, 16), Spacing = 10 };
            ScrollViewer scroll = new ScrollViewer { Content = _form };
            Grid.SetColumn(scroll, 1);
            grid.Children.Add(scroll);

            BuildForm();
            root.Children.Add(grid);
            Content = root;
        }

        void BuildForm()
        {
            _form.Children.Add(Section("CONNECTION"));

            _fProto = new ComboBox { Width = 140 };
            _fProto.ItemsSource = new[] { "ftp", "ftps", "sftp" };
            _fProto.SelectionChanged += delegate { OnProtocolChanged(); };
            _form.Children.Add(Row("Protocol", _fProto));

            _fName = Field(); _form.Children.Add(Row("Name", _fName));
            _fHost = Field(); _form.Children.Add(Row("Host", _fHost));
            _fPort = Field(); _fPort.Width = 90; _form.Children.Add(Row("Port", _fPort));
            _fPath = Field(); _form.Children.Add(Row("Initial Path", _fPath));

            _form.Children.Add(Section("AUTHENTICATION"));
            _fUser = Field(); _form.Children.Add(Row("Username", _fUser));
            _fPass = Field(); _fPass.PasswordChar = '●'; _form.Children.Add(Row("Password", _fPass));

            _fKey = Field();
            _keyBrowse = new Button { Content = "Choose…" };
            _keyBrowse.Click += delegate { ChooseKey(); };
            StackPanel keyControls = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            _fKey.Width = 230;
            keyControls.Children.Add(_fKey); keyControls.Children.Add(_keyBrowse);
            _keyRow = Row("SSH Key", keyControls, out _keyLabel);
            _form.Children.Add(_keyRow);

            _testBtn = new Button { Content = "Test Connection", Margin = new Thickness(108, 8, 0, 0) };
            _testBtn.Click += delegate { TestConnection(); };
            _form.Children.Add(_testBtn);
            _testResult = new TextBlock { Margin = new Thickness(108, 0, 0, 0), TextWrapping = TextWrapping.Wrap };
            _form.Children.Add(_testResult);

            _fName.TextChanged += delegate { OnFieldChanged(); };
            _fHost.TextChanged += delegate { OnFieldChanged(); };
            _fPort.TextChanged += delegate { OnFieldChanged(); };
            _fUser.TextChanged += delegate { OnFieldChanged(); };
            _fPass.TextChanged += delegate { OnFieldChanged(); };
            _fPath.TextChanged += delegate { OnFieldChanged(); };
            _fKey.TextChanged += delegate { OnFieldChanged(); };
        }

        static TextBox Field() { return new TextBox(); }

        static Control Section(string title)
        {
            return new TextBlock { Text = title, FontSize = 11, FontWeight = FontWeight.Bold,
                Foreground = Brushes.Gray, Margin = new Thickness(0, 8, 0, 0) };
        }

        Grid Row(string label, Control control)
        {
            TextBlock tb;
            return Row(label, control, out tb);
        }

        Grid Row(string label, Control control, out TextBlock labelBlock)
        {
            Grid g = new Grid();
            g.ColumnDefinitions = new ColumnDefinitions("100,*");
            labelBlock = new TextBlock { Text = label, HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0),
                Foreground = new SolidColorBrush(Color.FromRgb(90, 90, 90)) };
            Grid.SetColumn(labelBlock, 0);
            Grid.SetColumn(control, 1);
            if (control is Control c) c.HorizontalAlignment = HorizontalAlignment.Left;
            if (control is TextBox) ((TextBox)control).HorizontalAlignment = HorizontalAlignment.Stretch;
            g.Children.Add(labelBlock); g.Children.Add(control);
            return g;
        }

        void RefreshList()
        {
            int sel = _list.SelectedIndex;
            _items.Clear();
            foreach (ServerAccount a in _accounts)
            {
                string name = string.IsNullOrEmpty(a.Name) ? "Untitled Server" : a.Name;
                string host = string.IsNullOrEmpty(a.Host) ? "no host" : a.Host;
                _items.Add(name + "   (" + (a.Protocol ?? "ftp") + "://" + host + ")");
            }
            if (sel >= 0 && sel < _items.Count) _list.SelectedIndex = sel;
        }

        void OnSelectionChanged()
        {
            int i = _list.SelectedIndex;
            _editing = (i >= 0 && i < _accounts.Count) ? _accounts[i] : null;
            PopulateForm();
        }

        public void SelectAccount(string accountID)
        {
            for (int i = 0; i < _accounts.Count; i++)
                if (_accounts[i].AccountID == accountID) { _list.SelectedIndex = i; return; }
        }

        void UpdateFormVisibility() { _form.IsVisible = _editing != null; }

        void PopulateForm()
        {
            UpdateFormVisibility();
            if (_editing == null) return;
            _loading = true;
            _fName.Text = _editing.Name ?? "";
            _fHost.Text = _editing.Host ?? "";
            _fPort.Text = _editing.Port > 0 ? _editing.Port.ToString() : "";
            _fUser.Text = _editing.Username ?? "";
            _fPass.Text = _editing.Password ?? "";
            _fPath.Text = _editing.RemotePath ?? "/";
            _fKey.Text = _editing.IdentityFile ?? "";
            _fProto.SelectedItem = _editing.Protocol ?? "ftp";
            if (_fProto.SelectedIndex < 0) _fProto.SelectedIndex = 0;
            _testResult.Text = "";
            _loading = false;
            ApplyProtocolUI();
        }

        void ApplyProtocolUI()
        {
            bool isSftp = _editing != null && (_editing.Protocol ?? "ftp") == "sftp";
            _keyRow.IsVisible = isSftp;
        }

        void OnFieldChanged()
        {
            if (_loading || _editing == null) return;
            _editing.Name = _fName.Text;
            _editing.Host = _fHost.Text;
            int port; _editing.Port = int.TryParse(_fPort.Text, out port) && port > 0 ? port : 0;
            _editing.Username = _fUser.Text;
            _editing.Password = _fPass.Text;
            _editing.RemotePath = string.IsNullOrEmpty(_fPath.Text) ? "/" : _fPath.Text;
            _editing.IdentityFile = _fKey.Text;
            Persist();
            RefreshList();
        }

        void OnProtocolChanged()
        {
            if (_loading || _editing == null) return;
            string proto = (string)_fProto.SelectedItem;
            _editing.Protocol = proto;
            int cur; int.TryParse(_fPort.Text, out cur);
            if (_fPort.Text.Length == 0 || cur == 21 || cur == 22 || cur == 990)
            {
                _editing.Port = proto == "sftp" ? 22 : (proto == "ftps" ? 990 : 21);
                _loading = true; _fPort.Text = _editing.Port.ToString(); _loading = false;
            }
            ApplyProtocolUI();
            Persist();
            RefreshList();
        }

        void Persist() { ServerAccount.SaveAll(_accounts); }

        async void ChooseKey()
        {
            var top = GetTopLevel(this);
            if (top == null) return;
            var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Choose SSH private key", AllowMultiple = false
            });
            if (files != null && files.Count > 0)
            {
                _fKey.Text = files[0].Path.LocalPath;
            }
        }

        void AddAccount()
        {
            ServerAccount a = new ServerAccount();
            _accounts.Add(a);
            Persist();
            RefreshList();
            _list.SelectedIndex = _accounts.Count - 1;
        }

        async void RemoveAccount()
        {
            int i = _list.SelectedIndex;
            if (i < 0 || i >= _accounts.Count) return;
            ServerAccount a = _accounts[i];
            bool ok = await Dialogs.Confirm(this, "Delete Server", "Delete \"" + (a.Name ?? "this server") + "\"?");
            if (!ok) return;
            _accounts.RemoveAt(i);
            Persist();
            RefreshList();
            if (_accounts.Count > 0) _list.SelectedIndex = System.Math.Min(i, _accounts.Count - 1);
            else { _editing = null; PopulateForm(); }
        }

        void TestConnection()
        {
            if (_editing == null) return;
            if (string.IsNullOrEmpty(_editing.Host))
            {
                _testResult.Foreground = Brushes.Firebrick; _testResult.Text = "Enter a host first."; return;
            }
            _testBtn.IsEnabled = false;
            _testResult.Foreground = Brushes.Gray; _testResult.Text = "Connecting…";
            ServerAccount target = _editing;
            FtpClient client = new FtpClient(target);
            string path = string.IsNullOrEmpty(target.RemotePath) ? "/" : target.RemotePath;
            client.ListDirectory(path, delegate(List<RemoteFileNode> nodes, string error)
            {
                if (_editing != target) return;
                _testBtn.IsEnabled = true;
                if (error != null) { _testResult.Foreground = Brushes.Firebrick; _testResult.Text = "✗ " + error; }
                else { _testResult.Foreground = Brushes.SeaGreen; _testResult.Text = "✓ Connected — " + (nodes != null ? nodes.Count : 0) + " items"; }
            });
        }
    }
}
