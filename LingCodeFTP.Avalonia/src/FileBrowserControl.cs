using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace LingCodeFtp
{
    // Port of FTPFileBrowser: a lazy-loading remote tree. Expand lists in place;
    // double-click a folder navigates into it. Right-click for New Folder /
    // Rename / Delete / Upload / Download / Refresh.
    public class FileBrowserControl : UserControl
    {
        readonly FtpClient _client;
        string _rootPath;
        TreeView _tree;

        public event Action<RemoteFileNode> FileSelected;
        public event Action<string> Navigated;
        public event Action<int, string> RootLoaded;
        public event Action<RemoteFileNode> NodeDeleted;
        public event Action<RemoteFileNode, string> NodeRenamed;
        public event Action<string, bool> StatusUpdate;

        public string CurrentPath { get { return _rootPath; } }

        public FileBrowserControl(FtpClient client, string rootPath)
        {
            _client = client;
            _rootPath = string.IsNullOrEmpty(rootPath) ? "/" : rootPath;

            _tree = new TreeView { Background = new SolidColorBrush(Color.FromRgb(248, 248, 248)) };
            _tree.AddHandler(TreeViewItem.ExpandedEvent, OnItemExpanded);
            _tree.SelectionChanged += OnSelectionChanged;
            _tree.DoubleTapped += OnDoubleTapped;

            ContextMenu menu = new ContextMenu();
            menu.Items.Add(Item("New Folder…", delegate { MenuNewFolder(); }));
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Rename…", delegate { MenuRename(); }));
            menu.Items.Add(Item("Delete", delegate { MenuDelete(); }));
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Upload File…", delegate { MenuUpload(); }));
            menu.Items.Add(Item("Download…", delegate { MenuDownload(); }));
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Refresh", delegate { MenuRefresh(); }));
            _tree.ContextMenu = menu;

            Content = _tree;
        }

        static MenuItem Item(string header, Action onClick)
        {
            MenuItem m = new MenuItem { Header = header };
            m.Click += delegate { onClick(); };
            return m;
        }

        Window Owner() { return TopLevel.GetTopLevel(this) as Window; }
        RemoteFileNode NodeOf(object o) { TreeViewItem t = o as TreeViewItem; return t != null ? t.Tag as RemoteFileNode : null; }
        RemoteFileNode Selected() { return NodeOf(_tree.SelectedItem); }

        public void ReloadRoot()
        {
            _tree.Items.Clear();
            TreeViewItem loading = new TreeViewItem { Header = "Loading…", Foreground = Brushes.Gray };
            _tree.Items.Add(loading);

            _client.ListDirectory(_rootPath, delegate(List<RemoteFileNode> nodes, string error)
            {
                _tree.Items.Clear();
                if (error != null || nodes == null)
                {
                    _tree.Items.Add(new TreeViewItem { Header = "Error: " + (error ?? "listing failed"), Foreground = Brushes.Firebrick });
                }
                else
                {
                    foreach (RemoteFileNode node in nodes) _tree.Items.Add(MakeItem(node));
                }
                if (RootLoaded != null) RootLoaded(nodes != null ? nodes.Count : 0, error);
            });
        }

        public void NavigateToPath(string path)
        {
            if (string.IsNullOrEmpty(path)) path = "/";
            _rootPath = path;
            _tree.Items.Clear();
            if (Navigated != null) Navigated(_rootPath);
            ReloadRoot();
        }

        TreeViewItem MakeItem(RemoteFileNode node)
        {
            TreeViewItem t = new TreeViewItem { Tag = node, Header = HeaderFor(node) };
            if (node.IsDirectory) t.Items.Add(new TreeViewItem { Header = "Loading…", Foreground = Brushes.Gray });
            return t;
        }

        static Control HeaderFor(RemoteFileNode node)
        {
            StackPanel sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            Border icon = new Border { Width = 13, Height = 11, CornerRadius = new CornerRadius(2),
                VerticalAlignment = VerticalAlignment.Center };
            if (node.IsDirectory) { icon.Background = new SolidColorBrush(Color.FromRgb(240, 200, 100)); }
            else { icon.Background = Brushes.White; icon.BorderBrush = new SolidColorBrush(Color.FromRgb(150, 150, 150)); icon.BorderThickness = new Thickness(1); }
            sp.Children.Add(icon);
            sp.Children.Add(new TextBlock { Text = node.Name, VerticalAlignment = VerticalAlignment.Center });
            return sp;
        }

        void OnItemExpanded(object sender, RoutedEventArgs e)
        {
            TreeViewItem item = e.Source as TreeViewItem;
            RemoteFileNode node = NodeOf(item);
            if (node == null || !node.IsDirectory || node.ChildrenLoaded || node.IsLoading) return;
            node.IsLoading = true;
            _client.ListDirectory(node.FullPath, delegate(List<RemoteFileNode> nodes, string error)
            {
                node.IsLoading = false;
                node.ChildrenLoaded = true;
                item.Items.Clear();
                if (nodes != null) foreach (RemoteFileNode child in nodes) item.Items.Add(MakeItem(child));
                else if (error != null) item.Items.Add(new TreeViewItem { Header = "Error: " + error, Foreground = Brushes.Firebrick });
            });
        }

        void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            RemoteFileNode node = Selected();
            if (node == null || node.IsDirectory) return;
            if (FileSelected != null) FileSelected(node);
        }

        void OnDoubleTapped(object sender, RoutedEventArgs e)
        {
            RemoteFileNode node = Selected();
            if (node != null && node.IsDirectory) NavigateToPath(node.FullPath);
        }

        async void MenuNewFolder()
        {
            RemoteFileNode node = Selected();
            string parent = (node != null && node.IsDirectory) ? node.FullPath : _rootPath;
            string name = await Dialogs.Prompt(Owner(), "New Folder", "Folder name:", "");
            if (string.IsNullOrEmpty(name)) return;
            string newPath = parent.EndsWith("/") ? parent + name : parent + "/" + name;
            Status("Creating folder…", false);
            _client.CreateDirectory(newPath, delegate(string err)
            {
                if (err != null) Status("Error: " + err, true);
                else { Status("Created: " + name, false); ReloadRoot(); }
            });
        }

        async void MenuRename()
        {
            RemoteFileNode node = Selected();
            if (node == null) return;
            string newName = await Dialogs.Prompt(Owner(), "Rename", "Rename \"" + node.Name + "\" to:", node.Name);
            if (string.IsNullOrEmpty(newName) || newName == node.Name) return;
            string stripped = node.FullPath.EndsWith("/") ? node.FullPath.Substring(0, node.FullPath.Length - 1) : node.FullPath;
            int slash = stripped.LastIndexOf('/');
            string parent = slash >= 0 ? stripped.Substring(0, slash) : "";
            string newPath = parent + "/" + newName;
            if (node.IsDirectory) newPath += "/";
            Status("Renaming…", false);
            _client.RenameFile(stripped, newPath, delegate(string err)
            {
                if (err != null) { Status("Error: " + err, true); return; }
                string oldPath = node.FullPath;
                node.Name = newName; node.FullPath = newPath;
                if (NodeRenamed != null) NodeRenamed(node, oldPath);
                Status("Renamed to: " + newName, false);
                ReloadRoot();
            });
        }

        async void MenuDelete()
        {
            RemoteFileNode node = Selected();
            if (node == null) return;
            bool ok = await Dialogs.Confirm(Owner(), "Delete", "Delete \"" + node.Name + "\"?\nThis cannot be undone.");
            if (!ok) return;
            Status("Deleting…", false);
            _client.DeleteFile(node.FullPath, delegate(string err)
            {
                if (err != null) Status("Error: " + err, true);
                else { if (NodeDeleted != null) NodeDeleted(node); Status("Deleted: " + node.Name, false); ReloadRoot(); }
            });
        }

        async void MenuUpload()
        {
            var top = TopLevel.GetTopLevel(this);
            if (top == null) return;
            RemoteFileNode node = Selected();
            string targetPath = (node != null && node.IsDirectory) ? node.FullPath : _rootPath;
            if (!targetPath.EndsWith("/")) targetPath += "/";
            var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Upload file", AllowMultiple = true });
            if (files == null || files.Count == 0) return;
            Status("Uploading…", false);
            string tp = targetPath;
            Thread t = new Thread(delegate()
            {
                foreach (var f in files)
                {
                    try { byte[] data = File.ReadAllBytes(f.Path.LocalPath); _client.UploadSync(data, tp + Path.GetFileName(f.Path.LocalPath)); }
                    catch { }
                }
                Dispatcher.UIThread.Post(delegate { Status("Upload complete", false); ReloadRoot(); });
            });
            t.IsBackground = true; t.Start();
        }

        async void MenuDownload()
        {
            RemoteFileNode node = Selected();
            if (node == null || node.IsDirectory) return;
            var top = TopLevel.GetTopLevel(this);
            if (top == null) return;
            var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions { Title = "Save file", SuggestedFileName = node.Name });
            if (file == null) return;
            string dest = file.Path.LocalPath;
            Status("Downloading " + node.Name + "…", false);
            _client.DownloadFile(node.FullPath, delegate(byte[] data, string err)
            {
                if (err != null || data == null) { Status("Error: " + err, true); return; }
                try { File.WriteAllBytes(dest, data); Status("Saved: " + dest, false); }
                catch (Exception ex) { Status("Error: " + ex.Message, true); }
            });
        }

        void MenuRefresh() { Status("Refreshing…", false); ReloadRoot(); }

        void Status(string msg, bool isError) { if (StatusUpdate != null) StatusUpdate(msg, isError); }
    }
}
