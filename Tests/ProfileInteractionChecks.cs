using System.IO;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using ApiConfigTool;
using ApiConfigTool.Services;

internal static partial class Program
{
    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) yield return match;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }

    private static void ClickProfileAction(MainWindow window, string id, string action)
    {
        var list = (ListBox)window.FindName("ProfileList");
        var card = list.Items.Cast<MainWindow.ProfileCard>().Single(c => c.Profile.Id == id);
        list.ScrollIntoView(card);
        window.UpdateLayout();
        var container = (ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(card);
        var buttons = Descendants<Button>(container).ToList();
        var enable = buttons.Single(b => Equals(b.Content, "启用"));
        var delete = buttons.Single(b => Equals(b.Content, "删除"));
        Check(enable.Parent == delete.Parent && delete.IsEnabled);
        Check(delete.Parent is StackPanel { Orientation: Orientation.Horizontal });
        var button = buttons.Single(b => Equals(b.Content, action));
        Check(Equals(button.Tag, id));
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Pump();
    }

    private static void ConfirmedDeletionChecks(MainWindow window, IProfileConfigService service, string id)
    {
        var before = File.ReadAllBytes(service.StatePath);
        var live = File.ReadAllBytes(service.ConfigPath);
        var selected = ((ListBox)window.FindName("ProfileList")).SelectedItem;
        var title = ((TextBox)window.FindName("ProfileNameBox")).Text;
        var calls = 0;
        window.ConfirmProfileDeletion = profile => { Check(profile.Id == id); calls++; return false; };
        ClickProfileAction(window, id, "删除");
        Check(calls == 1 && before.SequenceEqual(File.ReadAllBytes(service.StatePath)));
        Check(live.SequenceEqual(File.ReadAllBytes(service.ConfigPath)));
        Check(ReferenceEquals(selected, ((ListBox)window.FindName("ProfileList")).SelectedItem));
        Check(((TextBox)window.FindName("ProfileNameBox")).Text == title);
        window.ConfirmProfileDeletion = profile => { Check(profile.Id == id); calls++; return true; };
        ClickProfileAction(window, id, "删除");
        Check(calls == 2 && service.LoadProfiles().Profiles.All(p => p.Id != id));
        Check(live.SequenceEqual(File.ReadAllBytes(service.ConfigPath)));
        window.ConfirmProfileDeletion = _ => true;
        _passed++;
        Console.WriteLine("PASS card deletion prompts, cancellation preserves state, confirmation removes active profile");
    }

    private static void DeleteOtherAndLastProfileChecks(MainWindow window, IProfileConfigService service, string activeId)
    {
        var list = (ListBox)window.FindName("ProfileList");
        var other = list.Items.Cast<MainWindow.ProfileCard>().Single(c => c.Profile.Id != activeId);
        list.SelectedItem = other; Pump();
        var url = (TextBox)window.FindName("BaseUrlBox");
        url.Text = "https://draft.example.com/v1";
        ClickProfileAction(window, activeId, "启用");
        var live = File.ReadAllBytes(service.ConfigPath);
        ClickProfileAction(window, activeId, "删除");
        Check(list.Items.Count == 1 && ((MainWindow.ProfileCard)list.SelectedItem).Profile.Id == other.Profile.Id);
        Check(url.Text == "https://draft.example.com/v1");
        Check(live.SequenceEqual(File.ReadAllBytes(service.ConfigPath)));
        url.Text = other.Profile.BaseUrl;
        ClickProfileAction(window, other.Profile.Id, "删除");
        Check(list.Items.Count == 0 && list.SelectedItem is null);
        Check(((FrameworkElement)window.FindName("EmptyProfiles")).Visibility == Visibility.Visible);
        Check(((TextBlock)window.FindName("EditorTitle")).Text == "新增配置");
        Check(url.Text == "" && service.LoadProfiles().Profiles.Count == 0);
        _passed++;
        Console.WriteLine("PASS deleting another card preserves the editor draft; deleting the final card shows the empty form");
    }

    private static void ModelDropdownChecks(MainWindow window, ComboBox combo)
    {
        var editor = (TextBox)combo.Template.FindName("PART_EditableTextBox", combo);
        void ClickInput(UIElement target)
        {
            target.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
            { RoutedEvent = UIElement.PreviewMouseLeftButtonUpEvent });
            Pump();
        }
        var original = combo.Text;
        ClickInput(editor);
        Check(!combo.IsDropDownOpen); // An empty list still permits manual input.
        combo.Items.Add("gpt-5"); combo.Items.Add("gpt-5-mini");
        combo.Text = original;
        ClickInput(editor);
        Check(combo.IsDropDownOpen && combo.Text == original);
        var popup = (Popup)combo.Template.FindName("PART_Popup", combo);
        Check(popup.IsOpen);
        combo.SelectedIndex = 1;
        combo.IsDropDownOpen = false;
        Pump();
        Check(combo.Text == "gpt-5-mini" && editor.Text == "gpt-5-mini");
        var arrow = Descendants<ToggleButton>(combo).Single();
        var toggle = (IToggleProvider)new ToggleButtonAutomationPeer(arrow).GetPattern(PatternInterface.Toggle);
        toggle.Toggle(); Pump();
        Check(combo.IsDropDownOpen); // The arrow continues to toggle normally.
        toggle.Toggle(); Pump();
        Check(!combo.IsDropDownOpen);
        var border = (Border)((Grid)VisualTreeHelper.GetChild(combo, 0)).Children[0];
        ClickInput(border);
        Check(combo.IsDropDownOpen);
        editor.Text = "manually-entered-model"; Pump();
        Check(combo.Text == "manually-entered-model");
        combo.IsDropDownOpen = false;
        combo.Items.Clear(); combo.Text = original;
        _passed++;
        Console.WriteLine("PASS model input and border open the dropdown, selection updates text, arrow toggles and manual entry works");
    }
}
