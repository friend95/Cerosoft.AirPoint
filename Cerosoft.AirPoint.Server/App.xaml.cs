using Microsoft.UI.Xaml;

namespace Cerosoft.AirPoint.Server
{
    public partial class App : Application
    {
        // Safe nullable window
        private Window? _window;

        public App()
        {
            this.InitializeComponent();
        }

        protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
        {
            _window = new MainWindow();
            _window.Activate();
        }
    }
}