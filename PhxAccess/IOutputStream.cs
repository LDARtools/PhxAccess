using System;

namespace LDARtools.PhxAccess
{
    public interface IOutputStream
    {
        void Write(byte[] buffer, int offset, int count);
        void Flush();

        long SendByteCount { get; }
        TimeSpan ConnectedTime { get; }
    }
}