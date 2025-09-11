using System;
using System.Windows;

namespace Vibe.Gui;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        var apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            var dlg = new MissingApiKeyWindow();
            if (dlg.ShowDialog() != true)
            {
                Shutdown();
                return;
            }
        }

        base.OnStartup(e);
    }
}
