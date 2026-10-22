using System.Net;
using System.Text;
using System.Text.Json;
using EtchaSketch.Core.Pantry;
using EtchaSketch.Data.Pantry;

namespace EtchaSketch.Tests.Pantry;

public sealed class BarcodeTests
{
    [Theory]
    [InlineData("4006381333931", "4006381333931")]   // EAN-13
    [InlineData("4 006381 333931", "4006381333931")] // as printed, with spaces
    [InlineData("73513537", "73513537")]             // EAN-8
    [InlineData("036000291452", "036000291452")]     // UPC-A
    [InlineData("10036000291459", "10036000291459")] // GTIN-14
    public void Real_barcodes_check_out(string text, string digits) => Assert.Equal(digits, Barcode.Normalize(text));

    [Theory]
    [InlineData("4006381333932")] // wrong check digit
    [InlineData("400638133393")]  // wrong length
    [InlineData("40063813339x1")]
    [InlineData("")]
    [InlineData(null)]
    public void Anything_else_does_not(string? text) => Assert.Null(Barcode.Normalize(text));

    [Fact]
    public void A_brand_is_put_in_front_of_the_name_unless_the_name_has_it()
    {
        Assert.Equal("Oatly Oat drink", new ProductInfo("Oat drink", "Oatly", null).DisplayName);
        Assert.Equal("Oatly Barista", new ProductInfo("Oatly Barista", "oatly", null).DisplayName);
        Assert.Equal("Rice", new ProductInfo("Rice", null, null).DisplayName);
    }

    [Fact]
    public async Task A_known_barcode_gives_its_name_brand_and_size()
    {
        var handler = new Answer(HttpStatusCode.OK, """{"code":"4006381333931","product":{"product_name":"Oat drink","brands":"Oatly, Oatly AB","quantity":"1 l"},"status":1}""");
        var lookup = new OpenFoodFacts(new HttpClient(handler) { BaseAddress = OpenFoodFacts.Address });

        var product = await lookup.LookupAsync("4006381333931");

        Assert.Equal(new ProductInfo("Oat drink", "Oatly", "1 l"), product);
        Assert.Equal("https://world.openfoodfacts.org/api/v2/product/4006381333931.json?fields=product_name,brands,quantity", handler.Asked!.ToString());
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, """{"code":"4006381333931","status":0,"status_verbose":"product not found"}""")]
    [InlineData(HttpStatusCode.OK, """{"code":"4006381333931","status":0}""")]
    [InlineData(HttpStatusCode.OK, """{"code":"4006381333931","product":{"product_name":"  "},"status":1}""")]
    public async Task An_unknown_barcode_or_one_without_a_name_gives_nothing(HttpStatusCode code, string body)
    {
        var lookup = new OpenFoodFacts(new HttpClient(new Answer(code, body)) { BaseAddress = OpenFoodFacts.Address });

        Assert.Null(await lookup.LookupAsync("4006381333931"));
    }

    [Fact]
    public async Task Only_the_barcode_is_sent_and_a_bad_one_is_never_sent()
    {
        var handler = new Answer(HttpStatusCode.OK, "{}");
        var lookup = new OpenFoodFacts(new HttpClient(handler) { BaseAddress = OpenFoodFacts.Address });

        await Assert.ThrowsAsync<ArgumentException>(() => lookup.LookupAsync("12345"));
        Assert.Null(handler.Asked);

        Assert.Null(OpenFoodFacts.Read(JsonDocument.Parse("""{"status":1}""").RootElement));
    }

    [Fact]
    public async Task A_server_error_is_thrown_so_the_screen_can_say_it_could_not_reach_it()
    {
        var lookup = new OpenFoodFacts(new HttpClient(new Answer(HttpStatusCode.BadGateway, "")) { BaseAddress = OpenFoodFacts.Address });

        await Assert.ThrowsAsync<HttpRequestException>(() => lookup.LookupAsync("4006381333931"));
    }

    private sealed class Answer(HttpStatusCode code, string body) : HttpMessageHandler
    {
        public Uri? Asked { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Asked = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }
}
