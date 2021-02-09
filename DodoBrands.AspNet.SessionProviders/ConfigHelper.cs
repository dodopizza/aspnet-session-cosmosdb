using System;
using System.Collections.Specialized;
using System.Configuration;

namespace DodoBrands.AspNet.SessionProviders
{
    internal static class ConfigHelper
    {
        public static int GetInt32(NameValueCollection config, string key, int defaultValue)
        {
            var value = defaultValue;
            var configValue = config[key];
            if (configValue != null && !int.TryParse(configValue, out value))
            {
                throw new ConfigurationErrorsException("lockTtlSeconds parameter can not be parsed.");
            }

            return value;
        }

        public static bool GetBoolean(NameValueCollection config, string key, bool defaultValue)
        {
            var value = defaultValue;
            var configValue = config[key];
            if (configValue != null && !bool.TryParse(configValue, out value))
            {
                throw new ConfigurationErrorsException("lockTtlSeconds parameter can not be parsed.");
            }

            return value;
        }

        public static T GetEnum<T>(NameValueCollection nameValueCollection, string propertyName, T defaultValue) where T:struct
        {
            if (!typeof(T).IsEnum)
            {
                throw new ArgumentException($"Expected a enum, got: {typeof(T)}");
            }

            var value = nameValueCollection[propertyName];
            if (string.IsNullOrWhiteSpace(value))
            {
                return defaultValue;
            }
            if (!Enum.TryParse(value, out T result))
            {
                throw new InvalidOperationException($"Can not parse {propertyName} as {typeof(T)}");
            }

            return result;
        }
    }
}