using System.Collections.Concurrent;

namespace LDARtools.PhxAccess
{
    public class MaxSizeList <T> : BlockingCollection<T>
    {
        public int MaxSize { get; protected set; }

        public MaxSizeList(int maxSize)
        {
            MaxSize = maxSize;
        }


        public new void Add(T item)
        {
            base.Add(item);

            while (base.Count > MaxSize)
            {
                base.Take();
            }
        }
    }
}
