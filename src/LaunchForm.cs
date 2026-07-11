using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace LingCodeFTP
{
    // Port of LaunchWindowController: a grid of rounded "server cards" plus an
    // Add card and a Settings button. Clicking a card opens that server;
    // right-clicking offers Edit / Delete. Cards reload on activation.
    public class LaunchForm : Form
    {
        FlowLayoutPanel _grid;
        SettingsForm _settings;

        static readonly Color GridBg = Color.FromArgb(240, 240, 240);
        static readonly Color HeaderBg = Color.FromArgb(246, 246, 246);
        static readonly Color Hairline = Color.FromArgb(212, 212, 212);

        public LaunchForm()
        {
            Text = "LingCode FTP";
            Width = 840;
            Height = 560;
            MinimumSize = new Size(440, 340);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = GridBg;
            Font = new Font("Segoe UI", 9f);

            Panel header = new Panel();
            header.Dock = DockStyle.Top;
            header.Height = 56;
            header.BackColor = HeaderBg;
            header.Paint += delegate(object s, PaintEventArgs e)
            {
                using (Pen p = new Pen(Hairline))
                    e.Graphics.DrawLine(p, 0, header.Height - 1, header.Width, header.Height - 1);
            };
            Controls.Add(header);

            Label title = new Label();
            title.Text = "LingCode FTP";
            title.Font = new Font("Segoe UI", 15f, FontStyle.Bold);
            title.AutoSize = true;
            title.BackColor = Color.Transparent;
            title.Location = new Point(18, 15);
            header.Controls.Add(title);

            Button settings = new Button();
            settings.Text = "⚙  Settings";
            settings.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            settings.Size = new Size(110, 30);
            settings.Location = new Point(header.Width - 126, 13);
            settings.FlatStyle = FlatStyle.System;
            settings.Click += delegate { ShowSettings(); };
            header.Controls.Add(settings);
            header.Resize += delegate { settings.Location = new Point(header.Width - 126, 13); };

            _grid = new FlowLayoutPanel();
            _grid.Dock = DockStyle.Fill;
            _grid.AutoScroll = true;
            _grid.Padding = new Padding(14);
            _grid.BackColor = GridBg;
            Controls.Add(_grid);
            _grid.BringToFront();

            Activated += delegate { ReloadCards(); };
            ReloadCards();
        }

        public void ReloadCards()
        {
            _grid.SuspendLayout();
            _grid.Controls.Clear();

            List<ServerAccount> accounts = ServerAccount.LoadAll();
            foreach (ServerAccount a in accounts)
            {
                ServerAccount captured = a;
                CardControl card = new CardControl();
                card.Account = a;
                card.Click += delegate { OpenAccount(captured); };

                ContextMenuStrip menu = new ContextMenuStrip();
                menu.Items.Add("Edit…", null, delegate { EditAccount(captured); });
                menu.Items.Add("Delete", null, delegate { DeleteAccount(captured); });
                card.ContextMenuStrip = menu;

                _grid.Controls.Add(card);
            }

            CardControl add = new CardControl();
            add.IsAdd = true;
            add.Click += delegate { ShowSettings(); };
            _grid.Controls.Add(add);

            _grid.ResumeLayout();
        }

        void OpenAccount(ServerAccount a)
        {
            ServerForm f = new ServerForm(a);
            Program.Context.Register(f);
            f.Show();
        }

        void ShowSettings()
        {
            if (_settings == null || _settings.IsDisposed)
            {
                _settings = new SettingsForm();
                _settings.FormClosed += delegate { ReloadCards(); };
            }
            _settings.Show();
            _settings.BringToFront();
            _settings.Activate();
        }

        void EditAccount(ServerAccount a)
        {
            ShowSettings();
            _settings.SelectAccount(a.AccountID);
        }

        void DeleteAccount(ServerAccount a)
        {
            if (MessageBox.Show("Remove \"" + a.Name + "\"?\n\nThis removes the server from your list "
                + "but does not delete any files.", "Remove Server",
                MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;

            List<ServerAccount> all = ServerAccount.LoadAll();
            all.RemoveAll(delegate(ServerAccount x) { return x.AccountID == a.AccountID; });
            ServerAccount.SaveAll(all);
            ReloadCards();
        }
    }

    // A custom-drawn rounded server card (or the "+" add card), matching the
    // Mac ServerCardButton: rounded fill that shades on hover, a hairline
    // border, a drawn server icon, and centered name / host / protocol.
    public class CardControl : Panel
    {
        public ServerAccount Account;
        public bool IsAdd;
        bool _hover;

        static readonly Color GridBg = Color.FromArgb(240, 240, 240);
        static readonly Color CardBg = Color.White;
        static readonly Color CardHover = Color.FromArgb(236, 236, 236);
        static readonly Color AddBg = Color.FromArgb(246, 246, 246);
        static readonly Color Border = Color.FromArgb(206, 206, 206);

        public CardControl()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Size = new Size(150, 140);
            Margin = new Padding(12);
            BackColor = GridBg;
            Cursor = Cursors.Hand;
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(GridBg);

            Rectangle r = new Rectangle(2, 2, Width - 5, Height - 5);
            using (GraphicsPath path = Round(r, 10))
            {
                using (Brush b = new SolidBrush(_hover ? CardHover : (IsAdd ? AddBg : CardBg)))
                    g.FillPath(b, path);
                using (Pen p = new Pen(Border))
                    g.DrawPath(p, path);
            }

            StringFormat center = new StringFormat();
            center.Alignment = StringAlignment.Center;
            center.LineAlignment = StringAlignment.Center;

            if (IsAdd)
            {
                using (Font f = new Font("Segoe UI", 30f))
                using (Brush b = new SolidBrush(Color.FromArgb(150, 150, 150)))
                    g.DrawString("+", f, b, new RectangleF(0, 0, Width, Height), center);
                return;
            }

            DrawServerIcon(g, new Rectangle(Width / 2 - 19, 22, 38, 34));

            StringFormat top = new StringFormat();
            top.Alignment = StringAlignment.Center;
            top.LineAlignment = StringAlignment.Near;
            top.FormatFlags = StringFormatFlags.NoWrap;
            top.Trimming = StringTrimming.EllipsisCharacter;

            using (Font nf = new Font("Segoe UI", 10.5f, FontStyle.Bold))
            using (Brush nb = new SolidBrush(Color.FromArgb(30, 30, 30)))
                g.DrawString(Account.Name ?? "Server", nf, nb,
                    new RectangleF(4, 68, Width - 8, 20), top);

            using (Font hf = new Font("Segoe UI", 8.5f))
            using (Brush hb = new SolidBrush(Color.FromArgb(120, 120, 120)))
                g.DrawString((Account.Host ?? "") + ":" + Account.Port, hf, hb,
                    new RectangleF(4, 90, Width - 8, 18), top);

            using (Font pf = new Font("Segoe UI", 8f))
            using (Brush pb = new SolidBrush(Color.FromArgb(165, 165, 165)))
                g.DrawString((Account.Protocol ?? "ftp").ToUpperInvariant(), pf, pb,
                    new RectangleF(4, 110, Width - 8, 16), top);
        }

        static void DrawServerIcon(Graphics g, Rectangle r)
        {
            Color tint = Color.FromArgb(92, 112, 142);
            using (Pen p = new Pen(tint, 1.6f))
            using (Brush dot = new SolidBrush(Color.FromArgb(80, 170, 120)))
            {
                int h = (r.Height - 6) / 2;
                Rectangle topBox = new Rectangle(r.X, r.Y, r.Width, h);
                Rectangle botBox = new Rectangle(r.X, r.Y + h + 6, r.Width, h);
                using (GraphicsPath tp = Round(topBox, 4)) g.DrawPath(p, tp);
                using (GraphicsPath bp = Round(botBox, 4)) g.DrawPath(p, bp);
                g.FillEllipse(dot, r.X + 6, topBox.Y + h / 2 - 2, 4, 4);
                g.FillEllipse(dot, r.X + 6, botBox.Y + h / 2 - 2, 4, 4);
            }
        }

        public static GraphicsPath Round(Rectangle r, int radius)
        {
            int d = radius * 2;
            GraphicsPath p = new GraphicsPath();
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }
}
