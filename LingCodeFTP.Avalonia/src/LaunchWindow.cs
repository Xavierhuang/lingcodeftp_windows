using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace LingCodeFtp
{
    // Launch window: a grid of rounded server cards + an Add card + Settings.
    public class LaunchWindow : Window
    {
        WrapPanel _grid;

        static readonly IBrush GridBg = new SolidColorBrush(Color.FromRgb(240, 240, 240));
        static readonly IBrush HeaderBg = new SolidColorBrush(Color.FromRgb(246, 246, 246));
        static readonly IBrush CardBg = Brushes.White;
        static readonly IBrush CardHover = new SolidColorBrush(Color.FromRgb(236, 236, 236));
        static readonly IBrush AddBg = new SolidColorBrush(Color.FromRgb(246, 246, 246));
        static readonly IBrush Border = new SolidColorBrush(Color.FromRgb(206, 206, 206));
        static readonly IBrush Hairline = new SolidColorBrush(Color.FromRgb(212, 212, 212));

        public LaunchWindow()
        {
            Title = "LingCode FTP";
            Width = 840;
            Height = 560;
            MinWidth = 440; MinHeight = 340;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = GridBg;

            DockPanel root = new DockPanel();

            // Header
            Border header = new Border { Background = HeaderBg, Height = 56,
                BorderBrush = Hairline, BorderThickness = new Thickness(0, 0, 0, 1) };
            DockPanel.SetDock(header, Dock.Top);
            Grid hgrid = new Grid { Margin = new Thickness(18, 0, 16, 0) };
            hgrid.ColumnDefinitions = new ColumnDefinitions("*,Auto");
            TextBlock title = new TextBlock { Text = "LingCode FTP", FontSize = 20, FontWeight = FontWeight.Bold,
                VerticalAlignment = VerticalAlignment.Center, Foreground = Brushes.Black };
            Grid.SetColumn(title, 0);
            Button settings = new Button { Content = "⚙  Settings", VerticalAlignment = VerticalAlignment.Center };
            settings.Click += delegate { ShowSettings(); };
            Grid.SetColumn(settings, 1);
            hgrid.Children.Add(title); hgrid.Children.Add(settings);
            header.Child = hgrid;
            root.Children.Add(header);

            _grid = new WrapPanel { Margin = new Thickness(14) };
            ScrollViewer scroll = new ScrollViewer { Content = _grid, Background = GridBg,
                HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
            root.Children.Add(scroll);

            Content = root;

            Activated += delegate { ReloadCards(); };
            ReloadCards();
        }

        void ReloadCards()
        {
            _grid.Children.Clear();
            List<ServerAccount> accounts = ServerAccount.LoadAll();
            foreach (ServerAccount a in accounts)
                _grid.Children.Add(MakeCard(a));
            _grid.Children.Add(MakeAddCard());
        }

        Control MakeCard(ServerAccount account)
        {
            Border card = new Border
            {
                Width = 150, Height = 140, Margin = new Thickness(12),
                CornerRadius = new CornerRadius(10), Background = CardBg,
                BorderBrush = Border, BorderThickness = new Thickness(1),
                Cursor = new Cursor(StandardCursorType.Hand)
            };

            StackPanel sp = new StackPanel { Margin = new Thickness(6, 18, 6, 6),
                HorizontalAlignment = HorizontalAlignment.Stretch };
            sp.Children.Add(ServerIcon());
            sp.Children.Add(new TextBlock { Text = account.Name ?? "Server", FontWeight = FontWeight.Bold,
                FontSize = 13, Margin = new Thickness(0, 10, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = new SolidColorBrush(Color.FromRgb(30, 30, 30)) });
            sp.Children.Add(new TextBlock { Text = (account.Host ?? "") + ":" + account.Port, FontSize = 11,
                Margin = new Thickness(0, 4, 0, 0), HorizontalAlignment = HorizontalAlignment.Center,
                Foreground = new SolidColorBrush(Color.FromRgb(120, 120, 120)) });
            sp.Children.Add(new TextBlock { Text = (account.Protocol ?? "ftp").ToUpperInvariant(), FontSize = 10,
                Margin = new Thickness(0, 4, 0, 0), HorizontalAlignment = HorizontalAlignment.Center,
                Foreground = new SolidColorBrush(Color.FromRgb(165, 165, 165)) });
            card.Child = sp;

            card.PointerEntered += delegate { card.Background = CardHover; };
            card.PointerExited += delegate { card.Background = CardBg; };
            ServerAccount captured = account;
            card.PointerPressed += delegate(object s, PointerPressedEventArgs e)
            {
                if (e.GetCurrentPoint(card).Properties.IsLeftButtonPressed) OpenAccount(captured);
            };

            ContextMenu menu = new ContextMenu();
            MenuItem edit = new MenuItem { Header = "Edit…" };
            edit.Click += delegate { EditAccount(captured); };
            MenuItem del = new MenuItem { Header = "Delete" };
            del.Click += delegate { DeleteAccount(captured); };
            menu.Items.Add(edit); menu.Items.Add(del);
            card.ContextMenu = menu;

            return card;
        }

        Control MakeAddCard()
        {
            Border card = new Border
            {
                Width = 150, Height = 140, Margin = new Thickness(12),
                CornerRadius = new CornerRadius(10), Background = AddBg,
                BorderBrush = Border, BorderThickness = new Thickness(1),
                Cursor = new Cursor(StandardCursorType.Hand)
            };
            card.Child = new TextBlock { Text = "+", FontSize = 34,
                Foreground = new SolidColorBrush(Color.FromRgb(150, 150, 150)),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            card.PointerPressed += delegate(object s, PointerPressedEventArgs e)
            {
                if (e.GetCurrentPoint(card).Properties.IsLeftButtonPressed) ShowSettings();
            };
            return card;
        }

        static Control ServerIcon()
        {
            IBrush tint = new SolidColorBrush(Color.FromRgb(92, 112, 142));
            IBrush dot = new SolidColorBrush(Color.FromRgb(80, 170, 120));
            StackPanel s = new StackPanel { Spacing = 4, HorizontalAlignment = HorizontalAlignment.Center };
            for (int i = 0; i < 2; i++)
            {
                Border box = new Border { Width = 38, Height = 14, CornerRadius = new CornerRadius(3),
                    BorderBrush = tint, BorderThickness = new Thickness(1.5) };
                box.Child = new Ellipse { Width = 4, Height = 4, Fill = dot,
                    HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(5, 0, 0, 0) };
                s.Children.Add(box);
            }
            return s;
        }

        void OpenAccount(ServerAccount a) { new ServerWindow(a).Show(); }

        void ShowSettings()
        {
            SettingsWindow w = new SettingsWindow();
            w.Closed += delegate { ReloadCards(); };
            w.Show();
        }

        void EditAccount(ServerAccount a)
        {
            SettingsWindow w = new SettingsWindow();
            w.Closed += delegate { ReloadCards(); };
            w.Show();
            w.SelectAccount(a.AccountID);
        }

        async void DeleteAccount(ServerAccount a)
        {
            bool ok = await Dialogs.Confirm(this, "Remove Server",
                "Remove \"" + a.Name + "\"?\nThis removes the server from your list but does not delete any files.");
            if (!ok) return;
            List<ServerAccount> all = ServerAccount.LoadAll();
            all.RemoveAll(delegate(ServerAccount x) { return x.AccountID == a.AccountID; });
            ServerAccount.SaveAll(all);
            ReloadCards();
        }
    }
}
