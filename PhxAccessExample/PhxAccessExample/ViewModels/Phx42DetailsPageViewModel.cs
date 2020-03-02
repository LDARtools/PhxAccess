using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using LDARtools.PhxAccess;
using PhxAccessExample.Interfaces;
using Prism.Commands;
using Prism.Navigation;

namespace PhxAccessExample.ViewModels
{
    public class Phx42DetailsPageViewModel : ViewModelBase
    {
        private readonly IBluetoothService _bluetoothService;
        private Phx42 _phx42 = null;
        private IBluetoothDevice _device = null;
        private string _name;
        private float _ppm = -100;
        private double _h2Level = 0;
        private double _batteryPercent = 6;
        private string _status;
        private bool _igniteInProgress = false;

        public string Name
        {
            get => _name;
            set => SetProperty(ref _name, value);
        }

        public float Ppm
        {
            get => _ppm;
            set
            {
                SetProperty(ref _ppm, value);
                RaisePropertyChanged(nameof(PpmLabel));
                RaisePropertyChanged(nameof(CanIgnite));
            }
        }

        public double H2Level
        {
            get => _h2Level;
            set => SetProperty(ref _h2Level, value);
        }

        public double BatteryPercent
        {
            get => _batteryPercent;
            set => SetProperty(ref _batteryPercent, value);
        }

        public string PpmLabel => Ppm < 0 ? "N/A" : (Ppm < 100 ? $"{Ppm:F2}" : $"{Ppm:F0}");

        public string Status
        {
            get => _status;
            set => SetProperty(ref _status, value);
        }

        public bool CanIgnite => Ppm < 0 && !_igniteInProgress;

        public DelegateCommand IgniteCommand { get; }

        public Phx42DetailsPageViewModel(INavigationService navigationService, IBluetoothService bluetoothService) : base(navigationService)
        {
            _bluetoothService = bluetoothService;
            IgniteCommand = new DelegateCommand(ExecuteIgniteCommand, () => CanIgnite).ObservesProperty(() => CanIgnite);
        }

        private void ExecuteIgniteCommand()
        {
            _igniteInProgress = true;
            RaisePropertyChanged(nameof(CanIgnite));
            _phx42.Ignite();
        }

        public override void OnNavigatedTo(INavigationParameters parameters)
        {
            base.OnNavigatedTo(parameters);

            try
            {
                if (parameters.ContainsKey("phx"))
                {
                    _phx42 = parameters.GetValue<Phx42>("phx");
                }

                if (parameters.ContainsKey("device"))
                {
                    _device = parameters.GetValue<IBluetoothDevice>("device");
                    Name = _device.Name;
                }
            }
            catch (Exception)
            {
                //no logging so just eat it
            }

            if (_phx42 == null || _device == null)
            {
                NavigationService.GoBackAsync();
            }

            _phx42.CommandError += Phx42_CommandError;
            _phx42.Error += Phx42_Error;
            _phx42.DataPolled += Phx42_DataPolled;

            _phx42.SetPeriodicReportingInterval(1000);
            _phx42.StartPeriodicReporting(true, true, false, true);
        }

        public override void OnNavigatedFrom(INavigationParameters parameters)
        {
            base.OnNavigatedFrom(parameters);

            _phx42.CommandError -= Phx42_CommandError;
            _phx42.Error -= Phx42_Error;
            _phx42.DataPolled -= Phx42_DataPolled;

            Task.Run(() =>
            {
                _phx42.Shutdown();
                _bluetoothService.Disconnect(_device);
            });
        }

        private void Phx42_DataPolled(object sender, DataPolledEventArgs e)
        {
            Ppm = e.Ppm;

            if (e.PhxProperties.ContainsKey(Phx42PropNames.HPH2) && double.TryParse(e.PhxProperties[Phx42PropNames.HPH2], out var h))
            {
                H2Level = h;
            }

            if (e.PhxProperties.ContainsKey(Phx42PropNames.BatteryCharge) && double.TryParse(e.PhxProperties[Phx42PropNames.BatteryCharge], out var b))
            {
                BatteryPercent = b;
            }
        }

        private void Phx42_Error(object sender, ErrorEventArgs e)
        {
            _igniteInProgress = false;
            RaisePropertyChanged(nameof(CanIgnite));
            Status = $"phx42 Error: {e.Exception.Message}";
        }

        private void Phx42_CommandError(object sender, CommandErrorEventArgs e)
        {
            _igniteInProgress = false;
            RaisePropertyChanged(nameof(CanIgnite));
            Status = $"phx42 {e.Error}";
        }
    }
}
