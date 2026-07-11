using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace LingCodeFTP
{
    // Port of SettingsWindowController: left = list of servers with +/-, right =
    // an editable form that live-saves every change. Includes Test Connection.
    public class SettingsForm : Form
    {
        List<ServerAccount> _accounts;
        ServerAccount _editing;
        bool _loading;

        ListBox _list;
        TextBox _fName, _fHost, _fPort, _fUser, _fPath, _fKey;
        TextBox _fPass;
        ComboBox _fProto;
        Button _keyBrowse, _testBtn;
        Label _keyLabel, _testResult;
        Panel _form;

        public SettingsForm()
        {
            Text = "Servers";
            Width = 720;
            Height = 560;
            MinimumSize = new Size(640, 480);
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Segoe UI", 9f);

            _accounts = ServerAccount.LoadAll();
            BuildUI();

            if (_accounts.Count > 0) _list.SelectedIndex = 0;
            else UpdateFormVisibility();
        }

        void BuildUI()
        {
            // ---- bottom bar with Done ----
            Panel bar = new Panel();
            bar.Dock = DockStyle.Bottom;
            bar.Height = 48;
            bar.BackColor = Color.FromArgb(245, 245, 245);
            Controls.Add(bar);

            Button done = new Button();
            done.Text = "Done";
            done.Size = new Size(80, 28);
            done.Anchor = AnchorStyles.Right;
            done.Location = new Point(bar.Width - 100, 10);
            done.Click += delegate { Persist(); Close(); };
            bar.Controls.Add(done);
            bar.Resize += delegate { done.Location = new Point(bar.Width - 100, 10); };

            SplitContainer split = new SplitContainer();
            split.Dock = DockStyle.Fill;
            split.SplitterDistance = 220;
            split.FixedPanel = FixedPanel.Panel1;
            Controls.Add(split);
            split.BringToFront();

            // ---- left: list + add/remove ----
            _list = new ListBox();
            _list.Dock = DockStyle.Fill;
            _list.IntegralHeight = false;
            _list.SelectedIndexChanged += delegate { OnSelectionChanged(); };
            split.Panel1.Controls.Add(_list);

            Panel footer = new Panel();
            footer.Dock = DockStyle.Bottom;
            footer.Height = 32;
            footer.BackColor = Color.FromArgb(238, 238, 238);
            split.Panel1.Controls.Add(footer);

            Button add = new Button();
            add.Text = "+";
            add.Size = new Size(30, 26);
            add.Location = new Point(6, 3);
            add.Click += delegate { AddAccount(); };
            footer.Controls.Add(add);

            Button remove = new Button();
            remove.Text = "−";  // minus
            remove.Size = new Size(30, 26);
            remove.Location = new Point(40, 3);
            remove.Click += delegate { RemoveAccount(); };
            footer.Controls.Add(remove);

            // ---- right: scrollable form ----
            _form = new Panel();
            _form.Dock = DockStyle.Fill;
            _form.AutoScroll = true;
            _form.Padding = new Padding(20, 16, 20, 16);
            split.Panel2.Controls.Add(_form);

            RefreshList();
            BuildForm();
        }

        int _y;
        void BuildForm()
        {
            _y = 12;
            AddSection("CONNECTION");

            _fProto = new ComboBox();
            _fProto.DropDownStyle = ComboBoxStyle.DropDownList;
            _fProto.Items.AddRange(new object[] { "ftp", "ftps", "sftp" });
            _fProto.SelectedIndexChanged += delegate { OnProtocolChanged(); };
            AddRow("Protocol", _fProto, 120);

            _fName = AddField("Name");
            _fHost = AddField("Host");
            _fPort = AddField("Port", 90);
            _fPath = AddField("Initial Path");

            AddSection("AUTHENTICATION");
            _fUser = AddField("Username");
            _fPass = AddField("Password");
            _fPass.UseSystemPasswordChar = true;

            // SSH key row (label + field + browse) — toggled for SFTP.
            _keyLabel = new Label();
            _keyLabel.Text = "SSH Key";
            _keyLabel.TextAlign = ContentAlignment.MiddleRight;
            _keyLabel.Size = new Size(100, 26);
            _keyLabel.Location = new Point(0, _y);
            _form.Controls.Add(_keyLabel);

            _fKey = new TextBox();
            _fKey.Location = new Point(108, _y + 2);
            _fKey.Width = 300;
            _fKey.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            _fKey.TextChanged += delegate { OnFieldChanged(); };
            _form.Controls.Add(_fKey);

            _keyBrowse = new Button();
            _keyBrowse.Text = "Choose…";
            _keyBrowse.Size = new Size(74, 24);
            _keyBrowse.Location = new Point(414, _y + 1);
            _keyBrowse.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _keyBrowse.Click += delegate { ChooseKey(); };
            _form.Controls.Add(_keyBrowse);
            _y += 34;

            // ---- test connection ----
            _y += 8;
            _testBtn = new Button();
            _testBtn.Text = "Test Connection";
            _testBtn.Size = new Size(130, 28);
            _testBtn.Location = new Point(108, _y);
            _testBtn.Click += delegate { TestConnection(); };
            _form.Controls.Add(_testBtn);

            _testResult = new Label();
            _testResult.AutoSize = true;
            _testResult.Location = new Point(248, _y + 6);
            _testResult.MaximumSize = new Size(300, 0);
            _form.Controls.Add(_testResult);
        }

        void AddSection(string title)
        {
            Label l = new Label();
            l.Text = title;
            l.Font = new Font("Segoe UI", 8f, FontStyle.Bold);
            l.ForeColor = Color.Gray;
            l.AutoSize = true;
            l.Location = new Point(0, _y);
            _form.Controls.Add(l);
            _y += 24;
        }

        TextBox AddField(string label) { return AddField(label, 0); }

        TextBox AddField(string label, int fixedWidth)
        {
            TextBox tb = new TextBox();
            AddRow(label, tb, fixedWidth);
            tb.TextChanged += delegate { OnFieldChanged(); };
            return tb;
        }

        void AddRow(string label, Control control, int fixedWidth)
        {
            Label l = new Label();
            l.Text = label;
            l.TextAlign = ContentAlignment.MiddleRight;
            l.Size = new Size(100, 26);
            l.Location = new Point(0, _y);
            _form.Controls.Add(l);

            control.Location = new Point(108, _y + 2);
            if (fixedWidth > 0)
            {
                control.Width = fixedWidth;
            }
            else
            {
                control.Width = 380;
                control.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            }
            _form.Controls.Add(control);
            _y += 34;
        }

        // ---- list ----
        void RefreshList()
        {
            _list.BeginUpdate();
            int sel = _list.SelectedIndex;
            _list.Items.Clear();
            foreach (ServerAccount a in _accounts)
            {
                string name = string.IsNullOrEmpty(a.Name) ? "Untitled Server" : a.Name;
                string host = string.IsNullOrEmpty(a.Host) ? "no host" : a.Host;
                _list.Items.Add(name + "   (" + (a.Protocol ?? "ftp") + "://" + host + ")");
            }
            if (sel >= 0 && sel < _list.Items.Count) _list.SelectedIndex = sel;
            _list.EndUpdate();
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

        void UpdateFormVisibility()
        {
            _form.Visible = (_editing != null);
        }

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
            _fProto.SelectedItem = (_editing.Protocol ?? "ftp");
            if (_fProto.SelectedIndex < 0) _fProto.SelectedIndex = 0;
            _testResult.Text = "";
            _loading = false;
            ApplyProtocolUI();
        }

        void ApplyProtocolUI()
        {
            bool isSftp = (_editing != null && (_editing.Protocol ?? "ftp") == "sftp");
            _keyLabel.Visible = isSftp;
            _fKey.Visible = isSftp;
            _keyBrowse.Visible = isSftp;
        }

        // ---- live edit ----
        void OnFieldChanged()
        {
            if (_loading || _editing == null) return;
            _editing.Name = _fName.Text;
            _editing.Host = _fHost.Text;
            int port; _editing.Port = int.TryParse(_fPort.Text, out port) && port > 0 ? port : 0;
            _editing.Username = _fUser.Text;
            _editing.Password = _fPass.Text;
            _editing.RemotePath = _fPath.Text.Length > 0 ? _fPath.Text : "/";
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
                if (proto == "sftp") _editing.Port = 22;
                else if (proto == "ftps") _editing.Port = 990;
                else _editing.Port = 21;
                _loading = true;
                _fPort.Text = _editing.Port.ToString();
                _loading = false;
            }
            ApplyProtocolUI();
            Persist();
            RefreshList();
        }

        void Persist()
        {
            ServerAccount.SaveAll(_accounts);
        }

        void ChooseKey()
        {
            OpenFileDialog dlg = new OpenFileDialog();
            dlg.Title = "Choose SSH private key";
            string ssh = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");
            if (System.IO.Directory.Exists(ssh)) dlg.InitialDirectory = ssh;
            if (dlg.ShowDialog(this) == DialogResult.OK)
            {
                _fKey.Text = dlg.FileName;   // triggers OnFieldChanged
            }
        }

        // ---- add / remove ----
        void AddAccount()
        {
            ServerAccount a = new ServerAccount();
            _accounts.Add(a);
            Persist();
            RefreshList();
            _list.SelectedIndex = _accounts.Count - 1;
            _fName.Focus();
            _fName.SelectAll();
        }

        void RemoveAccount()
        {
            int i = _list.SelectedIndex;
            if (i < 0 || i >= _accounts.Count) return;
            ServerAccount a = _accounts[i];
            if (MessageBox.Show("Delete \"" + (a.Name ?? "this server") + "\"?", "Delete Server",
                MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;
            _accounts.RemoveAt(i);
            Persist();
            RefreshList();
            if (_accounts.Count > 0) _list.SelectedIndex = Math.Min(i, _accounts.Count - 1);
            else { _editing = null; PopulateForm(); }
        }

        // ---- test connection ----
        void TestConnection()
        {
            if (_editing == null) return;
            if (string.IsNullOrEmpty(_editing.Host))
            {
                _testResult.ForeColor = Color.Firebrick;
                _testResult.Text = "Enter a host first.";
                return;
            }
            _testBtn.Enabled = false;
            _testResult.ForeColor = Color.Gray;
            _testResult.Text = "Connecting…";

            ServerAccount target = _editing;
            FtpClient client = new FtpClient(target, this);
            string path = string.IsNullOrEmpty(target.RemotePath) ? "/" : target.RemotePath;
            client.ListDirectory(path, delegate(List<RemoteFileNode> nodes, string error)
            {
                if (_editing != target) return;   // selection changed; drop stale result
                _testBtn.Enabled = true;
                if (error != null)
                {
                    _testResult.ForeColor = Color.Firebrick;
                    _testResult.Text = "✗ " + error;
                }
                else
                {
                    _testResult.ForeColor = Color.SeaGreen;
                    _testResult.Text = "✓ Connected — " + (nodes != null ? nodes.Count : 0) + " items";
                }
            });
        }
    }
}
