using AuraDrop.AndroidApp.Views;

namespace AuraDrop.AndroidApp;

public partial class App : Application
{
    private readonly IServiceProvider? _serviceProvider;

    public App()
    {
        InitializeComponent();
    }

    public App(IServiceProvider serviceProvider) : this()
    {
        _serviceProvider = serviceProvider;
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        MainPage? page = null;
        try
        {
            if (_serviceProvider != null)
            {
                page = _serviceProvider.GetService<MainPage>();
            }
            if (page == null && Handler?.MauiContext?.Services != null)
            {
                page = Handler.MauiContext.Services.GetService<MainPage>();
            }
        }
        catch { }

        page ??= new MainPage();
        return new Window(page);
    }
}
