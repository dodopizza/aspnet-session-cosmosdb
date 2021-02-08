using System.Collections.Specialized;
using System.Configuration;

namespace DodoBrands.CosmosDbSessionProvider
{
    public static class ConfigHelper
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
    }
}