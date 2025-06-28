namespace ConfluenceSyncService.MSGraphAPI
{
    public static class Authenticate
    {
        static string accessToken;
        static DateTime expiresOn;
        static Authenticate()
        {
            string accessToken = "";
            DateTime expiresOn = DateTime.UtcNow;
        }

        public static void SetAccessToken(string token)
        {
            accessToken = token;
        }

        public static void SetTokenExpiration(DateTime expirationDate)
        {
            expiresOn = expirationDate;
        }

        public static string GetAccessToken()
        {
            return (string)accessToken;
        }

        public static DateTime GetExpiresOn()
        {
            return (DateTime)expiresOn;
        }

    }
}
