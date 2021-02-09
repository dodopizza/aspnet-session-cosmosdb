using System.IO;
using System.Web;
using System.Web.SessionState;

namespace DodoBrands.AspNet.SessionProviders
{
    internal static class SessionSerializationUtil
    {
        public static byte[] Write(this SessionStateValue stateValue, bool compressed)
        {
            return compressed
                ? GenericSerializationUtil.BinaryWriterOperationToByteBufferWithCompression(stateValue,
                    SerializeSessionState)
                : GenericSerializationUtil.BinaryWriterOperationToByteBuffer(stateValue, SerializeSessionState);
        }

        public static SessionStateValue ReadSessionState(this byte[] source, bool compressed)
        {
            return compressed
                ? GenericSerializationUtil.ByteBufferToReaderOperationWithCompression(source, DeserializeSessionState)
                : GenericSerializationUtil.ByteBufferToReaderOperation(source, DeserializeSessionState);
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