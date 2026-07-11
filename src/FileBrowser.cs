using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace LingCodeFTP
{
    // Port of FTPFileBrowser: a lazy-loading tree of the remote filesystem.
    // Expanding a folder lists it in place; double-clicking a folder navigates
    // into it (making it the new root, driving the breadcrumb). Right-click
    // gives New Folder / Rename / Delete / Download / Refresh. Files can be
    // uploaded by dragging them in from Explorer.
    public class FileBrowser : UserControl
    {
        readonly FtpClient _client;
        string _rootPath;
        TreeView _tree;
        ImageList _icons;

        public event Action<RemoteFileNode> FileSelected;
        public event Action<string> Navigated;
        public event Action<int, string> RootLoaded;
        public event Action<RemoteFileNode> NodeDeleted;
        public event Action<RemoteFileNode, string> NodeRenamed;
        public event Action<string, bool> Status;

        public string CurrentPath { get { return _rootPath; } }

        public FileBrowser(FtpClient client, string rootPath)
        {
            _client = client;
            _rootPath = string.IsNullOrEmpty(rootPath) ? "/" : rootPath;

            _icons = new ImageList();
            _icons.ImageSize = new Size(16, 16);
            _icons.Images.Add("folder", FolderIcon());
            _icons.Images.Add("file", FileIcon());

            _tree = new TreeView();
            _tree.Dock = DockStyle.Fill;
            _tree.HideSelection = false;
            _tree.ImageList = _icons;
            _tree.ItemHeight = 20;
            _tree.ShowRootLines = true;
            _tree.PathSeparator = "/";
            _tree.BeforeExpand += OnBeforeExpand;
            _tree.AfterSelect += OnAfterSelect;
            _tree.NodeMouseDoubleClick += OnDoubleClick;
            _tree.AllowDrop = true;
            _tree.DragEnter += OnDragEnter;
            _tree.DragDrop += OnDragDrop;
            Controls.Add(_tree);

            BuildContextMenu();
        }

        void BuildContextMenu()
        {
            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Items.Add("New Folder…", null, delegate { MenuNewFolder(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Rename…", null, delegate { MenuRename(); });
            menu.Items.Add("Delete", null, delegate { MenuDelete(); });
            menu.Items.Add("Download…", null, delegate { MenuDownload(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Refresh", null, delegate { MenuRefresh(); });
            _tree.ContextMenuStrip = menu;

            // Select the node under the cursor on right-click so menu acts on it.
            _tree.MouseDown += delegate(object s, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Right)
                {
                    TreeNode n = _tree.GetNodeAt(e.X, e.Y);
                    if (n != null) _tree.SelectedNode = n;
                }
            };
        }

        RemoteFileNode NodeOf(TreeNode t) { return t == null ? null : t.Tag as RemoteFileNode; }
        RemoteFileNode Clicked() { return NodeOf(_tree.SelectedNode); }

        // ---- public ----
        public void ReloadRoot()
        {
            _tree.Nodes.Clear();
            TreeNode loading = new TreeNode("Loading…");
            loading.ForeColor = Color.Gray;
            _tree.Nodes.Add(loading);

            _client.ListDirectory(_rootPath, delegate(List<RemoteFileNode> nodes, string error)
            {
                _tree.Nodes.Clear();
                if (error != null || nodes == null)
                {
                    TreeNode err = new TreeNode("Error: " + (error ?? "listing failed"));
                    err.ForeColor = Color.Firebrick;
                    _tree.Nodes.Add(err);
                }
                else
                {
                    foreach (RemoteFileNode node in nodes)
                        _tree.Nodes.Add(MakeTreeNode(node));
                }
                if (RootLoaded != null) RootLoaded(nodes != null ? nodes.Count : 0, error);
            });
        }

        public void NavigateToPath(string path)
        {
            if (string.IsNullOrEmpty(path)) path = "/";
            _rootPath = path;
            _tree.Nodes.Clear();
            if (Navigated != null) Navigated(_rootPath);
            ReloadRoot();
        }

        TreeNode MakeTreeNode(RemoteFileNode node)
        {
            TreeNode t = new TreeNode(node.Name);
            t.Tag = node;
            t.ImageKey = node.IsDirectory ? "folder" : "file";
            t.SelectedImageKey = t.ImageKey;
            if (node.IsDirectory)
                t.Nodes.Add(new TreeNode("Loading…"));   // dummy for the expander
            return t;
        }

        // ---- lazy expand ----
        void OnBeforeExpand(object sender, TreeViewCancelEventArgs e)
        {
            RemoteFileNode node = NodeOf(e.Node);
            if (node == null || !node.IsDirectory) return;
            if (node.ChildrenLoaded) return;
            if (node.IsLoading) return;
            node.IsLoading = true;

            _client.ListDirectory(node.FullPath, delegate(List<RemoteFileNode> nodes, string error)
            {
                node.IsLoading = false;
                node.ChildrenLoaded = true;
                e.Node.Nodes.Clear();
                if (nodes != null)
                    foreach (RemoteFileNode child in nodes)
                        e.Node.Nodes.Add(MakeTreeNode(child));
                else if (error != null)
                {
                    TreeNode err = new TreeNode("Error: " + error);
                    err.ForeColor = Color.Firebrick;
                    e.Node.Nodes.Add(err);
                }
            });
        }

        void OnAfterSelect(object sender, TreeViewEventArgs e)
        {
            RemoteFileNode node = NodeOf(e.Node);
            if (node == null || node.IsDirectory) return;
            if (FileSelected != null) FileSelected(node);
        }

        void OnDoubleClick(object sender, TreeNodeMouseClickEventArgs e)
        {
            RemoteFileNode node = NodeOf(e.Node);
            if (node != null && node.IsDirectory)
                NavigateToPath(node.FullPath);
        }

        // ---- drag in from Explorer (upload) ----
        void OnDragEnter(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
                e.Effect = DragDropEffects.Copy;
        }

        void OnDragDrop(object sender, DragEventArgs e)
        {
            string[] files = (string[])e.Data.GetData(DataFormats.FileDrop);
            if (files == null || files.Length == 0) return;

            Point p = _tree.PointToClient(new Point(e.X, e.Y));
            RemoteFileNode target = NodeOf(_tree.GetNodeAt(p));
            string targetPath = (target != null && target.IsDirectory) ? target.FullPath : _rootPath;
            if (!targetPath.EndsWith("/")) targetPath += "/";

            NotifyStatus("Uploading…", false);
            string tp = targetPath;
            Thread t = new Thread(delegate()
            {
                foreach (string f in files)
                {
                    try
                    {
                        if (Directory.Exists(f)) UploadDir(f, tp + Path.GetFileName(f));
                        else UploadOne(f, tp + Path.GetFileName(f));
                    }
                    catch { }
                }
                BeginInvoke((Action)delegate()
                {
                    NotifyStatus("Upload complete", false);
                    ReloadRoot();
                });
            });
            t.IsBackground = true;
            t.Start();
        }

        void UploadOne(string localFile, string remotePath)
        {
            byte[] data = File.ReadAllBytes(localFile);
            _client.UploadSync(data, remotePath);
        }

        void UploadDir(string localDir, string remoteDir)
        {
            _client.CreateDirectorySync(remoteDir);
            foreach (string f in Directory.GetFiles(localDir))
                UploadOne(f, remoteDir + "/" + Path.GetFileName(f));
            foreach (string d in Directory.GetDirectories(localDir))
                UploadDir(d, remoteDir + "/" + Path.GetFileName(d));
        }

        // ---- context menu actions ----
        void MenuNewFolder()
        {
            RemoteFileNode node = Clicked();
            string parent = (node != null && node.IsDirectory) ? node.FullPath : _rootPath;
            string name = Prompt.Show(this, "New Folder", "Folder name:", "");
            if (string.IsNullOrEmpty(name)) return;
            string newPath = parent.EndsWith("/") ? parent + name : parent + "/" + name;

            NotifyStatus("Creating folder…", false);
            _client.CreateDirectory(newPath, delegate(string err)
            {
                if (err != null) NotifyStatus("Error: " + err, true);
                else { NotifyStatus("Created: " + name, false); ReloadRoot(); }
            });
        }

        void MenuRename()
        {
            RemoteFileNode node = Clicked();
            if (node == null) return;
            string newName = Prompt.Show(this, "Rename", "Rename \"" + node.Name + "\" to:", node.Name);
            if (string.IsNullOrEmpty(newName) || newName == node.Name) return;

            string stripped = node.FullPath.EndsWith("/")
                ? node.FullPath.Substring(0, node.FullPath.Length - 1) : node.FullPath;
            int slash = stripped.LastIndexOf('/');
            string parent = slash >= 0 ? stripped.Substring(0, slash) : "";
            string newPath = parent + "/" + newName;
            if (node.IsDirectory) newPath += "/";

            NotifyStatus("Renaming…", false);
            _client.RenameFile(stripped, newPath, delegate(string err)
            {
                if (err != null) { NotifyStatus("Error: " + err, true); return; }
                string oldPath = node.FullPath;
                node.Name = newName;
                node.FullPath = newPath;
                if (NodeRenamed != null) NodeRenamed(node, oldPath);
                NotifyStatus("Renamed to: " + newName, false);
                ReloadRoot();
            });
        }

        void MenuDelete()
        {
            RemoteFileNode node = Clicked();
            if (node == null) return;
            if (MessageBox.Show("Delete \"" + node.Name + "\"?\n\nThis cannot be undone.",
                "Delete", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;

            NotifyStatus("Deleting…", false);
            _client.DeleteFile(node.FullPath, delegate(string err)
            {
                if (err != null) NotifyStatus("Error: " + err, true);
                else
                {
                    if (NodeDeleted != null) NodeDeleted(node);
                    NotifyStatus("Deleted: " + node.Name, false);
                    ReloadRoot();
                }
            });
        }

        void MenuDownload()
        {
            RemoteFileNode node = Clicked();
            if (node == null || node.IsDirectory) return;
            SaveFileDialog dlg = new SaveFileDialog();
            dlg.FileName = node.Name;
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            string dest = dlg.FileName;

            NotifyStatus("Downloading " + node.Name + "…", false);
            _client.DownloadFile(node.FullPath, delegate(byte[] data, string err)
            {
                if (err != null || data == null) { NotifyStatus("Error: " + err, true); return; }
                try { File.WriteAllBytes(dest, data); NotifyStatus("Saved: " + dest, false); }
                catch (Exception ex) { NotifyStatus("Error: " + ex.Message, true); }
            });
        }

        void MenuRefresh()
        {
            NotifyStatus("Refreshing…", false);
            ReloadRoot();
        }

        void NotifyStatus(string msg, bool isError)
        {
            if (Status != null) Status(msg, isError);
        }

        // ---- simple drawn icons ----
        static Bitmap FolderIcon()
        {
            Bitmap b = new Bitmap(16, 16);
            using (Graphics g = Graphics.FromImage(b))
            {
                g.Clear(Color.Transparent);
                using (Brush br = new SolidBrush(Color.FromArgb(240, 200, 100)))
                    g.FillRectangle(br, 1, 4, 13, 9);
                using (Pen pen = new Pen(Color.FromArgb(180, 140, 60)))
                    g.DrawRectangle(pen, 1, 4, 13, 9);
                using (Brush tab = new SolidBrush(Color.FromArgb(240, 200, 100)))
                    g.FillRectangle(tab, 1, 2, 6, 3);
            }
            return b;
        }

        static Bitmap FileIcon()
        {
            Bitmap b = new Bitmap(16, 16);
            using (Graphics g = Graphics.FromImage(b))
            {
                g.Clear(Color.Transparent);
                using (Brush br = new SolidBrush(Color.White))
                    g.FillRectangle(br, 3, 1, 9, 13);
                using (Pen pen = new Pen(Color.FromArgb(150, 150, 150)))
                    g.DrawRectangle(pen, 3, 1, 9, 13);
            }
            return b;
        }
    }

    // Minimal input dialog (replaces the Mac NSAlert-with-accessory prompts).
    static class Prompt
    {
        public static string Show(IWin32Window owner, string title, string text, string def)
        {
            Form f = new Form();
            f.Text = title;
            f.FormBorderStyle = FormBorderStyle.FixedDialog;
            f.StartPosition = FormStartPosition.CenterParent;
            f.MinimizeBox = false; f.MaximizeBox = false;
            f.ClientSize = new Size(360, 110);
            f.Font = new Font("Segoe UI", 9f);

            Label l = new Label();
            l.Text = text; l.AutoSize = true; l.Location = new Point(12, 12);
            f.Controls.Add(l);

            TextBox tb = new TextBox();
            tb.Text = def; tb.Location = new Point(12, 36); tb.Width = 336;
            tb.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            f.Controls.Add(tb);

            Button ok = new Button();
            ok.Text = "OK"; ok.DialogResult = DialogResult.OK;
            ok.Location = new Point(192, 72); ok.Size = new Size(75, 26);
            f.Controls.Add(ok);

            Button cancel = new Button();
            cancel.Text = "Cancel"; cancel.DialogResult = DialogResult.Cancel;
            cancel.Location = new Point(273, 72); cancel.Size = new Size(75, 26);
            f.Controls.Add(cancel);

            f.AcceptButton = ok; f.CancelButton = cancel;
            tb.SelectAll(); tb.Focus();
            return f.ShowDialog(owner) == DialogResult.OK ? tb.Text : null;
        }
    }
}
