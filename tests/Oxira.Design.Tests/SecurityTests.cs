using System.Net;
using Oxira.Design.Api.Infrastructure;
using Oxira.Design.Application.Features.Growth;

namespace Oxira.Design.Tests;

public class SecurityTests(TestApp app) : IClassFixture<TestApp>
{
    [Theory]
    [InlineData("162.158.10.20", true)]
    [InlineData("::ffff:104.16.1.1", true)]
    [InlineData("2606:4700::1111", true)]
    [InlineData("8.8.8.8", false)]
    [InlineData("::ffff:8.8.8.8", false)]
    [InlineData("2001:db8::1", false)]
    public void Only_cloudflare_edges_are_trusted(string ip, bool edge) =>
        Assert.Equal(edge, Cloudflare.IsEdge(IPAddress.Parse(ip), Cloudflare.DefaultRanges));

    [Fact]
    public void Null_address_is_not_an_edge() => Assert.False(Cloudflare.IsEdge(null, Cloudflare.DefaultRanges));

    [Fact]
    public async Task A_spoofed_cf_connecting_ip_does_not_buy_more_free_renders()
    {
        // 2 free room renders per IP: a direct caller cannot reset that by inventing addresses
        for (var n = 0; n < 3; n++)
        {
            var c = app.Client();
            c.DefaultRequestHeaders.Add("CF-Connecting-IP", $"203.0.113.{n + 1}");
            var (status, body) = await c.PostJson("/api/redesign/renders", new { sessionId = $"spoof-sess-{n}", image = Http.Jpeg });
            if (n < 2) Assert.Equal(HttpStatusCode.OK, status);
            else
            {
                Assert.Equal(HttpStatusCode.PaymentRequired, status);
                Assert.Equal("credits", body.Str("reason"));
            }
        }
    }

    [Fact]
    public void Legacy_opt_out_hashes_only_inside_the_transition_window()
    {
        var legacy = OptOutLinks.Legacy("a@example.com");
        Assert.True(OptOutLinks.Verify("a@example.com", legacy, "secret-key-secret-key-secret-key", allowLegacy: true));
        Assert.False(OptOutLinks.Verify("a@example.com", legacy, "secret-key-secret-key-secret-key", allowLegacy: false));
        Assert.True(OptOutLinks.Verify("a@example.com", OptOutLinks.Sign("a@example.com", "secret-key-secret-key-secret-key"), "secret-key-secret-key-secret-key", allowLegacy: false));
    }
}
