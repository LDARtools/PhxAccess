using System;

namespace LDARtools.PhxAccess
{
    public interface IInputStream
    {
        byte ReadByte();
        void Flush();
        
        long ReceiveByteCount { get; }
        TimeSpan ConnectedTime { get; }
    }
}