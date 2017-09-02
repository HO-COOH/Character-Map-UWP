using System.Windows.Input;
using Windows.ApplicationModel;
using CharacterMap.Services;
using GalaSoft.MvvmLight;
using GalaSoft.MvvmLight.Command;

namespace CharacterMap.ViewModels
{
    public class SettingsViewModel : ViewModelBase
    {
        private bool _isLightThemeEnabled;
        public bool IsLightThemeEnabled
        {
            get => _isLightThemeEnabled;
            set => Set(ref _isLightThemeEnabled, value);
        }

        private string _appDescription;
        public string AppDescription
        {
            get => _appDescription;
            set => Set(ref _appDescription, value);
        }

        public ICommand SwitchThemeCommand { get; private set; }

        public SettingsViewModel()
        {
            SwitchThemeCommand = new RelayCommand(async () => { await ThemeSelectorService.SwitchThemeAsync(); });
        }

        public void Initialize()
        {
            IsLightThemeEnabled = ThemeSelectorService.IsLightThemeEnabled;
            AppDescription = GetAppDescription();
        }

        private string GetAppDescription()
        {
            var package = Package.Current;
            var packageId = package.Id;
            var version = packageId.Version;

            return $"{package.DisplayName} - {version.Major}.{version.Minor}.{version.Build}.{version.Revision}";
        }
    }
}
