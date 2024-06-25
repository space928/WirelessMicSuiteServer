using System.Collections.Concurrent;

namespace WirelessMicSuiteServer;

public class MessagePipe<TData, TMeta>
{
    private readonly SpinLock bufferLock;
    private SemaphoreSlim onDataSem;
    private ConcurrentQueue<Message> messages = [];
    private TData[] buffer;
    private int headIndex;
    private int tailIndex;

    /// <summary>
    /// Initializes a new instance of a <see cref="MessagePipe"/> with a given capacity.
    /// </summary>
    /// <param name="capacity"></param>
    public MessagePipe(int capacity = 8192)
    {
        buffer = new TData[capacity];
        onDataSem = new SemaphoreSlim(0);
        bufferLock = new();
    }

    /*
     * Circular buffers
     * Buff:
     * [ 0 1 2 3 4 5 6 7 8 9 ]
     *     [ ] [   ]            <- Simple case, just an array
     *     H         T
     *               {     }    <- new item
     *               
     *  [ ]            [   ]    <- Annoying case, Tail is in front of Head
     *      T          H
     *    
     *                [  ]      <- We don't allow individual items to wrap around to the beginning, so some space may be wasted
     *                H    T requested > remaining so, the last byte is wasted
     * 
     */

    private int RemainingSpace => headIndex <= tailIndex ? (buffer.Length - (tailIndex - headIndex)) : (buffer.Length - (headIndex - tailIndex));
    private bool IsEmpty => headIndex == tailIndex;

    /// <summary>
    /// Gets a segment of memory to write into. <see cref="Push(TMeta)"/> must be called after this method has been called.
    /// </summary>
    /// <param name="size">The number of elements to request.</param>
    /// <returns>An <see cref="ArraySegment{TData}"/> to write into.</returns>
    public ArraySegment<TData> GetBuffer(int size)
    {
        bool taken = false;
        bufferLock.Enter(ref taken);
        ArraySegment<TData> ret;

        int remainingSpaceToEnd = headIndex <= tailIndex ? (buffer.Length - tailIndex) : (buffer.Length - (headIndex - tailIndex));
        if (remainingSpaceToEnd >= size)
        {
            // Grow the array
            TData[] nbuff = new TData[buffer.Length * 2];
            if (headIndex <= tailIndex)
            {
                Array.Copy(buffer, nbuff, buffer.Length);
            } else
            {
                Array.Copy(buffer, 0, nbuff, 0, tailIndex);
                int sizeDiff = nbuff.Length - buffer.Length;
                Array.Copy(buffer, headIndex, nbuff, headIndex + sizeDiff, buffer.Length - headIndex);
                headIndex += sizeDiff;
                buffer = nbuff;
            }
        }

        ret = new(buffer, tailIndex, size);
        tailIndex += size;
        if (tailIndex == buffer.Length)
            tailIndex = 0;

        if (taken)
            bufferLock.Exit();
        return ret;
    }

    /// <summary>
    /// Pushes a previously allocated segment of data into the pipe.
    /// </summary>
    /// <param name="data">The array segment allocated with <see cref="GetBuffer(int)"/>.</param>
    /// <param name="metaData">Metadata describing the message.</param>
    public void Push(ArraySegment<TData> data, TMeta metaData)
    {
        messages.Enqueue(new(metaData, data));
        onDataSem.Release();
    }

    public bool TryPop(out Message msg)
    {
        var res = messages.TryDequeue(out msg);

        // TODO: Needs to handle the case where the item at the head of the cbuff has been GetBuffer() but not Push()
        //       ie: msg refers to a Segment which doesn't start at headIndex...
        //       I guess we should return false in that case or Spin until the necessary Push() is called, should
        //       only occur when data is being consumed much faster than it is produced.
        //       Although, now that I think about it, if we can trust onDataSem to be the count of Push() calls, then
        //       would that be safe? Nope: if GetData is called twice and the second GetData is Pushed before the first,
        //       then we have the same issue.
        // Similarly, we need to be able to know when we can reclaim the ArraySegment returned to the user...
        // Could just copy it into stackalloc mem, but that would limit the max item size...

        return res;
    }

    public Message Pop()
    {
        Message ret;
        while (!messages.TryDequeue(out ret))
            onDataSem.Wait();
        return ret;
    }

    public readonly struct Message(TMeta metaData, ArraySegment<TData> msgData)
    {
        public readonly TMeta metaData = metaData;
        public readonly ArraySegment<TData> msgData = msgData;
    }
}
