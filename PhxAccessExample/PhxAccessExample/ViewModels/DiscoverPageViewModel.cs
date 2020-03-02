using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Threading.Tasks;
using LDARtools.PhxAccess;
using PhxAccessExample.Interfaces;
using Prism.Commands;
using Prism.Navigation;
using Xamarin.Essentials;

namespace PhxAccessExample.ViewModels
{
    public class DiscoverPageViewModel : ViewModelBase
    {
        bool _isBusy = false;
        private bool _isConnecting = false;
        private readonly IBluetoothService _bluetoothService;
        private List<DeviceListItemViewModel> _discoveredDevices;
        private DeviceListItemViewModel _selectedDevice;
        private Phx42 _phx42 = null;
        private string _status;

        public List<DeviceListItemViewModel> DiscoveredDevices
        {
            get => _discoveredDevices;
            set => SetProperty(ref _discoveredDevices, value);
        }

        public DeviceListItemViewModel SelectedDevice
        {
            get => _selectedDevice;
            set
            {
                if (value != null)
                {
                    Connect(value);
                }

                SetProperty(ref _selectedDevice,  null);
            }
        }

        public bool IsBusy
        {
            get => _isBusy;
            set => SetProperty(ref _isBusy, value);
        }

        public string Status
        {
            get => _status;
            set => SetProperty(ref _status, value);
        }

        public DelegateCommand DiscoverCommand { get; }

        public DiscoverPageViewModel(INavigationService navigationService
            , IBluetoothService bluetoothService
        ) : base(navigationService)
        {
            _bluetoothService = bluetoothService;
            Title = "Discover phxs (pull to refresh)";
            DiscoverCommand = new DelegateCommand(ExecuteDiscoverCommand);
        }

        public override void OnNavigatedTo(INavigationParameters parameters)
        {
            base.OnNavigatedTo(parameters);

            _bluetoothService.DiscoveredDevices.CollectionChanged += DiscoveredDevices_CollectionChanged;
            _bluetoothService.PropertyChanged += BluetoothService_PropertyChanged;

            if (!_bluetoothService.IsDiscovering)
            {
                ExecuteDiscoverCommand();
            }
        }

        public override void OnNavigatedFrom(INavigationParameters parameters)
        {
            base.OnNavigatedFrom(parameters);

            _bluetoothService.DiscoveredDevices.CollectionChanged -= DiscoveredDevices_CollectionChanged;
            _bluetoothService.PropertyChanged -= BluetoothService_PropertyChanged;
        }

        private void DiscoveredDevices_CollectionChanged(object sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            DiscoveredDevices = _bluetoothService.DiscoveredDevices.Select(d => new DeviceListItemViewModel(d)).OrderByDescending(d => d.DeviceType == DeviceType.Phx42).ThenByDescending(d =>  d.DeviceType == DeviceType.Phx21).ToList();
        }

        private void BluetoothService_PropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(IBluetoothService.IsDiscovering) && !_isConnecting)
            {
                IsBusy = _bluetoothService.IsDiscovering;
                Status = "Finished looking for Bluetooth devices";
            }
        }

        private void ExecuteDiscoverCommand()
        {
            if (IsBusy)
                return;

            IsBusy = true;
            Status = "Looking for Bluetooth devices";

            SelectedDevice = null;
            DiscoveredDevices = null;
            _bluetoothService.StartDiscovery();
        }

        private void Connect(DeviceListItemViewModel device)
        {
            if (_isConnecting) return;

            if (device.DeviceType == DeviceType.Unknown)
            {
                Status = $"Can't connect to a device that is not a phx42 or phx21";
                return;
            }

            Task.Run(async () =>
            {
                try
                {
                    IsBusy = true;
                    _isConnecting = true;
                    Status = $"Connecting to {device.Name}";

                    _bluetoothService.StopDiscovery();

                    var streams = _bluetoothService.Connect(device.BluetoothDevice);

                    if (device.DeviceType == DeviceType.Phx42)
                    {
                        _phx42 = new Phx42(new StreamAdapter(streams.Item1), new StreamAdapter(streams.Item2));

                        NavigationParameters parameters = new NavigationParameters();

                        parameters.Add("phx", _phx42);
                        parameters.Add("device", device.BluetoothDevice);

                        await MainThread.InvokeOnMainThreadAsync(async () =>
                        {
                            var result = await NavigationService.NavigateAsync("Phx42DetailsPage", parameters);

                            if (!result.Success)
                            {

                            }
                        });
                    }
                }
                catch (Exception ex)
                {

                }
                finally
                {
                    _isConnecting = false;
                    IsBusy = false;
                }
            });
        }
    }
}
