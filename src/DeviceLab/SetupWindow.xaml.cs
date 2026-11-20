using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Media;
using System.Xml;

namespace DeviceLab;

public partial class SetupWindow : Window
{
    private enum Page { Welcome, Review, Install, Done }

    private readonly AndroidSetup setup;
    private SetupPlan plan;
    private Page page;
    private CancellationTokenSource? work;
    private string? license;

    internal SetupWindow(AndroidSetup setup, SetupPlan plan)
    {
        this.setup = setup;
        this.plan = plan;
        InitializeComponent();

        Intro.Text = plan.NeedsAndroid
            ? "Device Lab will download the Android components this PC needs. A fresh setup uses about 2–3 GB of data and needs 9 GB of free space."
            : "Device Lab will download Java so you can create virtual devices. The download is about 50 MB.";
    }

    private async void NextClicked(object sender, RoutedEventArgs e)
    {
        if (work is not null)
            return;

        switch (page)
        {
            case Page.Welcome:
                await ReviewAsync();
                break;
            case Page.Review when !plan.NeedsAndroid || Agree.IsChecked == true:
            case Page.Install:
                await InstallAsync();
                break;
            case Page.Done:
                DialogResult = true;
                break;
        }
    }

    private async Task ReviewAsync()
    {
        await RunAsync("Getting Google’s Android SDK terms…", async cancellationToken =>
        {
            if (plan.NeedsAndroid)
            {
                license = await setup.ReadLicenseAsync(cancellationToken);
                Terms.Text = license.Trim();
                LicensePanel.Visibility = Visibility.Visible;
            }

            page = Page.Review;
            PageTitle.Text = "Ready to install";
            Intro.Text = plan.NeedsAndroid
                ? "Review Google’s terms, then agree to continue with the Android download."
                : "Device Lab will install Java and use it automatically.";
            NextButton.Content = "_Install";
            SetMessage(null);
        });
    }

    private async Task InstallAsync()
    {
        LicensePanel.Visibility = Visibility.Collapsed;
        page = Page.Install;
        PageTitle.Text = "Installing Android setup";
        Intro.Text = "Keep this window open while Device Lab downloads and installs the required files.";
        NextButton.Content = "_Try again";

        await RunAsync("Preparing the downloads…", async cancellationToken =>
        {
            plan = await setup.PlanAsync(cancellationToken);
            await setup.InstallAsync(plan, Agree.IsChecked == true ? license : null,
                new Progress<SetupProgress>(update =>
                {
                    if (!cancellationToken.IsCancellationRequested && work is not null)
                    {
                        SetupProgressBar.IsIndeterminate = update.Percent is null;
                        SetupProgressBar.Value = update.Percent ?? 0;
                        SetMessage(update.Message);
                    }
                }), cancellationToken);

            page = Page.Done;
            PageTitle.Text = "Android setup is complete";
            Intro.Text = "Click Finish to return to Device Lab.";
            NextButton.Content = "_Finish";
            SetMessage(null);
        });
    }

    private async Task RunAsync(string message, Func<CancellationToken, Task> operation)
    {
        using var cancellation = new CancellationTokenSource();
        work = cancellation;
        NextButton.IsEnabled = false;
        CancelButton.Content = "_Cancel";
        SetMessage(message);
        SetupProgressBar.IsIndeterminate = true;
        SetupProgressBar.Visibility = Visibility.Visible;
        try
        {
            await operation(cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            SetMessage("Setup was canceled. You can try again when you’re ready.");
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or InvalidOperationException
            or UnauthorizedAccessException or Win32Exception or TimeoutException or OperationCanceledException or XmlException)
        {
            var detail = ex is HttpRequestException or OperationCanceledException or TimeoutException
                ? "The download was interrupted. Check your internet connection and try again."
                : ex.Message;
            SetMessage(detail, isError: true);
        }
        finally
        {
            work = null;
            SetupProgressBar.Visibility = Visibility.Collapsed;
            CancelButton.Content = "Close";
            CancelButton.IsEnabled = true;
            CancelButton.Visibility = page == Page.Done ? Visibility.Collapsed : Visibility.Visible;
            UpdateNextButton();
        }
    }

    private void SetMessage(string? message, bool isError = false)
    {
        SetupMessage.Text = message;
        SetupMessage.ToolTip = message;
        SetupMessage.MaxHeight = 130;
        SetupMessage.Foreground = (Brush)FindResource(isError ? "Error" : "Muted");
        SetupMessage.Visibility = message is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void AgreementChanged(object sender, RoutedEventArgs e) => UpdateNextButton();

    private void UpdateNextButton()
    {
        if (NextButton is not null)
            NextButton.IsEnabled = work is null && (page != Page.Review || !plan.NeedsAndroid || Agree.IsChecked == true);
    }

    private void CancelClicked(object sender, RoutedEventArgs e)
    {
        if (work is null)
            DialogResult = false;
        else
            CancelSetup();
    }

    private void WindowClosing(object? sender, CancelEventArgs e)
    {
        if (work is not null)
        {
            e.Cancel = true;
            CancelSetup();
        }
    }

    private void CancelSetup()
    {
        work?.Cancel();
        CancelButton.IsEnabled = false;
        SetMessage("Canceling setup…");
    }
}
