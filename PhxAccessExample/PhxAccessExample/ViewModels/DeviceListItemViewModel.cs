using System;
using System.Collections.Generic;
using System.Text;
using PhxAccessExample.Interfaces;

namespace PhxAccessExample.ViewModels
{
    public enum DeviceType
    {
        Unknown,
        Phx42,
        Phx21
    }
    public class DeviceListItemViewModel
    {
        public IBluetoothDevice BluetoothDevice { get; }

        public DeviceType DeviceType { get; }

        public string Name => BluetoothDevice.Name;

        public DeviceListItemViewModel(IBluetoothDevice bluetoothDevice)
        {
            BluetoothDevice = bluetoothDevice;

            if (BluetoothDevice.Name.StartsWith("phx42", StringComparison.InvariantCultureIgnoreCase))
            {
                DeviceType = DeviceType.Phx42;
            }
            else if (BluetoothDevice.Name.StartsWith("phx21", StringComparison.InvariantCultureIgnoreCase))
            {
                DeviceType = DeviceType.Phx21;
            }
            else
            {
                DeviceType = DeviceType.Unknown;
            }
        }
    }
}
