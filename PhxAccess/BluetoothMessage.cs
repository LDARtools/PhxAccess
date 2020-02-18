using System;

namespace LDARtools.PhxAccess
{
    public class BluetoothMessage
    {
        public byte[] Bytes { get; set; }
        public int Offest { get; set; }
        public int Length { get; set; }
        public byte ResponseKey { get; set; }
        public int WaitTime { get; set; }
        public Exception Exception { get; set; }
    }
}