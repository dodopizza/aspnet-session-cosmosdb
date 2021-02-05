using System;
using System.Collections.Specialized;
using System.Configuration;

namespace DodoBrands.CosmosDbSessionProvider
{
    public static class ConfigHelper
    {
        public static int Get(NameValueCollection config, string key, int defaultValue)
        {
            var value = defaultValue;

            var configValue = config[key];

            if (configValue != null && !int.TryParse(configValue, out value))
            {
                throw new ConfigurationErrorsException("lockTtlSeconds parameter can not be parsed.");
            }

            return value;
        }
    }
}