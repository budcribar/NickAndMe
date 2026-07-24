using PageToMovie.Core.Options;
using Xunit;

namespace PageToMovie.Tests;

public class AuthOptionsJwtKeyTests
{
    [Fact]
    public void Default_dev_key_is_detected_as_insecure()
    {
        Assert.True(AuthOptions.IsInsecureDefaultJwtSigningKey(AuthOptions.DefaultDevJwtSigningKey));
        Assert.True(AuthOptions.IsInsecureDefaultJwtSigningKey("  " + AuthOptions.DefaultDevJwtSigningKey + "  "));
        Assert.True(AuthOptions.IsInsecureDefaultJwtSigningKey(null));
        Assert.True(AuthOptions.IsInsecureDefaultJwtSigningKey(""));
    }

    [Fact]
    public void Custom_key_is_not_insecure_default()
    {
        Assert.False(AuthOptions.IsInsecureDefaultJwtSigningKey(
            "production-unique-jwt-signing-secret-at-least-32"));
    }
}
