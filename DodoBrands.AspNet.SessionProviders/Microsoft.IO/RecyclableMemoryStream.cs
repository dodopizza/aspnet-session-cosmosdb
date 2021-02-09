// The MIT License (MIT)
// 
// Copyright (c) 2015-2016 Microsoft
// 
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
// 
// The above copyright notice and this permission notice shall be included in all
// copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
// SOFTWARE.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace Microsoft.IO
{
#if NETCOREAPP2_1 || NETSTANDARD2_1
    using System.Buffers;
#endif

    /// <summary>
    /// MemoryStream implementation that deals with pooling and managing memory streams which use potentially large
    /// buffers.
    /// </summary>
    /// <remarks>
    /// This class works in tandem with the RecyclableMemoryStreamManager to supply MemoryStream
    /// objects to callers, while avoiding these specific problems:
    /// 1. LOH allocations - since all large buffers are pooled, they will never incur a Gen2 GC
    /// 2. Memory waste - A standard memory stream doubles its size when it runs out of room. This
    /// leads to continual memory growth as each stream approaches the maximum allowed size.
    /// 3. Memory copying - Each time a MemoryStream grows, all the bytes are copied into new buffers.
    /// This implementation only copies the bytes when GetBuffer is called.
    /// 4. Memory fragmentation - By using homogeneous buffer sizes, it ensures that blocks of memory
    /// can be easily reused.
    /// 
    /// The stream is implemented on top of a series of uniformly-sized blocks. As the stream's length grows,
    /// additional blocks are retrieved from the memory manager. It is these blocks that are pooled, not the stream
    /// object itself.
    /// 
    /// The biggest wrinkle in this implementation is when GetBuffer() is called. This requires a single 
    /// contiguous buffer. If only a single block is in use, then that block is returned. If multiple blocks 
    /// are in use, we retrieve a larger buffer from the memory manager. These large buffers are also pooled, 
    /// split by size--they are multiples/exponentials of a chunk size (1 MB by default).
    /// 
    /// Once a large buffer is assigned to the stream the small blocks are NEVER again used for this stream. All operations take place on the 
    /// large buffer. The large buffer can be replaced by a larger buffer from the pool as needed. All blocks and large buffers 
    /// are maintained in the stream until the stream is disposed (unless AggressiveBufferReturn is enabled in the stream manager).
    /// 
    /// A further wrinkle is what happens when the stream is longer than the maximum allowable array length under .NET. This is allowed
    /// when only blocks are in use, and only the Read/Write APIs are used. Once a stream grows to this size, any attempt to convert it
    /// to a single buffer will result in an exception. Similarly, if a stream is already converted to use a single larger buffer, then
    /// it cannot grow beyond the limits of the maximum allowable array size.
    /// </remarks>
    internal sealed class RecyclableMemoryStream_ : MemoryStream
    {
        private static readonly byte[] emptyArray = new byte[0];

        /// <summary>
        /// All of these blocks must be the same size
        /// </summary>
        private readonly List<byte[]> blocks = new List<byte[]>(1);

        private readonly Guid id;

        private readonly RecyclableMemoryStreamManager_ memoryManager;

        private readonly string tag;

        /// <summary>
        /// This list is used to store buffers once they're replaced by something larger.
        /// This is for the cases where you have users of this class that may hold onto the buffers longer
        /// than they should and you want to prevent race conditions which could corrupt the data.
        /// </summary>
        private List<byte[]> dirtyBuffers;

        private bool disposed;

        /// <summary>
        /// This is only set by GetBuffer() if the necessary buffer is larger than a single block size, or on
        /// construction if the caller immediately requests a single large buffer.
        /// </summary>
        /// <remarks>If this field is non-null, it contains the concatenation of the bytes found in the individual
        /// blocks. Once it is created, this (or a larger) largeBuffer will be used for the life of the stream.
        /// </remarks>
        private byte[] largeBuffer;

        /// <summary>
        /// Unique identifier for this stream across its entire lifetime
        /// </summary>
        /// <exception cref="ObjectDisposedException">Object has been disposed</exception>
        internal Guid Id
        {
            get
            {
                this.CheckDisposed();
                return this.id;
            }
        }

        /// <summary>
        /// A temporary identifier for the current usage of this stream.
        /// </summary>
        /// <exception cref="ObjectDisposedException">Object has been disposed</exception>
        internal string Tag
        {
            get
            {
                this.CheckDisposed();
                return this.tag;
            }
        }

        /// <summary>
        /// Gets the memory manager being used by this stream.
        /// </summary>
        /// <exception cref="ObjectDisposedException">Object has been disposed</exception>
        internal RecyclableMemoryStreamManager_ MemoryManager
        {
            get
            {
                this.CheckDisposed();
                return this.memoryManager;
            }
        }

        /// <summary>
        /// Callstack of the constructor. It is only set if MemoryManager.GenerateCallStacks is true,
        /// which should only be in debugging situations.
        /// </summary>
        internal string AllocationStack { get; }

        /// <summary>
        /// Callstack of the Dispose call. It is only set if MemoryManager.GenerateCallStacks is true,
        /// which should only be in debugging situations.
        /// </summary>
        internal string DisposeStack { get; private set; }

        #region Constructors
        /// <summary>
        /// Allocate a new RecyclableMemoryStream object.
        /// </summary>
        /// <param name="memoryManager">The memory manager</param>
        public RecyclableMemoryStream_(RecyclableMemoryStreamManager_ memoryManager)
            : this(memoryManager, Guid.NewGuid(), null, 0, null) { }

        /// <summary>
        /// Allocate a new RecyclableMemoryStream object.
        /// </summary>
        /// <param name="memoryManager">The memory manager</param>
        /// <param name="id">A unique identifier which can be used to trace usages of the stream.</param>
        public RecyclableMemoryStream_(RecyclableMemoryStreamManager_ memoryManager, Guid id)
            : this(memoryManager, id, null, 0, null) { }

        /// <summary>
        /// Allocate a new RecyclableMemoryStream object
        /// </summary>
        /// <param name="memoryManager">The memory manager</param>
        /// <param name="tag">A string identifying this stream for logging and debugging purposes</param>
        public RecyclableMemoryStream_(RecyclableMemoryStreamManager_ memoryManager, string tag)
            : this(memoryManager, Guid.NewGuid(), tag, 0, null) { }

        /// <summary>
        /// Allocate a new RecyclableMemoryStream object
        /// </summary>
        /// <param name="memoryManager">The memory manager</param>
        /// <param name="id">A unique identifier which can be used to trace usages of the stream.</param>
        /// <param name="tag">A string identifying this stream for logging and debugging purposes</param>
        public RecyclableMemoryStream_(RecyclableMemoryStreamManager_ memoryManager, Guid id, string tag)
            : this(memoryManager, id, tag, 0, null) { }

        /// <summary>
        /// Allocate a new RecyclableMemoryStream object
        /// </summary>
        /// <param name="memoryManager">The memory manager</param>
        /// <param name="tag">A string identifying this stream for logging and debugging purposes</param>
        /// <param name="requestedSize">The initial requested size to prevent future allocations</param>
        public RecyclableMemoryStream_(RecyclableMemoryStreamManager_ memoryManager, string tag, int requestedSize)
            : this(memoryManager, Guid.NewGuid(), tag, requestedSize, null) { }

        /// <summary>
        /// Allocate a new RecyclableMemoryStream object
        /// </summary>
        /// <param name="memoryManager">The memory manager</param>
        /// <param name="tag">A string identifying this stream for logging and debugging purposes</param>
        /// <param name="requestedSize">The initial requested size to prevent future allocations</param>
        public RecyclableMemoryStream_(RecyclableMemoryStreamManager_ memoryManager, string tag, long requestedSize)
            : this(memoryManager, Guid.NewGuid(), tag, requestedSize, null) { }

        /// <summary>
        /// Allocate a new RecyclableMemoryStream object
        /// </summary>
        /// <param name="memoryManager">The memory manager</param>
        /// <param name="id">A unique identifier which can be used to trace usages of the stream.</param>
        /// <param name="tag">A string identifying this stream for logging and debugging purposes</param>
        /// <param name="requestedSize">The initial requested size to prevent future allocations</param>
        public RecyclableMemoryStream_(RecyclableMemoryStreamManager_ memoryManager, Guid id, string tag, int requestedSize)
            : this(memoryManager, id, tag, requestedSize, null) { }

        /// <summary>
        /// Allocate a new RecyclableMemoryStream object
        /// </summary>
        /// <param name="memoryManager">The memory manager</param>
        /// <param name="id">A unique identifier which can be used to trace usages of the stream.</param>
        /// <param name="tag">A string identifying this stream for logging and debugging purposes</param>
        /// <param name="requestedSize">The initial requested size to prevent future allocations</param>
        public RecyclableMemoryStream_(RecyclableMemoryStreamManager_ memoryManager, Guid id, string tag, long requestedSize)
            : this(memoryManager, id, tag, requestedSize, null) { }

        /// <summary>
        /// Allocate a new RecyclableMemoryStream object
        /// </summary>
        /// <param name="memoryManager">The memory manager</param>
        /// <param name="id">A unique identifier which can be used to trace usages of the stream.</param>
        /// <param name="tag">A string identifying this stream for logging and debugging purposes</param>
        /// <param name="requestedSize">The initial requested size to prevent future allocations</param>
        /// <param name="initialLargeBuffer">An initial buffer to use. This buffer will be owned by the stream and returned to the memory manager upon Dispose.</param>
        internal RecyclableMemoryStream_(RecyclableMemoryStreamManager_ memoryManager, Guid id, string tag, long requestedSize, byte[] initialLargeBuffer)
            : base(emptyArray)
        {
            this.memoryManager = memoryManager;
            this.id = id;
            this.tag = tag;

            if (requestedSize < memoryManager.BlockSize)
            {
                requestedSize = memoryManager.BlockSize;
            }

            if (initialLargeBuffer == null)
            {
                this.EnsureCapacity(requestedSize);
            }
            else
            {
                this.largeBuffer = initialLargeBuffer;
            }

            if (this.memoryManager.GenerateCallStacks)
            {
                this.AllocationStack = Environment.StackTrace;
            }

            RecyclableMemoryStreamManager_.Events.Writer.MemoryStreamCreated(this.id, this.tag, requestedSize);
            this.memoryManager.ReportStreamCreated();
        }
        #endregion

        #region Dispose and Finalize
        /// <summary>
        /// The finalizer will be called when a stream is not disposed properly. 
        /// </summary>
        /// <remarks>Failing to dispose indicates a bug in the code using streams. Care should be taken to properly account for stream lifetime.</remarks>
        ~RecyclableMemoryStream_()
        {
            this.Dispose(false);
        }

        /// <summary>
        /// Returns the memory used by this stream back to the pool.
        /// </summary>
        /// <param name="disposing">Whether we're disposing (true), or being called by the finalizer (false)</param>
        [SuppressMessage("Microsoft.Usage", "CA1816:CallGCSuppressFinalizeCorrectly",
            Justification = "We have different disposal semantics, so SuppressFinalize is in a different spot.")]
        protected override void Dispose(bool disposing)
        {
            if (this.disposed)
            {
                string doubleDisposeStack = null;
                if (this.memoryManager.GenerateCallStacks)
                {
                    doubleDisposeStack = Environment.StackTrace;
                }

                RecyclableMemoryStreamManager_.Events.Writer.MemoryStreamDoubleDispose(this.id, this.tag,
                                                                                     this.AllocationStack,
                                                                                     this.DisposeStack,
                                                                                     doubleDisposeStack);
                return;
            }

            this.disposed = true;

            RecyclableMemoryStreamManager_.Events.Writer.MemoryStreamDisposed(this.id, this.tag);

            if (this.memoryManager.GenerateCallStacks)
            {
                this.DisposeStack = Environment.StackTrace;
            }

            if (disposing)
            {
                this.memoryManager.ReportStreamDisposed();

                GC.SuppressFinalize(this);
            }
            else
            {
                // We're being finalized.

                RecyclableMemoryStreamManager_.Events.Writer.MemoryStreamFinalized(this.id, this.tag, this.AllocationStack);

                if (AppDomain.CurrentDomain.IsFinalizingForUnload())
                {
                    // If we're being finalized because of a shutdown, don't go any further.
                    // We have no idea what's already been cleaned up. Triggering events may cause
                    // a crash.
                    base.Dispose(disposing);
                    return;
                }
                this.memoryManager.ReportStreamFinalized();
            }

            this.memoryManager.ReportStreamLength(this.length);

            if (this.largeBuffer != null)
            {
                this.memoryManager.ReturnLargeBuffer(this.largeBuffer, this.tag);
            }

            if (this.dirtyBuffers != null)
            {
                foreach (var buffer in this.dirtyBuffers)
                {
                    this.memoryManager.ReturnLargeBuffer(buffer, this.tag);
                }
            }

            this.memoryManager.ReturnBlocks(this.blocks, this.tag);
            this.blocks.Clear();

            base.Dispose(disposing);
        }

        /// <summary>
        /// Equivalent to Dispose
        /// </summary>
        public override void Close()
        {
            this.Dispose(true);
        }
        #endregion

        #region MemoryStream overrides
        /// <summary>
        /// Gets or sets the capacity
        /// </summary>
        /// <remarks>Capacity is always in multiples of the memory manager's block size, unless
        /// the large buffer is in use.  Capacity never decreases during a stream's lifetime. 
        /// Explicitly setting the capacity to a lower value than the current value will have no effect. 
        /// This is because the buffers are all pooled by chunks and there's little reason to 
        /// allow stream truncation.
        /// 
        /// Writing past the current capacity will cause Capacity to automatically increase, until MaximumStreamCapacity is reached.
        /// 
        /// If the capacity is larger than int.MaxValue, then Int.MaxValue will be returned. If you anticipate using
        /// larger streams, use the <see cref="Capacity64"/> property instead.
        /// </remarks>
        /// <exception cref="ObjectDisposedException">Object has been disposed</exception>
        public override int Capacity
        {
            get
            {
                this.CheckDisposed();
                if (this.largeBuffer != null)
                {
                    return this.largeBuffer.Length;
                }

                long size = (long)this.blocks.Count * this.memoryManager.BlockSize;
                if (size > int.MaxValue)
                {
                    throw new InvalidOperationException("Capacity is larger than int.MaxValue. Use Capacity64 instead");
                }
                return (int)size;
            }
            set
            {
                this.CheckDisposed();
                this.EnsureCapacity(value);
            }
        }
        
        /// <summary>
        /// Returns a 64-bit version of capacity, for streams larger than int.MaxValue in length.
        /// </summary>
        public long Capacity64
        {
            get
            {
                this.CheckDisposed();
                if (this.largeBuffer != null)
                {
                    return this.largeBuffer.Length;
                }

                long size = (long)this.blocks.Count * this.memoryManager.BlockSize;
                return size;
            }
            set
            {
                this.CheckDisposed();
                this.EnsureCapacity(value);
            }
        }

        private long length;

        /// <summary>
        /// Gets the number of bytes written to this stream.
        /// </summary>
        /// <exception cref="ObjectDisposedException">Object has been disposed</exception>
        /// <remarks>If the buffer has already been converted to a large buffer, then the maximum length is limited by the maximum allowed array length in .NET.</remarks>
        public override long Length
        {
            get
            {
                this.CheckDisposed();
                return this.length;
            }
        }

        private long position;

        /// <summary>
        /// Gets the current position in the stream
        /// </summary>
        /// <exception cref="ObjectDisposedException">Object has been disposed</exception>
        /// <exception cref="ArgumentOutOfRangeException">A negative value was passed</exception>
        /// <exception cref="InvalidOperationException">Stream is in large-buffer mode, but an attempt was made to set the position past the maximum allowed array length.</exception>
        /// <remarks>If the buffer has already been converted to a large buffer, then the maximum length (and thus position) is limited by the maximum allowed array length in .NET.</remarks>
        public override long Position
        {
            get
            {
                this.CheckDisposed();
                return this.position;
            }
            set
            {
                this.CheckDisposed();
                if (value < 0)
                {
                    throw new ArgumentOutOfRangeException("value", "value must be non-negative");
                }
                
                if (this.largeBuffer != null && value > RecyclableMemoryStreamManager_.MaxArrayLength)
                {
                    throw new InvalidOperationException($"Once the stream is converted to a single large buffer, position cannot be set past {RecyclableMemoryStreamManager_.MaxArrayLength}");
                }
                this.position = value;
            }
        }

        /// <summary>
        /// Whether the stream can currently read
        /// </summary>
        public override bool CanRead => !this.Disposed;

        /// <summary>
        /// Whether the stream can currently seek
        /// </summary>
        public override bool CanSeek => !this.Disposed;

        /// <summary>
        /// Always false
        /// </summary>
        public override bool CanTimeout => false;

        /// <summary>
        /// Whether the stream can currently write
        /// </summary>
        public override bool CanWrite => !this.Disposed;

        /// <summary>
        /// Returns a single buffer containing the contents of the stream.
        /// The buffer may be longer than the stream length.
        /// </summary>
        /// <returns>A byte[] buffer</returns>
        /// <remarks>IMPORTANT: Doing a Write() after calling GetBuffer() invalidates the buffer. The old buffer is held onto
        /// until Dispose is called, but the next time GetBuffer() is called, a new buffer from the pool will be required.</remarks>
        /// <exception cref="ObjectDisposedException">Object has been disposed</exception>
        /// <exception cref="InvalidOperationException">stream is too large for a contiguous buffer.</exception>
        public override byte[] GetBuffer()
        {
            this.CheckDisposed();

            if (this.largeBuffer != null)
            {
                return this.largeBuffer;
            }

            if (this.blocks.Count == 1)
            {
                return this.blocks[0];
            }

            // Buffer needs to reflect the capacity, not the length, because
            // it's possible that people will manipulate the buffer directly
            // and set the length afterward. Capacity sets the expectation
            // for the size of the buffer.

            if (this.Capacity64 > RecyclableMemoryStreamManager_.MaxArrayLength)
            {
                throw new InvalidOperationException("Stream is too large for a contiguous buffer.");
            }

            var newBuffer = this.memoryManager.GetLargeBuffer(this.Capacity64, this.tag);

            // InternalRead will check for existence of largeBuffer, so make sure we
            // don't set it until after we've copied the data.
            AssertLengthIsSmall();
            this.InternalRead(newBuffer, 0, (int)this.length, 0);
            this.largeBuffer = newBuffer;

            if (this.blocks.Count > 0 && this.memoryManager.AggressiveBufferReturn)
            {
                this.memoryManager.ReturnBlocks(this.blocks, this.tag);
                this.blocks.Clear();
            }

            return this.largeBuffer;
        }

        /// <summary>Asynchronously reads all the bytes from the current position in this stream and writes them to another stream.</summary>
        /// <param name="destination">The stream to which the contents of the current stream will be copied.</param>
        /// <param name="bufferSize">This parameter is ignored.</param>
        /// <param name="cancellationToken">The token to monitor for cancellation requests.</param>
        /// <returns>A task that represents the asynchronous copy operation.</returns>
        /// <exception cref="T:System.ArgumentNullException">
        ///   <paramref name="destination" /> is <see langword="null" />.</exception>
        /// <exception cref="T:System.ObjectDisposedException">Either the current stream or the destination stream is disposed.</exception>
        /// <exception cref="T:System.NotSupportedException">The current stream does not support reading, or the destination stream does not support writing.</exception>
        public override Task CopyToAsync(Stream destination, int bufferSize, CancellationToken cancellationToken)
        {
            if (destination == null)
            {
                throw new ArgumentNullException(nameof(destination));
            }

            CheckDisposed();

            if (this.length == 0)
            {
#if NET45
                return Task.FromResult(true);
#else
                return Task.CompletedTask;
#endif
            }

            if (destination is MemoryStream destinationRMS)
            {
                this.WriteTo(destinationRMS, this.position, this.length - this.position);
#if NET45
                return Task.FromResult(true);
#else
                return Task.CompletedTask;
#endif
            }
            else
            {
                if (this.largeBuffer == null)
                {
                    if (this.blocks.Count == 1)
                    {
                        AssertLengthIsSmall();
                        return destination.WriteAsync(this.blocks[0], (int)this.position, (int)(this.length - this.position), cancellationToken);
                    }
                    else
                    {
                        return CopyToAsyncImpl(cancellationToken);

                        async Task CopyToAsyncImpl(CancellationToken ct)
                        {                            
                            var bytesRemaining = this.length - this.position;
                            var blockAndOffset = this.GetBlockAndRelativeOffset(this.position);
                            int currentBlock = blockAndOffset.Block;
                            var currentOffset = blockAndOffset.Offset;
                            while (bytesRemaining > 0)
                            {
                                int amountToCopy = (int)Math.Min(this.blocks[currentBlock].Length - currentOffset, bytesRemaining);
                                await destination.WriteAsync(this.blocks[currentBlock], currentOffset, amountToCopy, ct);
                                bytesRemaining -= amountToCopy;
                                ++currentBlock;
                                currentOffset = 0;
                            }
                        }
                    }
                }
                else
                {
                    AssertLengthIsSmall();
                    return destination.WriteAsync(this.largeBuffer, (int)this.position, (int)(this.length - this.position), cancellationToken);
                }
            }
        }

#if NETCOREAPP2_1 || NETSTANDARD2_1
        /// <summary>
        /// Returns a sequence containing the contents of the stream.
        /// </summary>
        /// <returns>A ReadOnlySequence of bytes</returns>
        /// <remarks>IMPORTANT: Doing a Write(), Dispose(), or Close() after calling GetReadOnlySequence() invalidates the sequence.</remarks>
        /// <exception cref="ObjectDisposedException">Object has been disposed</exception>
        public ReadOnlySequence<byte> GetReadOnlySequence()
        {
            this.CheckDisposed();

            if (this.largeBuffer != null)
            {
                AssertLengthIsSmall();
                return new ReadOnlySequence<byte>(this.largeBuffer, 0, (int)this.length);
            }

            if (this.blocks.Count == 1)
            {
            AssertLengthIsSmall();
                return new ReadOnlySequence<byte>(this.blocks[0], 0, (int)this.length);
            }

            if (this.length > RecyclableMemoryStreamManager.MaxArrayLength)
            {
                throw new InvalidOperationException($"Cannot return a ReadOnlySequence larger than {RecyclableMemoryStreamManager.MaxArrayLength}, but stream length is {this.length}.");
            }

            BlockSegment first = new BlockSegment(this.blocks[0]);
            BlockSegment last = first;

            
            for (int blockIdx = 1; blockIdx < blocks.Count; blockIdx++)
            {
                last = last.Append(this.blocks[blockIdx]);
            }

            Debug.Assert(this.length <= Int32.MaxValue);
            return new ReadOnlySequence<byte>(first, 0, last, (int)this.length - (int)last.RunningIndex);
        }

        private sealed class BlockSegment : ReadOnlySequenceSegment<byte>
        {
            public BlockSegment(Memory<byte> memory) => Memory = memory;

            public BlockSegment Append(Memory<byte> memory)
            {
                var nextSegment = new BlockSegment(memory) { RunningIndex = RunningIndex + Memory.Length };
                Next = nextSegment;
                return nextSegment;
            }
        }
#endif

        /// <summary>
        /// Returns an ArraySegment that wraps a single buffer containing the contents of the stream.
        /// </summary>
        /// <param name="buffer">An ArraySegment containing a reference to the underlying bytes.</param>
        /// <returns>Always returns true.</returns>
        /// <remarks>GetBuffer has no failure modes (it always returns something, even if it's an empty buffer), therefore this method
        /// always returns a valid ArraySegment to the same buffer returned by GetBuffer.</remarks>
#if NET45
        public bool TryGetBuffer(out ArraySegment<byte> buffer)  
#else
        public override bool TryGetBuffer(out ArraySegment<byte> buffer)
#endif
        {
            this.CheckDisposed();
            Debug.Assert(this.length <= Int32.MaxValue);
            buffer = new ArraySegment<byte>(this.GetBuffer(), 0, (int)this.Length);
            // GetBuffer has no failure modes, so this should always succeed
            return true;
        }

        /// <summary>
        /// Returns a new array with a copy of the buffer's contents. You should almost certainly be using GetBuffer combined with the Length to 
        /// access the bytes in this stream. Calling ToArray will destroy the benefits of pooled buffers, but it is included
        /// for the sake of completeness.
        /// </summary>
        /// <exception cref="ObjectDisposedException">Object has been disposed</exception>
        /// <exception cref="NotSupportedException">The current RecyclableStreamManager object disallows ToArray calls.</exception>
#pragma warning disable CS0809
        [Obsolete("This method has degraded performance vs. GetBuffer and should be avoided.")]
        public override byte[] ToArray()
        {
            this.CheckDisposed();

            string stack = this.memoryManager.GenerateCallStacks ? Environment.StackTrace : null;
            RecyclableMemoryStreamManager_.Events.Writer.MemoryStreamToArray(this.id, this.tag, stack, this.length);

            if (this.memoryManager.ThrowExceptionOnToArray)
            {
                throw new NotSupportedException("The underlying RecyclableMemoryStreamManager is configured to not allow calls to ToArray.");
            }

            var newBuffer = new byte[this.Length];

            Debug.Assert(this.length <= Int32.MaxValue);
            this.InternalRead(newBuffer, 0, (int)this.length, 0);
            this.memoryManager.ReportStreamToArray();

            return newBuffer;
        }
#pragma warning restore CS0809

        /// <summary>
        /// Reads from the current position into the provided buffer
        /// </summary>
        /// <param name="buffer">Destination buffer</param>
        /// <param name="offset">Offset into buffer at which to start placing the read bytes.</param>
        /// <param name="count">Number of bytes to read.</param>
        /// <returns>The number of bytes read</returns>
        /// <exception cref="ArgumentNullException">buffer is null</exception>
        /// <exception cref="ArgumentOutOfRangeException">offset or count is less than 0</exception>
        /// <exception cref="ArgumentException">offset subtracted from the buffer length is less than count</exception>
        /// <exception cref="ObjectDisposedException">Object has been disposed</exception>
        public override int Read(byte[] buffer, int offset, int count)
        {
            return this.SafeRead(buffer, offset, count, ref this.position);
        }

        /// <summary>
        /// Reads from the specified position into the provided buffer
        /// </summary>
        /// <param name="buffer">Destination buffer</param>
        /// <param name="offset">Offset into buffer at which to start placing the read bytes.</param>
        /// <param name="count">Number of bytes to read.</param>
        /// <param name="streamPosition">Position in the stream to start reading from</param>
        /// <returns>The number of bytes read</returns>
        /// <exception cref="ArgumentNullException">buffer is null</exception>
        /// <exception cref="ArgumentOutOfRangeException">offset or count is less than 0</exception>
        /// <exception cref="ArgumentException">offset subtracted from the buffer length is less than count</exception>
        /// <exception cref="ObjectDisposedException">Object has been disposed</exception>
        /// <exception cref="InvalidOperationException">Stream position is beyond int.MaxValue</exception>
        public int SafeRead(byte[] buffer, int offset, int count, ref int streamPosition)
        {
            long longPosition = streamPosition;
            var retVal = this.SafeRead(buffer, offset, count, ref longPosition);
            if (longPosition > int.MaxValue)
            {
                throw new InvalidOperationException("Stream position is beyond int.MaxValue. Use SafeRead(byte[], int, int, ref long) override.");
            }
            streamPosition = (int)longPosition;
            return retVal;
        }

        /// <summary>
        /// Reads from the specified position into the provided buffer
        /// </summary>
        /// <param name="buffer">Destination buffer</param>
        /// <param name="offset">Offset into buffer at which to start placing the read bytes.</param>
        /// <param name="count">Number of bytes to read.</param>
        /// <param name="streamPosition">Position in the stream to start reading from</param>
        /// <returns>The number of bytes read</returns>
        /// <exception cref="ArgumentNullException">buffer is null</exception>
        /// <exception cref="ArgumentOutOfRangeException">offset or count is less than 0</exception>
        /// <exception cref="ArgumentException">offset subtracted from the buffer length is less than count</exception>
        /// <exception cref="ObjectDisposedException">Object has been disposed</exception>
        public int SafeRead(byte[] buffer, int offset, int count, ref long streamPosition)
        {
            this.CheckDisposed();
            if (buffer == null)
            {
                throw new ArgumentNullException(nameof(buffer));
            }

            if (offset < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(offset), "offset cannot be negative");
            }

            if (count < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(count), "count cannot be negative");
            }

            if (offset + count > buffer.Length)
            {
                throw new ArgumentException("buffer length must be at least offset + count");
            }

            int amountRead = this.InternalRead(buffer, offset, count, streamPosition);
            streamPosition += amountRead;
            return amountRead;
        }



#if NETCOREAPP2_1 || NETSTANDARD2_1
        /// <summary>
        /// Reads from the current position into the provided buffer
        /// </summary>
        /// <param name="buffer">Destination buffer</param>
        /// <returns>The number of bytes read</returns>
        /// <exception cref="ObjectDisposedException">Object has been disposed</exception>
        public override int Read(Span<byte> buffer)
        {
            return this.SafeRead(buffer, ref this.position);
        }

        /// <summary>
        /// Reads from the specified position into the provided buffer
        /// </summary>
        /// <param name="buffer">Destination buffer</param>
        /// <param name="streamPosition">Position in the stream to start reading from</param>
        /// <returns>The number of bytes read</returns>
        /// <exception cref="ObjectDisposedException">Object has been disposed</exception>
        /// <exception cref="InvalidOperationException">Stream position is beyond int.MaxValue</exception>
        public int SafeRead(Span<byte> buffer, ref int streamPosition)
        {
            long longPosition = streamPosition;
            int retVal = this.SafeRead(buffer, ref longPosition);
            if (longPosition > int.MaxValue)
            {
                throw new InvalidOperationException("Stream position is beyond int.MaxValue. Use SafeRead(Span<byte>, ref long) override.");
            }
            streamPosition = (int)longPosition;
            return retVal;
        }

        /// <summary>
        /// Reads from the specified position into the provided buffer
        /// </summary>
        /// <param name="buffer">Destination buffer</param>
        /// <param name="streamPosition">Position in the stream to start reading from</param>
        /// <returns>The number of bytes read</returns>
        /// <exception cref="ObjectDisposedException">Object has been disposed</exception>
        public int SafeRead(Span<byte> buffer, ref long streamPosition)
        {
            this.CheckDisposed();

            int amountRead = this.InternalRead(buffer, streamPosition);
            streamPosition += amountRead;
            return amountRead;
        }
        
#endif

        /// <summary>
        /// Writes the buffer to the stream
        /// </summary>
        /// <param name="buffer">Source buffer</param>
        /// <param name="offset">Start position</param>
        /// <param name="count">Number of bytes to write</param>
        /// <exception cref="ArgumentNullException">buffer is null</exception>
        /// <exception cref="ArgumentOutOfRangeException">offset or count is negative</exception>
        /// <exception cref="ArgumentException">buffer.Length - offset is not less than count</exception>
        /// <exception cref="ObjectDisposedException">Object has been disposed</exception>
        public override void Write(byte[] buffer, int offset, int count)
        {
            this.CheckDisposed();
            if (buffer == null)
            {
                throw new ArgumentNullException(nameof(buffer));
            }

            if (offset < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(offset), offset,
                                                      "Offset must be in the range of 0 - buffer.Length-1");
            }

            if (count < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(count), count, "count must be non-negative");
            }

            if (count + offset > buffer.Length)
            {
                throw new ArgumentException("count must be greater than buffer.Length - offset");
            }

            int blockSize = this.memoryManager.BlockSize;
            long end = (long)this.position + count;

            this.EnsureCapacity(end);

            if (this.largeBuffer == null)
            {
                int bytesRemaining = count;
                int bytesWritten = 0;
                var blockAndOffset = this.GetBlockAndRelativeOffset(this.position);

                while (bytesRemaining > 0)
                {
                    byte[] currentBlock = this.blocks[blockAndOffset.Block];
                    int remainingInBlock = blockSize - blockAndOffset.Offset;
                    int amountToWriteInBlock = Math.Min(remainingInBlock, bytesRemaining);

                    Buffer.BlockCopy(buffer, offset + bytesWritten, currentBlock, blockAndOffset.Offset,
                                     amountToWriteInBlock);

                    bytesRemaining -= amountToWriteInBlock;
                    bytesWritten += amountToWriteInBlock;

                    ++blockAndOffset.Block;
                    blockAndOffset.Offset = 0;
                }
            }
            else
            {
                Buffer.BlockCopy(buffer, offset, this.largeBuffer, (int)this.position, count);
            }
            this.position = end;
            this.length = Math.Max(this.position, this.length);
        }

#if NETCOREAPP2_1 || NETSTANDARD2_1
        /// <summary>
        /// Writes the buffer to the stream
        /// </summary>
        /// <param name="source">Source buffer</param>
        /// <exception cref="ArgumentNullException">buffer is null</exception>
        /// <exception cref="ObjectDisposedException">Object has been disposed</exception>
        public override void Write(ReadOnlySpan<byte> source)
        {
            this.CheckDisposed();

            int blockSize = this.memoryManager.BlockSize;
            long end = (long)this.position + source.Length;

            this.EnsureCapacity(end);

            if (this.largeBuffer == null)
            {
                var blockAndOffset = this.GetBlockAndRelativeOffset(this.position);

                while (source.Length > 0)
                {
                    byte[] currentBlock = this.blocks[blockAndOffset.Block];
                    int remainingInBlock = blockSize - blockAndOffset.Offset;
                    int amountToWriteInBlock = Math.Min(remainingInBlock, source.Length);

                    source.Slice(0, amountToWriteInBlock)
                        .CopyTo(currentBlock.AsSpan(blockAndOffset.Offset));

                    source = source.Slice(amountToWriteInBlock);

                    ++blockAndOffset.Block;
                    blockAndOffset.Offset = 0;
                }
            }
            else
            {
                source.CopyTo(this.largeBuffer.AsSpan((int)this.position));
            }
            this.position = end;
            this.length = Math.Max(this.position, this.length);
        }
#endif

        /// <summary>
        /// Returns a useful string for debugging. This should not normally be called in actual production code.
        /// </summary>
        public override string ToString()
        {
            return $"Id = {this.Id}, Tag = {this.Tag}, Length = {this.Length:N0} bytes";
        }

        /// <summary>
        /// Writes a single byte to the current position in the stream.
        /// </summary>
        /// <param name="value">byte value to write</param>
        /// <exception cref="ObjectDisposedException">Object has been disposed</exception>
        public override void WriteByte(byte value)
        {
            this.CheckDisposed();

            long end = (long)this.position + 1;

            if (this.largeBuffer == null)
            {
                var blockSize = this.memoryManager.BlockSize;

                var block = (int)(this.position / blockSize);

                if (block >= this.blocks.Count)
                {
                    this.EnsureCapacity(end);
                }

                this.blocks[block][this.position % blockSize] = value;
            }
            else
            {
                if (this.position >= this.largeBuffer.Length)
                {
                    this.EnsureCapacity(end);
                }

                this.largeBuffer[this.position] = value;
            }

            this.position = end;

            if (this.position > this.length)
            {
                this.length = this.position;
            }
        }

        /// <summary>
        /// Reads a single byte from the current position in the stream.
        /// </summary>
        /// <returns>The byte at the current position, or -1 if the position is at the end of the stream.</returns>
        /// <exception cref="ObjectDisposedException">Object has been disposed</exception>
        public override int ReadByte()
        {
            return this.SafeReadByte(ref this.position);
        }

        /// <summary>
        /// Reads a single byte from the specified position in the stream.
        /// </summary>
        /// <param name="streamPosition">The position in the stream to read from</param>
        /// <returns>The byte at the current position, or -1 if the position is at the end of the stream.</returns>
        /// <exception cref="ObjectDisposedException">Object has been disposed</exception>
        /// <exception cref="InvalidOperationException">Stream position is beyond int.MaxValue</exception>
        public int SafeReadByte(ref int streamPosition)
        {
            long longPosition = streamPosition;
            int retVal = this.SafeReadByte(ref longPosition);
            if (longPosition > int.MaxValue)
            {
                throw new InvalidOperationException("Stream position is beyond int.MaxValue. Use SafeReadByte(ref long) override.");
            }
            streamPosition = (int)longPosition;
            return retVal;
        }

        /// <summary>
        /// Reads a single byte from the specified position in the stream.
        /// </summary>
        /// <param name="streamPosition">The position in the stream to read from</param>
        /// <returns>The byte at the current position, or -1 if the position is at the end of the stream.</returns>
        /// <exception cref="ObjectDisposedException">Object has been disposed</exception>
        public int SafeReadByte(ref long streamPosition)
        {
            this.CheckDisposed();
            if (streamPosition == this.length)
            {
                return -1;
            }
            byte value;
            if (this.largeBuffer == null)
            {
                var blockAndOffset = this.GetBlockAndRelativeOffset(streamPosition);
                value = this.blocks[blockAndOffset.Block][blockAndOffset.Offset];
            }
            else
            {
                value = this.largeBuffer[streamPosition];
            }
            streamPosition++;
            return value;
        }

        /// <summary>
        /// Sets the length of the stream
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException">value is negative or larger than MaxStreamLength</exception>
        /// <exception cref="ObjectDisposedException">Object has been disposed</exception>
        public override void SetLength(long value)
        {
            this.CheckDisposed();
            if (value < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(value), "value must be non-negative");
            }

            this.EnsureCapacity(value);

            this.length = value;
            if (this.position > value)
            {
                this.position = value;
            }
        }

        /// <summary>
        /// Sets the position to the offset from the seek location
        /// </summary>
        /// <param name="offset">How many bytes to move</param>
        /// <param name="loc">From where</param>
        /// <returns>The new position</returns>
        /// <exception cref="ObjectDisposedException">Object has been disposed</exception>
        /// <exception cref="ArgumentOutOfRangeException">offset is larger than MaxStreamLength</exception>
        /// <exception cref="ArgumentException">Invalid seek origin</exception>
        /// <exception cref="IOException">Attempt to set negative position</exception>
        public override long Seek(long offset, SeekOrigin loc)
        {
            this.CheckDisposed();
            
            long newPosition;
            switch (loc)
            {
            case SeekOrigin.Begin:
                newPosition = offset;
                break;
            case SeekOrigin.Current:
                newPosition = offset + this.position;
                break;
            case SeekOrigin.End:
                newPosition = offset + this.length;
                break;
            default:
                throw new ArgumentException("Invalid seek origin", nameof(loc));
            }
            if (newPosition < 0)
            {
                throw new IOException("Seek before beginning");
            }
            this.position = newPosition;
            return this.position;
        }

        /// <summary>
        /// Synchronously writes this stream's bytes to the argument stream.
        /// </summary>
        /// <param name="stream">Destination stream</param>
        /// <remarks>Important: This does a synchronous write, which may not be desired in some situations</remarks>
        /// <exception cref="ArgumentNullException">stream is null</exception>
        public override void WriteTo(Stream stream)
        {
            this.WriteTo(stream, 0, this.length);
        }

        /// <summary>
        /// Synchronously writes this stream's bytes, starting at offset, for count bytes, to the argument stream.
        /// </summary>
        /// <param name="stream">Destination stream</param>
        /// <param name="offset">Offset in source</param>
        /// <param name="count">Number of bytes to write</param>
        /// <exception cref="ArgumentNullException">stream is null</exception>
        /// <exception cref="ArgumentOutOfRangeException">Offset is less than 0, or offset + count is beyond  this stream's length.</exception>
        public void WriteTo(Stream stream, int offset, int count)
        {
            this.WriteTo(stream, (long)offset, (long)count);
        }

        /// <summary>
        /// Synchronously writes this stream's bytes, starting at offset, for count bytes, to the argument stream.
        /// </summary>
        /// <param name="stream">Destination stream</param>
        /// <param name="offset">Offset in source</param>
        /// <param name="count">Number of bytes to write</param>
        /// <exception cref="ArgumentNullException">stream is null</exception>
        /// <exception cref="ArgumentOutOfRangeException">Offset is less than 0, or offset + count is beyond  this stream's length.</exception>
        public void WriteTo(Stream stream, long offset, long count)
        {
            this.CheckDisposed();
            if (stream == null)
            {
                throw new ArgumentNullException(nameof(stream));
            }

            if (offset < 0 || offset + count > this.length)
            {
                throw new ArgumentOutOfRangeException(message: "offset must not be negative and offset + count must not exceed the length of the stream", innerException: null);
            }

            if (this.largeBuffer == null)
            {
                var blockAndOffset = GetBlockAndRelativeOffset(offset);
                long bytesRemaining = count;
                int currentBlock = blockAndOffset.Block;
                int currentOffset = blockAndOffset.Offset;

                while (bytesRemaining > 0)
                {
                    int amountToCopy = (int)Math.Min((long)this.blocks[currentBlock].Length - currentOffset, bytesRemaining);
                    stream.Write(this.blocks[currentBlock], currentOffset, amountToCopy);

                    bytesRemaining -= amountToCopy;

                    ++currentBlock;
                    currentOffset = 0;
                }
            }
            else
            {
                stream.Write(this.largeBuffer, (int)offset, (int)count);
            }
        }
        #endregion

        #region Helper Methods
        private bool Disposed => this.disposed;

        [MethodImpl((MethodImplOptions)256)]
        private void CheckDisposed()
        {
            if (this.Disposed)
            {
                this.ThrowDisposedException();
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void ThrowDisposedException()
        {
            throw new ObjectDisposedException($"The stream with Id {this.id} and Tag {this.tag} is disposed.");
        }

        private int InternalRead(byte[] buffer, int offset, int count, long fromPosition)
        {
            if (this.length - fromPosition <= 0)
            {
                return 0;
            }

            int amountToCopy;

            if (this.largeBuffer == null)
            {
                var blockAndOffset = this.GetBlockAndRelativeOffset(fromPosition);
                int bytesWritten = 0;
                int bytesRemaining = (int)Math.Min((long)count, this.length - fromPosition);

                while (bytesRemaining > 0)
                {
                    amountToCopy = Math.Min(this.blocks[blockAndOffset.Block].Length - blockAndOffset.Offset,
                                                bytesRemaining);
                    Buffer.BlockCopy(this.blocks[blockAndOffset.Block], blockAndOffset.Offset, buffer,
                                     bytesWritten + offset, amountToCopy);

                    bytesWritten += amountToCopy;
                    bytesRemaining -= amountToCopy;

                    ++blockAndOffset.Block;
                    blockAndOffset.Offset = 0;
                }
                return bytesWritten;
            }
            amountToCopy = (int)Math.Min((long)count, this.length - fromPosition);
            Buffer.BlockCopy(this.largeBuffer, (int)fromPosition, buffer, offset, amountToCopy);
            return amountToCopy;
        }

#if NETCOREAPP2_1 || NETSTANDARD2_1
        private int InternalRead(Span<byte> buffer, long fromPosition)
        {
            if (this.length - fromPosition <= 0)
            {
                return 0;
            }
            
            int amountToCopy;

            if (this.largeBuffer == null)
            {
                var blockAndOffset = this.GetBlockAndRelativeOffset(fromPosition);
                int bytesWritten = 0;
                int bytesRemaining = (int)Math.Min(buffer.Length, this.length - fromPosition);

                while (bytesRemaining > 0)
                {
                    amountToCopy = Math.Min(this.blocks[blockAndOffset.Block].Length - blockAndOffset.Offset,
                                            bytesRemaining);
                    this.blocks[blockAndOffset.Block].AsSpan(blockAndOffset.Offset, amountToCopy)
                        .CopyTo(buffer.Slice(bytesWritten));

                    bytesWritten += amountToCopy;
                    bytesRemaining -= amountToCopy;

                    ++blockAndOffset.Block;
                    blockAndOffset.Offset = 0;
                }
                return bytesWritten;
            }
            amountToCopy = (int)Math.Min((long)buffer.Length, this.length - fromPosition);
            this.largeBuffer.AsSpan((int)fromPosition, amountToCopy).CopyTo(buffer);
            return amountToCopy;
        }
#endif

        private struct BlockAndOffset
        {
            public int Block;
            public int Offset;

            public BlockAndOffset(int block, int offset)
            {
                this.Block = block;
                this.Offset = offset;
            }
        }

        [MethodImpl((MethodImplOptions)256)]
        private BlockAndOffset GetBlockAndRelativeOffset(long offset)
        {
            var blockSize = this.memoryManager.BlockSize;
            int blockIndex = (int)(offset / blockSize);
            int offsetIndex = (int)(offset % blockSize);
            return new BlockAndOffset(blockIndex, offsetIndex);
        }

        private void EnsureCapacity(long newCapacity)
        {
            if (newCapacity > this.memoryManager.MaximumStreamCapacity && this.memoryManager.MaximumStreamCapacity > 0)
            {
                RecyclableMemoryStreamManager_.Events.Writer.MemoryStreamOverCapacity(newCapacity,
                                                                                    this.memoryManager
                                                                                        .MaximumStreamCapacity, this.tag,
                                                                                    this.AllocationStack);
                throw new InvalidOperationException("Requested capacity is too large: " + newCapacity + ". Limit is " +
                                                    this.memoryManager.MaximumStreamCapacity);
            }

            if (this.largeBuffer != null)
            {
                if (newCapacity > this.largeBuffer.Length)
                {
                    var newBuffer = this.memoryManager.GetLargeBuffer(newCapacity, this.tag);
                    Debug.Assert(this.length <= Int32.MaxValue);
                    this.InternalRead(newBuffer, 0, (int)this.length, 0);
                    this.ReleaseLargeBuffer();
                    this.largeBuffer = newBuffer;
                }
            }
            else
            {
                while (this.Capacity64 < newCapacity)
                {
                    blocks.Add((this.memoryManager.GetBlock()));
                }
            }
        }

        /// <summary>
        /// Release the large buffer (either stores it for eventual release or returns it immediately).
        /// </summary>
        private void ReleaseLargeBuffer()
        {
            if (this.memoryManager.AggressiveBufferReturn)
            {
                this.memoryManager.ReturnLargeBuffer(this.largeBuffer, this.tag);
            }
            else
            {
                if (this.dirtyBuffers == null)
                {
                    // We most likely will only ever need space for one
                    this.dirtyBuffers = new List<byte[]>(1);
                }
                this.dirtyBuffers.Add(this.largeBuffer);
            }

            this.largeBuffer = null;
        }
#if !NET40
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
#endif
        private void AssertLengthIsSmall()
        {
            Debug.Assert(this.length <= Int32.MaxValue, "this.length was assumed to be <= Int32.MaxValue, but was larger.");
        }
        #endregion
    }
}