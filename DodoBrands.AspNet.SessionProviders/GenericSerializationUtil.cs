using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using Microsoft.IO;

namespace DodoBrands.AspNet.SessionProviders
{
    public static class GenericSerializationUtil
    {
        private static readonly RecyclableMemoryStreamManager_ StreamManager = new RecyclableMemoryStreamManager_();

        public static byte[] BinaryWriterOperationToByteBuffer<T>(T state, Action<BinaryWriter, T> write)
        {
            using (var stream = StreamManager.GetStream())
            {
                using (var writer = new BinaryWriter(stream, Encoding.Default, true))
                {
                    write(writer, state);
                    writer.Flush();
                }

                var plainData = stream.ToArray();

                return plainData;
            }
        }

        public static byte[] BinaryWriterOperationToByteBufferWithCompression<T>(T state,
            Action<BinaryWriter, T> write)
        {
            using (var plainStream = StreamManager.GetStream())
            {
                using (var writer = new BinaryWriter(plainStream, Encoding.Default, true))
                {
                    write(writer, state);
                    writer.Flush();
                }

                using (var gzipBackendStream = StreamManager.GetStream())
                {
                    using (var zip = new GZipStream(gzipBackendStream, CompressionLevel.Optimal, true))
                    {
                        plainStream.Position = 0;
                        plainStream.CopyTo(zip, 4096);
                        zip.Flush();
                    }

                    return gzipBackendStream.ToArray();
                }
            }
        }

        public static T ByteBufferToReaderOperation<T>(byte[] source, Func<BinaryReader, T> read)
        {
            using (var stream = StreamManager.GetStream(source))
            {
                using (var reader = new BinaryReader(stream, Encoding.Default))
                {
                    return read(reader);
                }
            }
        }

        public static T ByteBufferToReaderOperationWithCompression<T>(byte[] source, Func<BinaryReader, T> read)
        {
            using (var stream = StreamManager.GetStream(source))
            {
                using (var zip = new GZipStream(stream, CompressionMode.Decompress, true))
                {
                    using (var reader = new BinaryReader(zip, Encoding.Default, true))
                    {
                        return read(reader);
                    }
                }
            }
        }
    }
}