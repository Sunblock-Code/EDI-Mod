using Edi.Core;
using Edi.Forms;
using Serilog;

Thread thread = new Thread(() =>
{
    // Single-instance guard. NOTE: the mutex must stay alive for the whole run — a
    // previous version disposed it immediately, which let a 2nd instance start and
    // then crash fighting over the API port (127.0.0.1:5000). Keep it referenced.
    bool createdNew;
    var mutex = new Mutex(true, "Edi", out createdNew);
    if (!createdNew)
    {
        Environment.Exit(0);
        return;
    }

    try
    {
        var host = Host.CreateDefaultBuilder()
            .UseSerilog((ctx, sp, loggerConfig) =>
            {
                var ediConfig = sp.GetService<Edi.Core.Services.ConfigurationManager>().Get<EdiConfig>();

                loggerConfig
                    .MinimumLevel.Debug()
                    .WriteTo.Conditional(
                        _ => ediConfig.UseLogs,
                        wt => wt.File("./Edilog.txt",
                                    rollingInterval: RollingInterval.Day,
                                    retainedFileCountLimit: 1)
                    );
            })
            .ConfigureServices((context, services) =>
            {
                services.AddEdi("./EdiConfig.json");
                services.AddTransient<App>();
            })
            .Build();

        try
        {
            host.StartAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            // The API server port (127.0.0.1:5000) is in use — another app (e.g. a game launcher
            // or a Logitech G HUB applet) grabbed it. Don't quit: the desktop UI and direct device
            // control don't need the HTTP server (it's only for external games to push events).
            // EDI still opens; the API just won't be available until the port is free.
            Log.Warning(ex, "Web host failed to start (port 127.0.0.1:5000 in use?) — running the UI without the API server.");
        }

        var app = host.Services.GetRequiredService<App>();

        app.Run();

        // 🧹 Paramos servicios al cerrar la app
        try { host.StopAsync().GetAwaiter().GetResult(); } catch { }
        host.Dispose();
    }
    finally
    {
        try { mutex.ReleaseMutex(); } catch { }
        mutex.Dispose();
    }
});
thread.SetApartmentState(ApartmentState.STA);
thread.Start();
