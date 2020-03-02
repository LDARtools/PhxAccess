using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace PhxAccessExample.Interfaces
{
    public interface IBluetoothService : INotifyPropertyChanged
    {
        bool IsDiscovering { get; }
        void StartDiscovery();
        void StopDiscovery();

        ObservableCollection<IBluetoothDevice> DiscoveredDevices { get; }

        Task<(Stream, Stream)> Connect(IBluetoothDevice device);
        void Disconnect(IBluetoothDevice device);
    }
}
