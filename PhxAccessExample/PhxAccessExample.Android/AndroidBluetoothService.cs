using Android.Bluetooth;
using Android.Content;
using Java.Lang;
using Java.Util;
using PhxAccessExample.Interfaces;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;

namespace PhxAccessExample.Droid
{
    public class AndroidBluetoothService : IBluetoothService
    {
        private static UUID SERIAL_UUID = UUID.FromString("00001101-0000-1000-8000-00805f9b34fb");
        private readonly Context context;
        private BluetoothAdapter bluetoothAdapter;
        private bool _isDiscovering;
        private Receiver receiver;


        public event PropertyChangedEventHandler PropertyChanged;

        public bool IsDiscovering
        {
            get => _isDiscovering;
            set
            {
                _isDiscovering = value;
                RaisePropertyChanged();
            }
        }

        public ObservableCollection<IBluetoothDevice> DiscoveredDevices { get; } = new ObservableCollection<IBluetoothDevice>();
        public void StartDiscovery()
        {
            DiscoveredDevices.Clear();

            bluetoothAdapter?.StartDiscovery();
        }

        public void StopDiscovery()
        {
            bluetoothAdapter?.CancelDiscovery();
        }


        public (Stream, Stream) Connect(IBluetoothDevice device)
        {
            BluetoothDevice btDevice = (device as Device).BluetoothDevice;

            btDevice.FetchUuidsWithSdp();
            //var parcelUuids = btDevice.GetUuids();

            //var bondState = btDevice.BondState;

            var bluetoothSocket = btDevice.CreateInsecureRfcommSocketToServiceRecord(SERIAL_UUID);

            bluetoothSocket.Connect();

            device.Socket = bluetoothSocket;

            return (bluetoothSocket.InputStream, bluetoothSocket.OutputStream);
        }

        public void Disconnect(IBluetoothDevice device)
        {
            (device.Socket as BluetoothSocket)?.Close();
        }

        public AndroidBluetoothService(Context context)
        {
            bluetoothAdapter = BluetoothAdapter.DefaultAdapter;
            //Address = bluetoothAdapter?.Address;

            // Register for broadcasts when a device is discovered
            receiver = new Receiver();
            receiver.OnDeviceDiscoveredFunc = OnDeviceDiscovered;
            receiver.OnDiscoveryCompleteFunc = OnDeviceDiscoveryComplete;

            var filter = new IntentFilter(BluetoothDevice.ActionFound);
            context.RegisterReceiver(receiver, filter);

            // Register for broadcasts when discovery has finished
            filter = new IntentFilter(BluetoothAdapter.ActionDiscoveryFinished);
            context.RegisterReceiver(receiver, filter);
        }

        private void OnDeviceDiscoveryComplete()
        {
            IsDiscovering = false;
        }

        private void OnDeviceDiscovered(IBluetoothDevice device)
        {
            DiscoveredDevices.Add(device);
        }

        protected void RaisePropertyChanged([CallerMemberName] string name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }

    public class Device : IBluetoothDevice
    {
        public string Name { get; protected set; }
        public bool IsConnected { get; protected set; }
        public int SignalStrength { get; set; }
        public string Address { get; protected set; }
        public object Socket { get; set; }

        internal BluetoothDevice BluetoothDevice { get; set; }

        public Device(BluetoothDevice bluetoothDevice)
        {
            BluetoothDevice = bluetoothDevice;
            Name = bluetoothDevice.Name;
            Address = bluetoothDevice.Address;
        }

        public Device(BluetoothDevice bluetoothDevice, int signalStrength)
        {
            BluetoothDevice = bluetoothDevice;
            Name = bluetoothDevice.Name;
            Address = bluetoothDevice.Address;
            SignalStrength = signalStrength;
        }

        public override string ToString()
        {
            return Name;
        }
    }

    public class Receiver : BroadcastReceiver
    {
        public Action<IBluetoothDevice> OnDeviceDiscoveredFunc;
        public Action OnDiscoveryCompleteFunc;

        public override void OnReceive(Context context, Intent intent)
        {
            string action = intent.Action;

            // When discovery finds a device
            if (action == BluetoothDevice.ActionFound)
            {
                // Get the BluetoothDevice object from the Intent
                BluetoothDevice device = (BluetoothDevice)intent.GetParcelableExtra(BluetoothDevice.ExtraDevice);

                if (string.IsNullOrEmpty(device.Name))
                    return;

                int rssi = intent.GetShortExtra(BluetoothDevice.ExtraRssi, Short.MinValue);

                OnDeviceDiscoveredFunc(new Device(device, rssi));
            }
            else if (action == BluetoothAdapter.ActionDiscoveryFinished)
            {
                OnDiscoveryCompleteFunc?.Invoke();
            }
        }
    }
}