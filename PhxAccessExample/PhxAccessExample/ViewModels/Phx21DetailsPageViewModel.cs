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
    public class Phx21DetailsPageViewModel : ViewModelBase
    {
        private readonly IBluetoothService _bluetoothService;
        private Phx21 _phx21 = null;
        private IBluetoothDevice _device = null;
        private string _name;
        private float _ppm = -100;
        private double _h2Level = 0;
        private double _batteryVoltage = 6;
        private string _status;
        private bool _igniteInProgress = false;
        private DateTime? _igniteTime = null;

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

        public double BatteryVoltage
        {
            get => _batteryVoltage;
            set => SetProperty(ref _batteryVoltage, value);
        }

        public string PpmLabel => Ppm < 0 ? "N/A" : (Ppm < 100 ? $"{Ppm:F2}" : $"{Ppm:F0}");

        public string Status
        {
            get => _status;
            set => SetProperty(ref _status, value);
        }

        public bool CanIgnite => Ppm < 0 && !_igniteInProgress;

        public DelegateCommand IgniteCommand { get; }

        public Phx21DetailsPageViewModel(INavigationService navigationService, IBluetoothService bluetoothService) : base(navigationService)
        {
            _bluetoothService = bluetoothService;
            IgniteCommand = new DelegateCommand(ExecuteIgniteCommand, () => CanIgnite).ObservesProperty(() => CanIgnite);
        }

        private void ExecuteIgniteCommand()
        {
            _igniteInProgress = true;
            _igniteTime = DateTime.Now;
            RaisePropertyChanged(nameof(CanIgnite));
            _phx21.IgniteOn();
        }

        public override void OnNavigatedTo(INavigationParameters parameters)
        {
            base.OnNavigatedTo(parameters);

            try
            {
                if (parameters.ContainsKey("phx"))
                {
                    _phx21 = parameters.GetValue<Phx21>("phx");
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

            if (_phx21 == null || _device == null)
            {
                NavigationService.GoBackAsync();
            }

            _phx21.Error += Phx21Error;
            _phx21.DataPolled += Phx21DataPolled;

            _phx21.StartPollingData(1000);

            var version = _phx21.GetFirmwareVersion();

            Status = $"Firmware version: {version}";
        }

        public override void OnNavigatedFrom(INavigationParameters parameters)
        {
            base.OnNavigatedFrom(parameters);

            _phx21.Error -= Phx21Error;
            _phx21.DataPolled -= Phx21DataPolled;

            Task.Run(() =>
            {
                _phx21.SendGoodbye();
                _phx21.Shutdown();
                _bluetoothService.Disconnect(_device);
            });
        }

        private void Phx21DataPolled(object sender, DataPolledEventArgs e)
        {
            if (e.PhxProperties.ContainsKey(nameof(Phx21Status.IsIgnited)) && e.PhxProperties[nameof(Phx21Status.IsIgnited)] == bool.FalseString)
            {
                Ppm = -100;
                RaisePropertyChanged(nameof(CanIgnite));
            }
            else
            {
                _igniteInProgress = false;
                _igniteTime = null;
                Ppm = e.Ppm;
            }

            if (e.PhxProperties.ContainsKey(nameof(Phx21Status.TankPressure)) && double.TryParse(e.PhxProperties[nameof(Phx21Status.TankPressure)], out var h))
            {
                H2Level = h;
            }

            if (e.PhxProperties.ContainsKey(nameof(Phx21Status.BatteryVoltage)) && double.TryParse(e.PhxProperties[nameof(Phx21Status.BatteryVoltage)], out var b))
            {
                BatteryVoltage = b;
            }

            if (_igniteTime.HasValue && DateTime.Now - _igniteTime.Value > TimeSpan.FromSeconds(90))
            {
                _igniteTime = null;
                _igniteInProgress = false;
                Status = "Ignite failed";
            }
        }

        private void Phx21Error(object sender, ErrorEventArgs e)
        {
            _igniteInProgress = false;
            _igniteTime = null;
            RaisePropertyChanged(nameof(CanIgnite));
            Status = $"phx21 Error: {e.Exception.Message}";
        }
    }
}
