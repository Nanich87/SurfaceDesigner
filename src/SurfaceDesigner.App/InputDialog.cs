using System.Windows;
using System.Windows.Controls;

namespace SurfaceDesigner.App;

/// <summary>Прост диалог за въвеждане на текст/число.</summary>
internal sealed class InputDialog : Window
{
    private readonly TextBox _box;

    private InputDialog(string title, string prompt, string value, bool multiline = false)
    {
        Title = title;
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        var panel = new StackPanel { Margin = new Thickness(12), MinWidth = 280 };
        panel.Children.Add(new TextBlock { Text = prompt, Margin = new Thickness(0, 0, 0, 6) });
        _box = multiline
            ? new TextBox
            {
                Text = value,
                AcceptsReturn = true,
                Height = 320,
                Width = 420,
                FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            }
            : new TextBox { Text = value };
        panel.Children.Add(_box);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        var ok = new Button { Content = "OK", Width = 80, IsDefault = true, Margin = new Thickness(0, 0, 6, 0) };
        ok.Click += (_, _) => DialogResult = true;
        var cancel = new Button { Content = "Отказ", Width = 80, IsCancel = true };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        panel.Children.Add(buttons);
        Content = panel;

        Loaded += (_, _) =>
        {
            _box.Focus();
            if (!multiline)
            {
                _box.SelectAll();
            }
        };
    }

    /// <summary>Избор на една от няколко възможности (радио бутони). Връща индекса или null при отказ.</summary>
    public static int? AskChoice(Window owner, string title, string prompt, IReadOnlyList<string> options, int selected = 0)
    {
        var win = new Window
        {
            Title = title,
            Owner = owner,
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
        };
        var panel = new StackPanel { Margin = new Thickness(12), MinWidth = 300 };
        panel.Children.Add(new TextBlock { Text = prompt, Margin = new Thickness(0, 0, 0, 8), TextWrapping = TextWrapping.Wrap, MaxWidth = 420 });
        var radios = options.Select((o, i) => new RadioButton { Content = o, IsChecked = i == selected, Margin = new Thickness(0, 2, 0, 2) }).ToList();
        foreach (var r in radios)
        {
            panel.Children.Add(r);
        }

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        var ok = new Button { Content = "OK", Width = 80, IsDefault = true, Margin = new Thickness(0, 0, 6, 0) };
        ok.Click += (_, _) => win.DialogResult = true;
        buttons.Children.Add(ok);
        buttons.Children.Add(new Button { Content = "Отказ", Width = 80, IsCancel = true });
        panel.Children.Add(buttons);
        win.Content = panel;

        return win.ShowDialog() == true ? radios.FindIndex(r => r.IsChecked == true) : null;
    }

    public static string? AskMultiline(Window owner, string title, string prompt, string value)
    {
        var d = new InputDialog(title, prompt, value, multiline: true) { Owner = owner };
        return d.ShowDialog() == true ? d._box.Text : null;
    }

    public static string? AskText(Window owner, string title, string prompt, string value = "")
    {
        var d = new InputDialog(title, prompt, value) { Owner = owner };
        return d.ShowDialog() == true ? d._box.Text : null;
    }

    public static double? AskNumber(Window owner, string title, string prompt, double value)
    {
        while (true)
        {
            var text = AskText(owner, title, prompt, value.ToString("0.###"));
            if (text == null)
            {
                return null;
            }

            if (NumberParser.TryParse(text, out var v))
            {
                return v;
            }

            MessageBox.Show(owner, "Невалидно число.", title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}

internal static class NumberParser
{
    public static bool TryParse(string? text, out double value) =>
        double.TryParse(
            (text ?? "").Trim().Replace(',', '.'),
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out value) && double.IsFinite(value);
}
