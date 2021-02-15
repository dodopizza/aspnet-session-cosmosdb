using System.Security.Cryptography;
using System.Text;

namespace Dodo.AspNet.SessionProviders.CosmosDb
{
    internal static class HashUtil
    {
        public static string CreateHashInHex(string source, int lenBytes)
        {
            using (var sha = new SHA1CryptoServiceProvider())
            {
                var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(source));
                var sb = new StringBuilder();
                for (var i = 0; i < lenBytes && i < hash.Length; i++)
                {
                    sb.Append(hash[i].ToString("X2"));
                }

                return sb.ToString();
            }
        }
    }
}