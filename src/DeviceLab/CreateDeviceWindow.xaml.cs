using System.Windows;

namespace DeviceLab;

public partial class CreateDeviceWindow : Window
{
    internal CreateDeviceWindow(IReadOnlyList<SystemImage> images, string suggestedName, bool startImmediately)
    {
        InitializeComponent();
        DeviceNameBox.Text = suggestedName;
        ImageBox.ItemsSource = images;
        ImageBox.SelectedItem = images.FirstOrDefault(image => image.Package == AndroidSetup.RecommendedSystem) ?? images[0];
        if (!startImmediately)
            CreateButton.Content = "_Create device";
    }

    internal string DeviceName => DeviceNameBox.Text.Trim();
    internal SystemImage SelectedImage => (SystemImage)ImageBox.SelectedItem;

    private void DetailsChanged(object sender, RoutedEventArgs e)
    {
        if (CreateButton is null || ImageBox?.SelectedItem is not SystemImage image)
            return;

        CreateButton.IsEnabled = DeviceName.Length > 0;
        DeviceSummary.Text = $"{DeviceName}  ·  {image.DisplayName}";
    }

    private void CreateClicked(object sender, RoutedEventArgs e) => DialogResult = true;
}
