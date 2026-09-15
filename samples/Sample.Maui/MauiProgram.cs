namespace Sample.Maui;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp() => MauiApp
        .CreateBuilder()
        .UseMauiApp<global::Sample.App>()
        .ConfigureSample()
        .Build();
}
