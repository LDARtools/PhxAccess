using System;
using System.Collections.Generic;
using System.Text;

namespace PhxAccessExample.Interfaces
{
    public interface IBluetoothDevice
    {
        string Address { get; }
        string Name { get; }
        bool IsConnected { get; }
        int SignalStrength { get; set; }
        object Socket { get; set; }
    }
}
