using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Web;
using System.Web.SessionState;
using DodoBrands.CosmosDbSessionProvider.Cosmos;

namespace DodoBrands.CosmosDbSessionProvider
{
    public static class SerializationUtil
    {
        public static byte[] Write(this SessionStateValue stateValue)
        {
            return BinaryWriterOperationToByteBuffer(stateValue, SerializeSessionState);
        }

        public static SessionStateValue ReadSessionState(this byte[] source)
        {
            return ByteBufferToReaderOperation(source, DeserializeSessionState);
        }
        
        public static byte[] BinaryWriterOperationToByteBuffer<T>(T state, Action<BinaryWriter, T> write)
        {
            using (var stream = new MemoryStream())
            {
                using (var writer = new BinaryWriter(stream, Encoding.Default, leaveOpen: true))
                {
                    write(writer, state);
                    writer.Flush();
                }
                stream.Position = 0;
                var plainData = stream.ToArray();
                
                stream.SetLength(0);
                using (var zip = new GZipStream(stream, CompressionLevel.Optimal, leaveOpen: true))
                {
                    zip.Write(plainData, 0, plainData.Length);
                    zip.Flush();
                }

                stream.Position = 0;
                return stream.ToArray();
            }
        }

        private static T ByteBufferToReaderOperation<T>(byte[] source, Func<BinaryReader, T> read)
        {
            using (var stream = new MemoryStream(source))
            {
                using (var zip = new GZipStream(stream, CompressionMode.Decompress, leaveOpen: true))
                {
                    using (var reader = new BinaryReader(zip, Encoding.Default, leaveOpen: true))
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
            int timeout = reader.ReadInt32();
            var hasSessionItems = reader.ReadBoolean();
            bool hasStaticObjects = reader.ReadBoolean();
            return new SessionStateValue(
                hasSessionItems ? SessionStateItemCollection.Deserialize(reader) : null,
                hasStaticObjects ? HttpStaticObjectsCollection.Deserialize(reader) : null,
                timeout);
        }
    }
}