using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Web;
using System.Web.SessionState;
using Microsoft.IO;

namespace DodoBrands.CosmosDbSessionProvider
{
    public static class SerializationUtil
    {
        private static readonly RecyclableMemoryStreamManager_ StreamManager = new RecyclableMemoryStreamManager_();

        public static byte[] Write(this SessionStateValue stateValue, bool compressed)
        {
            return compressed
                ? BinaryWriterOperationToByteBufferWithCompression(stateValue, SerializeSessionState)
                : BinaryWriterOperationToByteBuffer(stateValue, SerializeSessionState);
        }

        public static SessionStateValue ReadSessionState(this byte[] source, bool compressed)
        {
            return compressed
                ? ByteBufferToReaderOperationWithCompression(source, DeserializeSessionState)
                : ByteBufferToReaderOperation(source, DeserializeSessionState);
        }

        private static byte[] BinaryWriterOperationToByteBuffer<T>(T state, Action<BinaryWriter, T> write)
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

        private static byte[] BinaryWriterOperationToByteBufferWithCompression<T>(T state,
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

        private static T ByteBufferToReaderOperation<T>(byte[] source, Func<BinaryReader, T> read)
        {
            using (var stream = StreamManager.GetStream(source))
            {
                using (var reader = new BinaryReader(stream, Encoding.Default))
                {
                    return read(reader);
                }
            }
        }

        private static T ByteBufferToReaderOperationWithCompression<T>(byte[] source, Func<BinaryReader, T> read)
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

        private static void SerializeSessionState(BinaryWriter writer, SessionStateValue s)
        {
            writer.Write(s.Timeout);
            var hasSessionItems = s.SessionItems != null && s.SessionItems.Count != 0;
            writer.Write(hasSessionItems);
            var hasStaticObjects = s.StaticObjects != null && s.StaticObjects.Count != 0;
            writer.Write(hasStaticObjects);
            if (hasSessionItems)
            {
                s.SessionItems.Serialize(writer);
            }

            if (hasStaticObjects)
            {
                s.StaticObjects.Serialize(writer);
            }
        }

        private static SessionStateValue DeserializeSessionState(BinaryReader reader)
        {
            var timeout = reader.ReadInt32();
            var hasSessionItems = reader.ReadBoolean();
            var hasStaticObjects = reader.ReadBoolean();
            return new SessionStateValue(
                hasSessionItems ? SessionStateItemCollection.Deserialize(reader) : null,
                hasStaticObjects ? HttpStaticObjectsCollection.Deserialize(reader) : null,
                timeout);
        }
    }
}