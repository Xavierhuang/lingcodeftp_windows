using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace LingCodeFtp
{
    // Minimal modal dialogs (Avalonia has no built-in MessageBox).
    static class Dialogs
    {
        public static Task<bool> Confirm(Window owner, string title, string message)
        {
            Window dlg = new Window
            {
                Title = title, Width = 400, SizeToContent = SizeToContent.Height,
                CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Background = Brushes.White
            };
            StackPanel sp = new StackPanel { Margin = new Thickness(16), Spacing = 14 };
            sp.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Black });
            StackPanel row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8,
                HorizontalAlignment = HorizontalAlignment.Right };
            Button ok = new Button { Content = "OK", MinWidth = 74 };
            Button cancel = new Button { Content = "Cancel", MinWidth = 74 };
            ok.Click += delegate { dlg.Close(true); };
            cancel.Click += delegate { dlg.Close(false); };
            row.Children.Add(ok); row.Children.Add(cancel);
            sp.Children.Add(row);
            dlg.Content = sp;
            return dlg.ShowDialog<bool>(owner);
        }

        public static Task Alert(Window owner, string title, string message)
        {
            Window dlg = new Window
            {
                Title = title, Width = 400, SizeToContent = SizeToContent.Height,
                CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Background = Brushes.White
            };
            StackPanel sp = new StackPanel { Margin = new Thickness(16), Spacing = 14 };
            sp.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Black });
            Button ok = new Button { Content = "OK", MinWidth = 74, HorizontalAlignment = HorizontalAlignment.Right };
            ok.Click += delegate { dlg.Close(); };
            sp.Children.Add(ok);
            dlg.Content = sp;
            return dlg.ShowDialog(owner);
        }

        public static Task<string> Prompt(Window owner, string title, string message, string def)
        {
            Window dlg = new Window
            {
                Title = title, Width = 400, SizeToContent = SizeToContent.Height,
                CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Background = Brushes.White
            };
            StackPanel sp = new StackPanel { Margin = new Thickness(16), Spacing = 12 };
            sp.Children.Add(new TextBlock { Text = message, Foreground = Brushes.Black });
            TextBox tb = new TextBox { Text = def ?? "" };
            sp.Children.Add(tb);
            StackPanel row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8,
                HorizontalAlignment = HorizontalAlignment.Right };
            Button ok = new Button { Content = "OK", MinWidth = 74, IsDefault = true };
            Button cancel = new Button { Content = "Cancel", MinWidth = 74, IsCancel = true };
            ok.Click += delegate { dlg.Close(tb.Text); };
            cancel.Click += delegate { dlg.Close((string)null); };
            row.Children.Add(ok); row.Children.Add(cancel);
            sp.Children.Add(row);
            dlg.Content = sp;
            tb.SelectAll();
            return dlg.ShowDialog<string>(owner);
        }
    }
}
