using System.Windows;
using System.Windows.Controls;

namespace DeviceLab;

public partial class CreateDeviceWindow : Window
{
    internal CreateDeviceWindow(IReadOnlyList<SystemImage> images)
    {
        InitializeComponent();
        ImageBox.ItemsSource = images;
        ImageBox.SelectedIndex = 0;
    }

    internal string DeviceName => DeviceNameBox.Text.Trim();
    internal SystemImage SelectedImage => (SystemImage)ImageBox.SelectedItem;

    private void NameChanged(object sender, TextChangedEventArgs e)
    {
        if (CreateButton is not null)
            CreateButton.IsEnabled = DeviceName.Length > 0;
    }

    private void CreateClicked(object sender, RoutedEventArgs e) => DialogResult = true;
}
