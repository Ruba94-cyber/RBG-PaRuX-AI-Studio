namespace PaRuX.Services
{
    /// <summary>
    /// Public trust anchor for the detached PaRuX GitHub-release manifest signature.
    /// The matching encrypted private key is release-operator material and MUST remain outside
    /// the repository/build tree. Rotating this key requires an intentional application release:
    /// already-installed builds can authenticate only envelopes signed by the key embedded here.
    /// </summary>
    internal static class ReleaseTrustAnchor
    {
        public const string PublicKeyPem = "-----BEGIN PUBLIC KEY-----\nMIIBojANBgkqhkiG9w0BAQEFAAOCAY8AMIIBigKCAYEAtoWa7KelqPwwZf2UdrNF\nFuJCgd3552T65vhr+TfArY6Sc4VwBr8K01ajkS3qY8xNpXRSdzT11bC7wLqGug5I\nh5YGmOZdk2trH8MBipMgOGbsZ4QO/wK4DhPBpwi2PpaQ5dAVEmxQJv3YVkhJmn8X\nqyUT559sLY4U6zmr2eDUVdRlsnFPIumdraDn5cJio9Z8duj48RFsUoEesbKUz3Uj\ndIu42fkopwBKK3smaO+TjLyQnaz1TWKetos7u7kNHIsmq/Op03k1UH/c8FonMP9B\nJeT5WOuAM5vr1y2L960YImV4nB50bAwiBMuPLMilrH9IvF6PoinSuKkHSGTjvhKK\nNdEpMr9tRbzJZFfarCV3FcoYTKOOEzna7fCNCJbeikFbUGrG6MWv3IYnk0Aa4viO\nEIkyTHrrUxduhxwzOX0WnA/afF7TbY1Pug0pzcHyn4WXe2kZJ9l/869meHmjyPAq\n6UOLoaTZg8mdP9vy6+DkxbCDVRg3wzX2BPy8JyORsgAHAgMBAAE=\n-----END PUBLIC KEY-----";

        public static bool IsConfigured =>
            !string.IsNullOrWhiteSpace(PublicKeyPem)
            && PublicKeyPem.Contains("BEGIN PUBLIC KEY", System.StringComparison.Ordinal);
    }
}
