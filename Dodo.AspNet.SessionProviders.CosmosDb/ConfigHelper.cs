using System;
using System.Collections.Specialized;
using System.Configuration;

namespace Dodo.AspNet.SessionProviders.CosmosDb
{
    internal static class ConfigHelper
    {
        public static int GetInt32(this NameValueCollection config, string key, int defaultValue)
        {
            var value = defaultValue;
            var configValue = config[key];
            if (configValue != null && !int.TryParse(configValue, out value))
            {
                throw new ConfigurationErrorsException("lockTtlSeconds parameter can not be parsed.");
            }

            return value;
        }

        public static bool GetBoolean(this NameValueCollection config, string key, bool defaultValue)
        {
            var value = defaultValue;
            var configValue = config[key];
            if (configValue != null && !bool.TryParse(configValue, out value))
            {
                throw new ConfigurationErrorsException("lockTtlSeconds parameter can not be parsed.");
            }

            return value;
        }

        public static T GetEnum<T>(this NameValueCollection config, string propertyName, T defaultValue) where T:struct
        {
            if (!typeof(T).IsEnum)
            {
                throw new ArgumentException($"Expected a enum, got: {typeof(T)}");
            }

            var value = config[propertyName];
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

        public static string GetRequiredString(this NameValueCollection config, string name)
        {
            var value = config[name];
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new ConfigurationErrorsException($"{name} is not specified.");
            }

            return value;
        }
    }
}