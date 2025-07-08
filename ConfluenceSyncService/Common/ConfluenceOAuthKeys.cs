namespace ConfluenceSyncService.Common
{
    public static class ConfluenceOAuthKeys
    {
        public static string GetRefreshTokenKey(string profileKey)
            => $"ConfluenceOAuth:Profiles:{profileKey}:RefreshToken";

        public static string GetClientIdKey(string profileKey)
            => $"ConfluenceOAuth:Profiles:{profileKey}:ClientId";

        public static string GetClientSecretKey(string profileKey)
            => $"ConfluenceOAuth:Profiles:{profileKey}:ClientSecret";
    }
}

