using Edi.Core;
using Edi.Core.Gallery;
using Microsoft.AspNetCore.StaticFiles;

using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using System.Threading.Tasks;
using System.Collections.Generic;

using System.Net;
using Microsoft.Extensions.Logging;


namespace Edi.Forms
{
    /// <summary>
    /// Interaction lógica para App.xaml
    /// </summary>
    public partial class App : Application
    {
        public static IEdi Edi { get; private set; }
        public static IServiceProvider ServiceProvider { get; private set; }

        public App(IEdi edi, IServiceProvider serviceProvider)
        {
            Edi = edi;
            ServiceProvider = serviceProvider;
        }


        public App()
        {
        
        }

        [DllImport("shell32.dll", SetLastError = true)]
        private static extern void SetCurrentProcessExplicitAppUserModelID([MarshalAs(UnmanagedType.LPWStr)] string AppID);

        protected override async void OnStartup(StartupEventArgs e)
        {
            // Give the process a stable taskbar identity so Windows reliably uses the window
            // icon (and groups all EDI windows under one button) instead of a blank/default one.
            try { SetCurrentProcessExplicitAppUserModelID("Edi.FunscriptController"); } catch { }

            var mainWindow = new MainWindow();

            mainWindow.Show();
            base.OnStartup(e);
            // Ejecuta el servidor web en un hilo separado para no bloquear la interfaz de usuario

        }
    }
}
